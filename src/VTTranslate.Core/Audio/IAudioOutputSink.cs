namespace VTTranslate.Core.Audio;

/// <summary>
/// Abstraction over "a place synthesized PCM audio can be played." Production code
/// only ever wires up <see cref="AudioPlaybackSink"/> (a real WASAPI output device).
/// Extracted so <see cref="Session.DirectionPipeline"/> can be unit-tested with a fake
/// sink, mirroring the existing <see cref="IAudioInputSource"/> pattern — minimal
/// extraction, no change to the real playback subsystem's behavior.
///
/// <see cref="PlaybackError"/> exists specifically for device hot-plug resilience: if
/// the output device disappears mid-session (Bluetooth/USB disconnect), this is the
/// only way that failure becomes visible — previously there was no error channel on
/// this interface at all, so a disappearing playback device failed silently.
/// </summary>
public interface IAudioOutputSink : IDisposable
{
    /// <summary>Raised if the underlying device stops unexpectedly (e.g. disconnected) rather than via a normal Stop() call.</summary>
    event EventHandler<Exception>? PlaybackError;

    void Start(int sampleRateHz);
    void EnqueueAudio(byte[] pcm16);
    void Stop();
}
