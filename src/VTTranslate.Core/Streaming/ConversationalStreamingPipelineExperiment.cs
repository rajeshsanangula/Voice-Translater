using VTTranslate.Core.Diagnostics;

namespace VTTranslate.Core.Streaming;

/// <summary>
/// EXPERIMENTAL, SHADOW-ONLY — Step 5.13. Orchestrates the FULL conversational path —
/// partial ASR observations → stable semantic segment → bounded-context translation →
/// TTS → isolated test playback — by COMPOSING, never modifying, the already-validated
/// pieces from Steps 2/5.8/5.11/5.5-5.6a and the frozen Step 5.12 TTS interfaces:
/// <see cref="IStreamingStabilityEngine"/> (Step 2's <see cref="PrefixStabilityEngine"/>,
/// unmodified), <see cref="SemanticCompletionHeuristic"/> (Step 5.8, unmodified),
/// the C1/C2 bounded-context CONCEPT from Step 5.11 (reimplemented here against this
/// step's own segment history — Step 5.11's <see cref="ContextWindowTranslationExperiment"/>
/// class itself is not reused, since its "strategy" shape was built around comparing five
/// strategies per step, not driving a single strategy through a live pipeline),
/// <see cref="IIncrementalTranslationProvider"/> (Step 5.5/5.6a, unmodified), and the
/// FROZEN Step 5.12 <see cref="IStreamingTtsProvider"/>/<see cref="ITestPlaybackSink"/>
/// interfaces (read-only reuse — <see cref="AzureStreamingTtsProvider"/> and
/// <see cref="StreamingTtsPipelineExperiment"/> themselves are NOT referenced or modified;
/// Step 5.12's own findings remain UNVALIDATED and nothing here changes that).
///
/// Has no reference to production playback, microphone capture, Google Meet, or any
/// production audio/UI type. Never wired into <c>AzureSpeechTranslationProvider</c>.
///
/// DESIGN DECISIONS made explicit here (see docs/design-notes/conversational-streaming-pipeline-experiment.md
/// for the full rationale/evidence behind each):
/// <list type="bullet">
/// <item><description><b>Segment boundary = stability-committed text that
/// <see cref="SemanticCompletionHeuristic"/> judges complete</b>, measured as the token
/// delta since the previous boundary (not the raw stability-commit delta, which can be
/// smaller than a full semantic unit).</description></item>
/// <item><description><b>Speakability gating is structural, not re-implemented as a
/// revision-counting heuristic.</b> A segment only ever exists here because
/// PrefixStabilityEngine already committed it (its own "never retract a commit"
/// guarantee) — so unlike Steps 5.9-5.11 (which iteratively re-translate a still-moving
/// candidate and must count consecutive non-contradicting results before trusting it),
/// each pipeline segment is translated exactly ONCE and is eligible for TTS the moment
/// that single translation succeeds. Re-deriving Step 5.9's N-consecutive-agreement rule
/// for a value that cannot change would be inventing an unneeded protection, not
/// providing one — documented as a deliberate simplification enabled by the boundary
/// design above, not an oversight.</description></item>
/// <item><description><b>TTS scheduling is single-consumer, strictly sequential.</b>
/// <see cref="DrainAsync"/> processes exactly one queued segment at a time, in FIFO
/// order. This trivially and structurally guarantees per-utterance ordering (concern 7)
/// without a separate reordering buffer, at the DOCUMENTED cost of no synthesis
/// parallelism — a real limitation, not claimed as optimal; see design notes §Ordering
/// and §TTS scheduling.</description></item>
/// <item><description><b>Backpressure policy: drop-oldest-non-final.</b> When the queue
/// is at <see cref="MaxQueueDepth"/>, the oldest NON-final queued segment is evicted to
/// make room. A Final segment is only ever dropped in the edge case where the queue is
/// already saturated entirely with undrained Final segments (a backlog-saturation
/// condition, not the common path) — Final segments are otherwise never dropped, mirroring
/// PrefixStabilityEngine's own "final content is never lost" guarantee.</description></item>
/// <item><description><b>Bounded context (C1/C2) is built from this experiment's own
/// per-session segment history</b> (the SOURCE text of previously enqueued segments,
/// across utterance boundaries within the same session) — an explicit, ASSUMED extension
/// of Step 5.11's within-utterance-only C1/C2 strategies, made specifically to exercise
/// concern 10 (consecutive utterances). Labeled ASSUMED, not PROVEN, in the design
/// notes.</description></item>
/// </list>
/// </summary>
public sealed class ConversationalStreamingPipelineExperiment
{
    private readonly IStreamingStabilityEngine _stability;
    private readonly IIncrementalTranslationProvider _translationProvider;
    private readonly IStreamingTtsProvider _ttsProvider;
    private readonly ITestPlaybackSink _sink;
    private readonly IDiagnosticLogger _logger;
    private readonly string _sessionTag;
    private readonly string _contextStrategy;
    private readonly string _sourceLanguage;
    private readonly string _targetLanguage;
    private readonly string _voiceName;
    private readonly int _maxQueueDepth;

    private readonly List<PipelineSegmentRequest> _queue = new();
    private readonly List<string> _segmentHistory = new();
    private readonly HashSet<(string UtteranceId, int Generation, int SegmentSequence)> _delivered = new();

    private string? _currentUtteranceId;
    private int _lastEmittedBoundaryTokenCount;
    private int _nextSegmentSequence;
    private int _maxObservedQueueDepth;

    public int CurrentGeneration { get; private set; } = 1;
    public int CurrentQueueDepth => _queue.Count;
    public int MaxObservedQueueDepth => _maxObservedQueueDepth;

    public ConversationalStreamingPipelineExperiment(
        IStreamingStabilityEngine stability,
        IIncrementalTranslationProvider translationProvider,
        IStreamingTtsProvider ttsProvider,
        ITestPlaybackSink sink,
        IDiagnosticLogger logger,
        string sessionTag,
        string contextStrategy = "C1",
        string sourceLanguage = "de",
        string targetLanguage = "en",
        string voiceName = "en-US-JennyNeural",
        int maxQueueDepth = 5)
    {
        if (contextStrategy is not ("C1" or "C2" or "C0"))
            throw new ArgumentException("Step 5.13 reuses only C0 (no context, baseline)/C1/C2 — see the freeze on C3/C4.", nameof(contextStrategy));

        _stability = stability;
        _translationProvider = translationProvider;
        _ttsProvider = ttsProvider;
        _sink = sink;
        _logger = logger;
        _sessionTag = sessionTag;
        _contextStrategy = contextStrategy;
        _sourceLanguage = sourceLanguage;
        _targetLanguage = targetLanguage;
        _voiceName = voiceName;
        _maxQueueDepth = maxQueueDepth;
    }

    /// <summary>Bumps the current generation — simulates barge-in/interruption. Any segment already queued or mid-flight under an earlier generation will be rejected as stale, at every checkpoint that re-validates it.</summary>
    public void AdvanceGeneration() => CurrentGeneration++;

    /// <summary>Feeds one partial (Recognizing) event. Returns a newly-queued segment request if this partial pushed the committed SOURCE text past a new semantic boundary, else null.</summary>
    public PipelineSegmentRequest? ObservePartial(PipelineSourcePartialEvent partial, List<PipelineSegmentResult> droppedResults)
    {
        ResetTrackingIfNewUtterance(partial.UtteranceId);

        var stabilityResult = _stability.ProcessPartial(new PartialSourceEvent(
            partial.UtteranceId, partial.SequenceNumber, partial.SourceText, partial.Timestamp));

        if (!stabilityResult.ShouldEmit) return null;

        var completion = SemanticCompletionHeuristic.Evaluate(stabilityResult.CommittedSourceText);
        if (!completion.IsSemanticallyComplete) return null;

        return TryEnqueueBoundary(partial.UtteranceId, partial.Generation, stabilityResult.CommittedSourceText, isFinal: false, partial.Timestamp, droppedResults);
    }

    /// <summary>Feeds one final (Recognized) event. Always enqueues any remaining uncommitted-boundary content as a Final segment, guaranteeing no source content is ever silently lost — mirroring PrefixStabilityEngine's own finalize contract.</summary>
    public PipelineSegmentRequest? ObserveFinal(PipelineSourceFinalEvent final, List<PipelineSegmentResult> droppedResults)
    {
        ResetTrackingIfNewUtterance(final.UtteranceId);

        var stabilityResult = _stability.ProcessFinal(new FinalSourceEvent(
            final.UtteranceId, final.SequenceNumber, final.SourceText, final.Timestamp));

        PipelineSegmentRequest? request = null;
        if (stabilityResult.CommittedSourceText.Length > 0)
        {
            request = TryEnqueueBoundary(final.UtteranceId, final.Generation, stabilityResult.CommittedSourceText, isFinal: true, final.Timestamp, droppedResults);
        }

        // Utterance is over: next ObservePartial/ObserveFinal for a DIFFERENT UtteranceId
        // starts fresh boundary tracking (consecutive-utterance concern) while segment
        // HISTORY (used for bounded context) deliberately persists across the boundary —
        // see the class-level "ASSUMED extension" note.
        _currentUtteranceId = null;
        _lastEmittedBoundaryTokenCount = 0;
        _nextSegmentSequence = 0;

        return request;
    }

    /// <summary>Processes every currently-queued segment, one at a time, in FIFO order, until the queue is empty. Returns the results in processing order.</summary>
    public async Task<List<PipelineSegmentResult>> DrainAsync(CancellationToken ct)
    {
        var results = new List<PipelineSegmentResult>();
        while (_queue.Count > 0)
        {
            var request = _queue[0];
            _queue.RemoveAt(0);
            results.Add(await ProcessSegmentAsync(request, ct));
        }
        return results;
    }

    private void ResetTrackingIfNewUtterance(string utteranceId)
    {
        if (_currentUtteranceId == utteranceId) return;
        _currentUtteranceId = utteranceId;
        _lastEmittedBoundaryTokenCount = 0;
        _nextSegmentSequence = 0;
    }

    private PipelineSegmentRequest? TryEnqueueBoundary(
        string utteranceId, int generation, string committedSourceText, bool isFinal, DateTimeOffset t0, List<PipelineSegmentResult> droppedResults)
    {
        var tokens = committedSourceText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length <= _lastEmittedBoundaryTokenCount)
        {
            // Final called with nothing new beyond the last boundary — nothing to enqueue.
            return isFinal ? null : null;
        }

        var segmentText = string.Join(" ", tokens.Skip(_lastEmittedBoundaryTokenCount));
        _lastEmittedBoundaryTokenCount = tokens.Length;

        var contextText = BuildContext();
        var request = new PipelineSegmentRequest(
            utteranceId, generation, _nextSegmentSequence++, segmentText, contextText, isFinal, t0);

        _segmentHistory.Add(segmentText);
        EnqueueWithBackpressurePolicy(request, droppedResults);
        return request;
    }

    private string BuildContext() => _contextStrategy switch
    {
        "C1" => _segmentHistory.Count >= 1 ? _segmentHistory[^1] : string.Empty,
        "C2" => string.Join(" ", _segmentHistory.TakeLast(2)),
        _ => string.Empty,
    };

    private void EnqueueWithBackpressurePolicy(PipelineSegmentRequest request, List<PipelineSegmentResult> droppedResults)
    {
        if (_queue.Count >= _maxQueueDepth)
        {
            var oldestNonFinalIndex = _queue.FindIndex(r => !r.IsFinalSegment);
            if (oldestNonFinalIndex >= 0)
            {
                var dropped = _queue[oldestNonFinalIndex];
                _queue.RemoveAt(oldestNonFinalIndex);
                droppedResults.Add(BuildDroppedResult(dropped));
                LogOutcome(dropped, PipelineSegmentOutcome.DroppedForBackpressure);
            }
            else if (!request.IsFinalSegment)
            {
                droppedResults.Add(BuildDroppedResult(request));
                LogOutcome(request, PipelineSegmentOutcome.DroppedForBackpressure);
                return; // do not enqueue — the incoming non-final segment itself was dropped
            }
            // else: queue is saturated entirely with undrained Final segments; enqueue
            // anyway, exceeding MaxQueueDepth, rather than lose Final content.
        }

        _queue.Add(request);
        _maxObservedQueueDepth = Math.Max(_maxObservedQueueDepth, _queue.Count);
    }

    private async Task<PipelineSegmentResult> ProcessSegmentAsync(PipelineSegmentRequest request, CancellationToken ct)
    {
        var key = (request.UtteranceId, request.Generation, request.SegmentSequence);

        if (request.Generation != CurrentGeneration)
            return Reject(request, PipelineSegmentOutcome.RejectedStaleGeneration, "generation superseded before processing started");

        if (_delivered.Contains(key))
            return Reject(request, PipelineSegmentOutcome.RejectedDuplicate, "already delivered to test sink");

        var t0 = request.T0SegmentReady;
        var t1 = DateTimeOffset.UtcNow;

        TranslationProviderResult translation;
        try
        {
            translation = await _translationProvider.TranslateAsync(
                new TranslationProviderRequest(_sourceLanguage, _targetLanguage, request.SourceText, NullIfEmpty(request.ContextText)), ct);
        }
        catch (OperationCanceledException)
        {
            return Reject(request, PipelineSegmentOutcome.Cancelled, "cancelled during translation", t0, t1);
        }

        var t2 = DateTimeOffset.UtcNow;

        if (request.Generation != CurrentGeneration)
            return Reject(request, PipelineSegmentOutcome.RejectedStaleGeneration, "generation superseded during translation", t0, t1, t2);

        if (!translation.Success || string.IsNullOrEmpty(translation.CandidateTranslatedText))
            return Reject(request, PipelineSegmentOutcome.TranslationFailed, translation.FailureReason ?? "translation returned no usable text", t0, t1, t2);

        var t3 = DateTimeOffset.UtcNow;
        DateTimeOffset? t4 = null;
        void OnChunk(TtsAudioChunk chunk) => t4 ??= chunk.Timestamp;

        TtsSynthesisResult synthesis;
        try
        {
            synthesis = await _ttsProvider.SynthesizeAsync(translation.CandidateTranslatedText, _targetLanguage, _voiceName, OnChunk, ct);
        }
        catch (OperationCanceledException)
        {
            return Reject(request, PipelineSegmentOutcome.Cancelled, "cancelled during synthesis", t0, t1, t2, t3);
        }

        var t5 = synthesis.CompletedAt ?? DateTimeOffset.UtcNow;

        if (request.Generation != CurrentGeneration)
            return Reject(request, PipelineSegmentOutcome.RejectedStaleGeneration, "generation superseded during synthesis — discarded, never delivered", t0, t1, t2, t3, t4, t5, synthesis.TotalBytes, synthesis.ChunkCount);

        if (!synthesis.Success)
            return Reject(request, PipelineSegmentOutcome.SynthesisFailed, synthesis.FailureReason ?? "synthesis failed", t0, t1, t2, t3, t4, t5, synthesis.TotalBytes, synthesis.ChunkCount);

        var t6 = DateTimeOffset.UtcNow;
        _sink.Enqueue(request.UtteranceId, request.Generation, request.SegmentSequence, synthesis.TotalBytes, t6);
        _delivered.Add(key);

        LogOutcome(request, PipelineSegmentOutcome.DeliveredToTestSink);
        return new PipelineSegmentResult(
            request.UtteranceId, request.Generation, request.SegmentSequence, PipelineSegmentOutcome.DeliveredToTestSink,
            (t1 - t0).TotalMilliseconds, (t2 - t1).TotalMilliseconds, (t3 - t2).TotalMilliseconds,
            t4.HasValue ? (t4.Value - t3).TotalMilliseconds : null,
            t4.HasValue ? (t5 - t4.Value).TotalMilliseconds : null,
            t4.HasValue ? (t4.Value - t0).TotalMilliseconds : null,
            (t6 - t0).TotalMilliseconds,
            synthesis.TotalBytes, synthesis.ChunkCount, null);
    }

    private static string? NullIfEmpty(string s) => string.IsNullOrEmpty(s) ? null : s;

    private PipelineSegmentResult Reject(
        PipelineSegmentRequest request, PipelineSegmentOutcome outcome, string reason,
        DateTimeOffset? t0 = null, DateTimeOffset? t1 = null, DateTimeOffset? t2 = null, DateTimeOffset? t3 = null,
        DateTimeOffset? t4 = null, DateTimeOffset? t5 = null, int totalBytes = 0, int chunkCount = 0)
    {
        LogOutcome(request, outcome);
        return new PipelineSegmentResult(
            request.UtteranceId, request.Generation, request.SegmentSequence, outcome,
            t0.HasValue && t1.HasValue ? (t1.Value - t0.Value).TotalMilliseconds : null,
            t1.HasValue && t2.HasValue ? (t2.Value - t1.Value).TotalMilliseconds : null,
            t2.HasValue && t3.HasValue ? (t3.Value - t2.Value).TotalMilliseconds : null,
            t3.HasValue && t4.HasValue ? (t4.Value - t3.Value).TotalMilliseconds : null,
            t4.HasValue && t5.HasValue ? (t5.Value - t4.Value).TotalMilliseconds : null,
            t0.HasValue && t4.HasValue ? (t4.Value - t0.Value).TotalMilliseconds : null,
            null, totalBytes, chunkCount, reason);
    }

    private PipelineSegmentResult BuildDroppedResult(PipelineSegmentRequest request) =>
        new(request.UtteranceId, request.Generation, request.SegmentSequence, PipelineSegmentOutcome.DroppedForBackpressure,
            null, null, null, null, null, null, null, 0, 0, "evicted from queue — MaxQueueDepth exceeded under fast-speech backpressure");

    private void LogOutcome(PipelineSegmentRequest request, PipelineSegmentOutcome outcome) =>
        _logger.Log(_sessionTag, "PipelineSegmentOutcome",
            $"utteranceId={request.UtteranceId} generation={request.Generation} segmentSequence={request.SegmentSequence} " +
            $"isFinal={request.IsFinalSegment} outcome={outcome} queueDepth={_queue.Count}");
}
