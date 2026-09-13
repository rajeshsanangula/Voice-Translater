using System.Diagnostics;
using VTTranslate.Core.Diagnostics;

namespace VTTranslate.Core.Streaming;

/// <summary>
/// EXPERIMENTAL, SHADOW-ONLY — Step 5.12. Orchestrates ONE candidate's journey from
/// "classified stable/Speakable" through real TTS synthesis to an ISOLATED, in-memory-only
/// test playback sink — measuring T0 (candidate became Speakable) → T1 (TTS request
/// starts) → T2 (first audio chunk) → T3 (synthesis complete) → T4 (handed to test sink).
///
/// SAFETY RULES, enforced structurally, not just documented:
/// <list type="bullet">
/// <item><description><b>Unstable candidates are never sent to TTS.</b> <see cref="SubmitAsync"/>
/// checks <see cref="TtsPipelineRequest.IsStable"/> FIRST, before any TTS call — an
/// unstable request returns <see cref="TtsPipelineOutcome.RejectedUnstable"/> immediately,
/// with zero interaction with <see cref="IStreamingTtsProvider"/>.</description></item>
/// <item><description><b>Stale generations can never reach the test sink.</b> The
/// generation is re-checked against <see cref="CurrentGeneration"/> both before starting
/// synthesis and again immediately after it completes (synthesis is not instantaneous —
/// a generation bump WHILE synthesis is in flight must still be caught) — a stale result
/// is discarded (<see cref="TtsPipelineOutcome.RejectedStaleGeneration"/>), never handed
/// to <see cref="ITestPlaybackSink"/>.</description></item>
/// <item><description><b>No utterance/sequence is ever delivered to the test sink twice.</b>
/// A (UtteranceId, Generation, SequenceNumber) triple already delivered is rejected on any
/// later resubmission (<see cref="TtsPipelineOutcome.RejectedDuplicate"/>).</description></item>
/// <item><description><b>This experiment prefers silence over incorrect speech</b> — every
/// rejection path above results in NOTHING being sent to the test sink, never a fallback
/// or best-effort emission.</description></item>
/// </list>
///
/// Has no reference to production playback, microphone capture, Google Meet, or any
/// production audio type. Never wired into <c>AzureSpeechTranslationProvider</c>.
/// </summary>
public sealed class StreamingTtsPipelineExperiment
{
    private readonly IStreamingTtsProvider _ttsProvider;
    private readonly ITestPlaybackSink _playbackSink;
    private readonly IDiagnosticLogger _logger;
    private readonly string _sessionTag;

    private readonly HashSet<(string UtteranceId, int Generation, int SequenceNumber)> _delivered = new();

    public int CurrentGeneration { get; private set; } = 1;

    public StreamingTtsPipelineExperiment(
        IStreamingTtsProvider ttsProvider, ITestPlaybackSink playbackSink, IDiagnosticLogger logger, string sessionTag)
    {
        _ttsProvider = ttsProvider;
        _playbackSink = playbackSink;
        _logger = logger;
        _sessionTag = sessionTag;
    }

    /// <summary>Bumps the current generation — simulates a reconnect/reset/new-utterance-superseding-old event. Any in-flight or subsequently-completing synthesis from an earlier generation will be rejected.</summary>
    public void AdvanceGeneration() => CurrentGeneration++;

    public void Reset()
    {
        CurrentGeneration++;
        _delivered.Clear();
    }

    public async Task<TtsPipelineResult> SubmitAsync(TtsPipelineRequest request, CancellationToken ct)
    {
        var key = (request.UtteranceId, request.Generation, request.SequenceNumber);

        if (!request.IsStable)
        {
            LogOutcome(request, TtsPipelineOutcome.RejectedUnstable);
            return BuildResult(request, TtsPipelineOutcome.RejectedUnstable, null, null, null, null, null, 0, 0, "candidate not yet Speakable");
        }

        if (request.Generation != CurrentGeneration)
        {
            LogOutcome(request, TtsPipelineOutcome.RejectedStaleGeneration);
            return BuildResult(request, TtsPipelineOutcome.RejectedStaleGeneration, null, null, null, null, null, 0, 0, "generation superseded before synthesis started");
        }

        if (_delivered.Contains(key))
        {
            LogOutcome(request, TtsPipelineOutcome.RejectedDuplicate);
            return BuildResult(request, TtsPipelineOutcome.RejectedDuplicate, null, null, null, null, null, 0, 0, "already delivered to test sink");
        }

        var t0 = request.T0CandidateBecameSpeakable;
        var t1 = DateTimeOffset.UtcNow;
        DateTimeOffset? t2 = null;

        void OnChunk(TtsAudioChunk chunk) => t2 ??= chunk.Timestamp;

        TtsSynthesisResult synthesisResult;
        try
        {
            synthesisResult = await _ttsProvider.SynthesizeAsync(request.Text, request.TargetLanguage, request.VoiceName, OnChunk, ct);
        }
        catch (OperationCanceledException)
        {
            LogOutcome(request, TtsPipelineOutcome.Cancelled);
            return BuildResult(request, TtsPipelineOutcome.Cancelled, (t1 - t0).TotalMilliseconds, null, null, null, null, 0, 0, "cancelled");
        }

        var t3 = synthesisResult.CompletedAt ?? DateTimeOffset.UtcNow;

        if (ct.IsCancellationRequested)
        {
            LogOutcome(request, TtsPipelineOutcome.Cancelled);
            return BuildResult(request, TtsPipelineOutcome.Cancelled, (t1 - t0).TotalMilliseconds, null, null, null, null,
                synthesisResult.TotalBytes, synthesisResult.ChunkCount, "cancelled");
        }

        if (!synthesisResult.Success)
        {
            LogOutcome(request, TtsPipelineOutcome.SynthesisFailed);
            return BuildResult(request, TtsPipelineOutcome.SynthesisFailed, (t1 - t0).TotalMilliseconds, null, null, null, null,
                synthesisResult.TotalBytes, synthesisResult.ChunkCount, synthesisResult.FailureReason);
        }

        // Re-check generation AFTER synthesis completes — this is the critical guard
        // against a generation bump that occurred WHILE synthesis was in flight.
        if (request.Generation != CurrentGeneration)
        {
            LogOutcome(request, TtsPipelineOutcome.RejectedStaleGeneration);
            return BuildResult(request, TtsPipelineOutcome.RejectedStaleGeneration,
                (t1 - t0).TotalMilliseconds, t2.HasValue ? (t2.Value - t1).TotalMilliseconds : null,
                t2.HasValue ? (t3 - t2.Value).TotalMilliseconds : null, t2.HasValue ? (t2.Value - t0).TotalMilliseconds : null,
                null, synthesisResult.TotalBytes, synthesisResult.ChunkCount, "generation superseded during synthesis — discarded, never delivered");
        }

        var t4 = DateTimeOffset.UtcNow;
        _playbackSink.Enqueue(request.UtteranceId, request.Generation, request.SequenceNumber, synthesisResult.TotalBytes, t4);
        _delivered.Add(key);

        LogOutcome(request, TtsPipelineOutcome.DeliveredToTestSink);
        return BuildResult(request, TtsPipelineOutcome.DeliveredToTestSink,
            (t1 - t0).TotalMilliseconds, t2.HasValue ? (t2.Value - t1).TotalMilliseconds : null,
            t2.HasValue ? (t3 - t2.Value).TotalMilliseconds : null, t2.HasValue ? (t2.Value - t0).TotalMilliseconds : null,
            (t4 - t0).TotalMilliseconds, synthesisResult.TotalBytes, synthesisResult.ChunkCount, null);
    }

    private void LogOutcome(TtsPipelineRequest request, TtsPipelineOutcome outcome) =>
        _logger.Log(_sessionTag, "TtsPipelineOutcome",
            $"utteranceId={request.UtteranceId} generation={request.Generation} sequenceNumber={request.SequenceNumber} " +
            $"isStable={request.IsStable} outcome={outcome}");

    private static TtsPipelineResult BuildResult(
        TtsPipelineRequest request, TtsPipelineOutcome outcome,
        double? t0ToT1, double? t1ToT2, double? t2ToT3, double? t0ToT2, double? t0ToT4,
        int totalBytes, int chunkCount, string? failureReason) =>
        new(request.UtteranceId, request.Generation, request.SequenceNumber, outcome,
            t0ToT1, t1ToT2, t2ToT3, t0ToT2, t0ToT4, totalBytes, chunkCount, failureReason);
}
