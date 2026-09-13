namespace VTTranslate.Core.Streaming;

/// <summary>
/// EXPERIMENTAL, SHADOW-ONLY — Step 5.13. SOURCE-side state for one committed segment
/// boundary, mirroring (by shape, not by shared type) the SOURCE state machines used in
/// every prior Streaming/ experiment. "SemanticallyComplete" is the state at which the
/// pipeline decides to hand a segment to translation — see
/// <see cref="ConversationalStreamingPipelineExperiment"/>.
/// </summary>
public enum PipelineSourceState { Provisional, SemanticallyComplete, Final }

/// <summary>
/// EXPERIMENTAL, SHADOW-ONLY — Step 5.13. Terminal outcome for one segment's journey
/// through the pipeline. "Dropped*" outcomes exist ONLY for the backpressure policy (see
/// design notes §9) and are never used for a Final segment — Final segments are
/// guaranteed to reach the queue, per the same "never lose final content" contract
/// PrefixStabilityEngine already makes for source text.
/// </summary>
public enum PipelineSegmentOutcome
{
    QueuedForProcessing,
    RejectedStaleGeneration,
    RejectedDuplicate,
    TranslationFailed,
    SynthesisFailed,
    Cancelled,
    DeliveredToTestSink,
    DroppedForBackpressure,
}

/// <summary>One Azure-shaped partial (Recognizing) event fed into the pipeline. Deliberately reuses the field shape of <see cref="PartialSourceEvent"/> plus an explicit <see cref="Generation"/> for barge-in/interruption simulation (Step 5.12's generation-guard pattern, generalized here to the whole pipeline, not just TTS).</summary>
public sealed record PipelineSourcePartialEvent(
    string UtteranceId, int Generation, int SequenceNumber, string SourceText, DateTimeOffset Timestamp);

/// <summary>One Azure-shaped final (Recognized) event fed into the pipeline.</summary>
public sealed record PipelineSourceFinalEvent(
    string UtteranceId, int Generation, int SequenceNumber, string SourceText, DateTimeOffset Timestamp);

/// <summary>
/// One segment queued for translation + TTS after the pipeline decided the committed
/// SOURCE prefix is a semantic boundary (or is the forced-final remainder). Carries its
/// own <see cref="Generation"/> captured at enqueue time — re-checked at every later stage,
/// never re-read from the live counter, so a request created under generation 1 is judged
/// against generation 1 for its entire lifetime even if the pipeline moves on.
/// </summary>
public sealed record PipelineSegmentRequest(
    string UtteranceId,
    int Generation,
    int SegmentSequence,
    string SourceText,
    string ContextText,
    bool IsFinalSegment,
    DateTimeOffset T0SegmentReady);

/// <summary>
/// Full timing/outcome result for one segment. Metadata only — no source, context, or
/// translated/synthesized text content, and audio is represented only as byte/chunk
/// counts. Every timing field is null (never a fabricated zero) whenever that stage was
/// never reached.
/// </summary>
public sealed record PipelineSegmentResult(
    string UtteranceId,
    int Generation,
    int SegmentSequence,
    PipelineSegmentOutcome Outcome,
    double? T0ToT1TranslationStartMs,
    double? T1ToT2TranslationCompleteMs,
    double? T2ToT3TtsStartMs,
    double? T3ToT4FirstAudioMs,
    double? T4ToT5SynthesisCompleteMs,
    double? T0ToT4FirstAudioTotalMs,
    double? T0ToT6DeliveredTotalMs,
    int TotalBytes,
    int ChunkCount,
    string? FailureReason);

/// <summary>Per-utterance summary emitted once an utterance reaches Final and all its queued segments have drained. Null (never fabricated zero) whenever a metric could not be reliably calculated.</summary>
public sealed record PipelineUtteranceSummary(
    string UtteranceId,
    int SegmentCount,
    int DeliveredCount,
    int DroppedForBackpressureCount,
    int RejectedStaleGenerationCount,
    int RejectedDuplicateCount,
    int? ApproxMissingTokenCount,
    int? ApproxDuplicateTokenCount,
    double? EarliestFirstAudioLatencyMs,
    int MaxObservedQueueDepth);
