namespace VTTranslate.Core.Streaming;

/// <summary>
/// EXPERIMENTAL, SHADOW-ONLY — Step 5.12. Request to synthesize ONE piece of text via TTS.
/// <see cref="IsStable"/> must be true for the pipeline to ever send this to TTS — see
/// <see cref="StreamingTtsPipelineExperiment"/>'s doc comment for the exact rule. This
/// abstraction is isolated by design: it has no reference to production playback,
/// microphone capture, Google Meet, or any production audio type.
/// </summary>
public sealed record TtsPipelineRequest(
    string UtteranceId,
    int Generation,
    int SequenceNumber,
    string Text,
    bool IsStable,
    string TargetLanguage,
    string VoiceName,
    DateTimeOffset T0CandidateBecameSpeakable);

/// <summary>One synthesized audio chunk, as received from the real (or, in unit tests only, fake) TTS provider — metadata only, no text.</summary>
public sealed record TtsAudioChunk(byte[] Data, int SequenceNumber, DateTimeOffset Timestamp);

/// <summary>Result of one <see cref="IStreamingTtsProvider.SynthesizeAsync"/> call.</summary>
public sealed record TtsSynthesisResult(
    bool Success,
    int TotalBytes,
    int ChunkCount,
    string? FailureReason,
    DateTimeOffset? FirstChunkAt,
    DateTimeOffset? CompletedAt);

/// <summary>
/// EXPERIMENTAL, SHADOW-ONLY — Step 5.12. Isolated abstraction over a text-to-speech
/// provider. Supports text input, target language, voice selection, synthesis result, and
/// timing metadata (via the returned <see cref="TtsSynthesisResult"/> and the per-chunk
/// callback) and failure state (<see cref="TtsSynthesisResult.Success"/>/<c>FailureReason</c>).
/// Never coupled to production playback — implementations must not reference
/// <c>IAudioOutputSink</c> or any production audio type.
/// </summary>
public interface IStreamingTtsProvider
{
    /// <param name="onChunk">Invoked once per audio chunk AS IT ARRIVES (for streaming-mode measurement) — never invoked with production-audio side effects.</param>
    Task<TtsSynthesisResult> SynthesizeAsync(string text, string targetLanguage, string voiceName, Action<TtsAudioChunk> onChunk, CancellationToken ct);
}

/// <summary>Outcome state for one pipeline submission — see <see cref="StreamingTtsPipelineExperiment"/>.</summary>
public enum TtsPipelineOutcome { RejectedUnstable, RejectedStaleGeneration, RejectedDuplicate, SynthesisFailed, Cancelled, DeliveredToTestSink }

/// <summary>
/// Full timing/outcome result for one pipeline submission. Metadata only — no source or
/// synthesized-text content, and audio bytes are never included here (only counts).
/// Null (never fabricated zero) whenever a timing point was not reached.
/// </summary>
public sealed record TtsPipelineResult(
    string UtteranceId,
    int Generation,
    int SequenceNumber,
    TtsPipelineOutcome Outcome,
    double? T0ToT1Ms,
    double? T1ToT2Ms,
    double? T2ToT3Ms,
    double? T0ToT2Ms,
    double? T0ToT4Ms,
    int TotalBytes,
    int ChunkCount,
    string? FailureReason);

/// <summary>
/// EXPERIMENTAL, SHADOW-ONLY, ISOLATED TEST SINK — Step 5.12. Records audio hand-off
/// events in memory ONLY. Never plays audio through any real output device, NAudio
/// output, or production <c>IAudioOutputSink</c> — deliberately, so this experiment can
/// never produce physical, audible sound regardless of how it is invoked.
/// </summary>
public interface ITestPlaybackSink
{
    void Enqueue(string utteranceId, int generation, int sequenceNumber, int byteCount, DateTimeOffset t4);
}

/// <summary>Default <see cref="ITestPlaybackSink"/> — records enqueue events in memory for test/diagnostic inspection, never plays audio.</summary>
public sealed class InMemoryTestPlaybackSink : ITestPlaybackSink
{
    public sealed record EnqueueEvent(string UtteranceId, int Generation, int SequenceNumber, int ByteCount, DateTimeOffset T4);

    private readonly List<EnqueueEvent> _events = new();
    public IReadOnlyList<EnqueueEvent> Events => _events;

    public void Enqueue(string utteranceId, int generation, int sequenceNumber, int byteCount, DateTimeOffset t4) =>
        _events.Add(new EnqueueEvent(utteranceId, generation, sequenceNumber, byteCount, t4));
}
