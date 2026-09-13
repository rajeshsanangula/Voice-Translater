namespace VTTranslate.Core.Session;

/// <summary>
/// Per-stage latency for one translation direction, using the stage model:
///   T0 — first captured audio chunk forwarded for this utterance
///   T1/T2 — first recognition partial (Azure gives us one combined signal for both;
///           there is no separately-observable "speech detected, no text yet" event)
///   T3/T4 — final recognition WITH translation already included (Azure's
///           TranslationRecognizer bundles ASR+MT server-side into one event — T3 and
///           T4 are the same instant in this architecture, not two measurable points)
///   T5 — first synthesized (TTS) audio chunk arrives at the provider
///   T6 — that audio has been handed to the playback sink's EnqueueAudio
///   T7 — the audio device has actually started making sound — NOT measured; this
///        would require querying the audio device's own hardware playback position,
///        which this implementation does not do. Never reported as a number.
///
/// What this class measures is PIPELINE-INTERNAL latency only: from "our code saw the
/// first audio byte for this utterance" to "our code handed synthesized audio to the
/// playback API." It does NOT include: microphone hardware/driver buffering before
/// capture, network transit to/from Azure, Azure's own internal service processing
/// time before it emits an event to us, WASAPI playback buffering, or the acoustic
/// delay from speaker to ear. Real human-perceived latency is pipeline-internal
/// latency PLUS all of those — do not report this class's numbers as "the latency the
/// user experiences."
///
/// Thread-safety: callbacks arrive on Azure SDK worker threads and could in principle
/// overlap (e.g. around a reconnect boundary), so the small piece of mutable state that
/// tracks "is an utterance currently in progress" is guarded by a lock. The underlying
/// <see cref="LatencyTracker"/> instances are independently thread-safe.
/// </summary>
public sealed class LatencyBreakdown
{
    /// <summary>T0 → T1: capture arrival to first recognition partial.</summary>
    public LatencyTracker CaptureToFirstPartial { get; } = new();

    /// <summary>T1 → T3/T4: first partial to final recognition-with-translation (Azure bundles ASR+MT; not further separable).</summary>
    public LatencyTracker RecognitionAndTranslation { get; } = new();

    /// <summary>T3/T4 → T5: final result to first synthesized audio chunk.</summary>
    public LatencyTracker Synthesis { get; } = new();

    /// <summary>T5 → T6: first synthesized chunk to handing it to the playback sink (our own forwarding overhead, expected to be near-zero).</summary>
    public LatencyTracker SynthesisToPlaybackEnqueue { get; } = new();

    /// <summary>T0 → T6: total pipeline-internal latency for one utterance. See class doc comment — this is NOT total human-perceived latency.</summary>
    public LatencyTracker EndToEnd { get; } = new();

    private readonly object _lock = new();
    private DateTimeOffset _captureArrivedAt;   // T0
    private DateTimeOffset _utteranceStartedAt; // T1
    private DateTimeOffset _finalizedAt;        // T3/T4
    private DateTimeOffset _firstSynthesizedAt; // T5
    private bool _utteranceInProgress;
    private bool _awaitingCaptureForNextUtterance = true;

    /// <summary>Call once per forwarded capture chunk (i.e. only when actually pushed to the provider, not while muted).</summary>
    public void OnCaptureChunkForwarded()
    {
        lock (_lock)
        {
            if (_awaitingCaptureForNextUtterance)
            {
                _captureArrivedAt = DateTimeOffset.UtcNow;
                _awaitingCaptureForNextUtterance = false;
            }
        }
    }

    public void OnPartialResult()
    {
        DateTimeOffset now;
        DateTimeOffset captureArrived;
        bool hadCaptureTimestamp;
        lock (_lock)
        {
            now = DateTimeOffset.UtcNow;
            if (!_utteranceInProgress)
            {
                _utteranceStartedAt = now;
                _utteranceInProgress = true;
            }
            captureArrived = _captureArrivedAt;
            hadCaptureTimestamp = !_awaitingCaptureForNextUtterance;
        }

        if (hadCaptureTimestamp)
            CaptureToFirstPartial.Record(now - captureArrived);
    }

    public void OnFinalResult()
    {
        DateTimeOffset started;
        DateTimeOffset now;
        bool wasInProgress;
        lock (_lock)
        {
            now = DateTimeOffset.UtcNow;
            _finalizedAt = now;
            started = _utteranceStartedAt;
            wasInProgress = _utteranceInProgress;
        }
        if (wasInProgress)
            RecognitionAndTranslation.Record(now - started);
    }

    public void OnAudioSynthesized()
    {
        DateTimeOffset now;
        DateTimeOffset finalized;
        lock (_lock)
        {
            now = DateTimeOffset.UtcNow;
            _firstSynthesizedAt = now;
            finalized = _finalizedAt;
        }

        if (finalized != default)
            Synthesis.Record(now - finalized);
    }

    /// <summary>Call right after handing synthesized audio to the playback sink (T6) — completes the utterance's measurement cycle.</summary>
    public void OnPlaybackEnqueued()
    {
        DateTimeOffset now;
        DateTimeOffset synthesizedAt;
        DateTimeOffset captureArrived;
        bool wasInProgress;
        lock (_lock)
        {
            now = DateTimeOffset.UtcNow;
            synthesizedAt = _firstSynthesizedAt;
            captureArrived = _captureArrivedAt;
            wasInProgress = _utteranceInProgress;
            _utteranceInProgress = false;
            _awaitingCaptureForNextUtterance = true; // next utterance gets a fresh T0
        }

        if (synthesizedAt != default)
            SynthesisToPlaybackEnqueue.Record(now - synthesizedAt);
        if (wasInProgress)
            EndToEnd.Record(now - captureArrived);
    }

    /// <summary>
    /// Call instead of <see cref="OnPlaybackEnqueued"/> when synthesized audio for this
    /// utterance was intentionally suppressed (mute, or a rejected UtteranceEligibilityGate
    /// decision) and never reached playback — resets state for the next utterance's T0
    /// without recording a (misleading) playback-latency sample.
    /// </summary>
    public void OnPlaybackSuppressed()
    {
        lock (_lock)
        {
            _utteranceInProgress = false;
            _awaitingCaptureForNextUtterance = true;
        }
    }
}
