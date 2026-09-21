using System.Threading;
using VTTranslate.Core.Audio;
using VTTranslate.Core.Providers;
using VTTranslate.Core.Diagnostics;

namespace VTTranslate.Core.Session;

/// <summary>
/// Wires one audio input source through one provider to one playback sink, for a single
/// translation direction. Two independent instances (mic->German, remote->English)
/// make up a full bidirectional session; because each owns a distinct capture device
/// and only ever writes to its own playback device, generated audio is never fed
/// back into a capture stream (see docs/architecture.md, "Feedback-loop prevention").
///
/// Mute/PTT (Layer 2 — explicit user-intent control, NOT speaker identification; see
/// docs/design-notes/utterance-gating-and-push-to-talk.md): <see cref="SetMuted"/> is
/// the sole primitive. It gates two independent things:
///  1. Captured audio is simply not forwarded to the provider while muted — the
///     capture device itself (<see cref="IAudioInputSource"/>) is never started/
///     stopped by muting, and the provider never knows mute exists.
///  2. If mute is toggled on WHILE an utterance is already in flight, that utterance's
///     synthesized audio must not reach playback either, without a fragile timing
///     assumption and without risking suppression of the NEXT utterance. This uses a
///     monotonically increasing per-utterance sequence number, incremented exactly
///     once per <see cref="ISpeechTranslationProvider.FinalResult"/> (Azure's own
///     utterance boundary, not a timer): if mute was active at any point during that
///     utterance, its sequence number is recorded, and every
///     <see cref="ISpeechTranslationProvider.AudioSynthesized"/> event for that exact
///     sequence number (there can be several, since TTS can stream in chunks) is
///     dropped — an exact-equality check, not a decrement-once flag, so it can't
///     accidentally consume itself early or bleed into the next utterance once the
///     sequence number advances again.
/// Transcript display is unaffected by mute in both cases — only audio output is gated.
///
/// Device hot-plug resilience: both the capture and playback error channels
/// (<see cref="IAudioInputSource.CaptureError"/>, <see cref="IAudioOutputSink.PlaybackError"/>)
/// are treated as fatal for this direction — a physical device disappearing mid-session
/// (Bluetooth/USB disconnect) is surfaced as a clear error rather than silently doing
/// nothing or attempting to blindly reconnect to a device ID that may no longer refer
/// to the same physical hardware. See docs/design-notes/device-hotplug-resilience.md
/// for why auto-reconnecting to audio hardware is deliberately NOT attempted here.
/// </summary>
public sealed class DirectionPipeline : IAsyncDisposable
{
    // Phase 20 — bundled-TTS observability (observation only; never read by control flow). Metadata only:
    // byte counts and latencies, never audio or text. Null logger = no logging.
    private readonly IDiagnosticLogger? _obsLogger;
    private long _obsFinalTicks;
    private int _obsFirstEnqueueLogged = 1; // 1 = nothing pending (no Final yet)
    private string ObsTag => $"{Session.SourceLanguage}->{Session.TargetLanguage}";

    /// <summary>
    /// Logs, once per utterance (first non-suppressed chunk after a Final), the time from the Final result to the
    /// moment the audio was handed to the playback sink. This is "enqueued to sink", not audible start — the sink
    /// (WASAPI/BufferedWaveProvider) exposes no played-position, so audible start is not measured here.
    /// </summary>
    private void ObserveEnqueue(int bytes)
    {
        if (_obsLogger is null) return;
        if (Interlocked.Exchange(ref _obsFirstEnqueueLogged, 1) != 0) return;
        var finalTicks = Interlocked.Read(ref _obsFinalTicks);
        var ms = finalTicks == 0 ? (double?)null : (DateTimeOffset.UtcNow - new DateTimeOffset(finalTicks, TimeSpan.Zero)).TotalMilliseconds;
        _obsLogger.Log(ObsTag, "PlaybackFirstEnqueue", $"finalToEnqueueMs={(ms.HasValue ? ms.Value.ToString("F0") : "n/a")} bytes={bytes}");
    }

    public TranslationSession Session { get; }
    public LatencyBreakdown Latency { get; } = new();

    private readonly IAudioInputSource _capture;
    private readonly IAudioOutputSink _playback;
    private readonly ISpeechTranslationProvider _provider;

    private volatile bool _isMuted;
    private volatile bool _utteranceInProgress;
    private volatile bool _muteRequestedDuringCurrentUtterance;
    private long _utteranceSequence;
    private long _suppressedUtteranceSequence = -1;

    public event EventHandler<TranslationResult>? Transcript;
    public event EventHandler<ProviderError>? Error;
    public event EventHandler<string>? StatusChanged;

    /// <summary>True when captured audio for this direction is not being forwarded for translation.</summary>
    public bool IsMuted => _isMuted;

    public DirectionPipeline(
        TranslationSession session,
        IAudioInputSource capture,
        IAudioOutputSink playback,
        ISpeechTranslationProvider provider,
        IDiagnosticLogger? logger = null)
    {
        Session = session;
        _capture = capture;
        _playback = playback;
        _provider = provider;
        _obsLogger = logger; // Phase 20: observation only (null = no logging, identical behavior)

        _capture.PcmChunkCaptured += (_, chunk) =>
        {
            if (!_isMuted)
            {
                Latency.OnCaptureChunkForwarded(); // T0
                _provider.PushAudio(chunk);
            }
        };
        _capture.CaptureError += (_, ex) => RaiseError($"Audio capture failed: {ex.Message}", true);
        _playback.PlaybackError += (_, ex) =>
        {
            _obsLogger?.Log(ObsTag, "PlaybackError", $"type={ex.GetType().Name}"); // Phase 20: observation only
            RaiseError($"Audio playback failed: {ex.Message}", true);
        };

        _provider.PartialResult += (_, r) =>
        {
            _utteranceInProgress = true;
            Latency.OnPartialResult(); // T1
            Transcript?.Invoke(this, r);
        };
        _provider.FinalResult += (_, r) =>
        {
            Latency.OnFinalResult(); // T3/T4
            Interlocked.Exchange(ref _obsFinalTicks, DateTimeOffset.UtcNow.Ticks); // Phase 20: observation only
            Interlocked.Exchange(ref _obsFirstEnqueueLogged, 0);

            // Utterance boundary, declared by Azure itself — not a timer. See the
            // class doc comment for why this is the deterministic correlation point.
            var seq = Interlocked.Increment(ref _utteranceSequence);
            if (_muteRequestedDuringCurrentUtterance || _isMuted)
                Interlocked.Exchange(ref _suppressedUtteranceSequence, seq);

            _utteranceInProgress = false;
            _muteRequestedDuringCurrentUtterance = false;

            Transcript?.Invoke(this, r); // transcript visibility is unaffected by mute
        };
        _provider.AudioSynthesized += (_, audio) =>
        {
            Latency.OnAudioSynthesized(); // T5

            // Exact-equality check against the utterance this audio belongs to (per
            // Azure's own sequential event ordering) — not "consume once," so ALL of a
            // suppressed utterance's (possibly multiple, streamed) audio chunks are
            // dropped, and the check stops applying the instant the next utterance's
            // FinalResult advances the sequence again.
            if (Interlocked.Read(ref _suppressedUtteranceSequence) == Interlocked.Read(ref _utteranceSequence))
            {
                Latency.OnPlaybackSuppressed();
                _obsLogger?.Log(ObsTag, "PlaybackSuppressed", $"bytes={audio.Pcm16.Length}"); // Phase 20: observation only
                return;
            }

            _playback.EnqueueAudio(audio.Pcm16);
            ObserveEnqueue(audio.Pcm16.Length); // Phase 20: observation only
            Latency.OnPlaybackEnqueued(); // T6
        };
        _provider.Error += (_, e) => RaiseError(e.Message, e.IsFatal);

        if (_provider is IReconnectingProvider reconnecting)
            reconnecting.StatusChanged += (_, s) => StatusChanged?.Invoke(this, s);
    }

    /// <summary>
    /// Explicit user-intent mute control (Layer 2). Does NOT start/stop the capture
    /// device, does NOT call into the provider's StartAsync/StopAsync/reconnect logic
    /// in any way, and is completely independent per <see cref="DirectionPipeline"/>
    /// instance (i.e. independent per translation direction). This is not speaker
    /// identification — it reflects only whether the user has explicitly indicated
    /// intent to be heard; see docs/design-notes/utterance-gating-and-push-to-talk.md.
    /// </summary>
    public void SetMuted(bool muted)
    {
        if (muted && !_isMuted && _utteranceInProgress)
            _muteRequestedDuringCurrentUtterance = true;

        _isMuted = muted;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        Session.Status = SessionStatus.Starting;
        try
        {
            _playback.Start(16000);
            await _provider.StartAsync(Session.SourceLanguage, Session.TargetLanguage, ct);
            _capture.Start();
            Session.Status = SessionStatus.Running;
            Session.StartedAt = DateTimeOffset.UtcNow;
        }
        catch (Exception ex)
        {
            Session.Status = SessionStatus.Error;
            Session.LastError = ex.Message;
            throw;
        }
    }

    public async Task StopAsync()
    {
        _capture.Stop();
        await _provider.StopAsync();
        _playback.Stop();
        Session.Status = SessionStatus.Stopped;
        Session.StoppedAt = DateTimeOffset.UtcNow;
    }

    private void RaiseError(string message, bool fatal)
    {
        Session.LastError = message;
        if (fatal) Session.Status = SessionStatus.Error;
        Error?.Invoke(this, new ProviderError(message, fatal));
    }

    public async ValueTask DisposeAsync()
    {
        try { await StopAsync(); } catch { /* best-effort */ }
        _capture.Dispose();
        _playback.Dispose();
        await _provider.DisposeAsync();
    }
}
