namespace VTTranslate.Core.Audio;

/// <summary>
/// Abstraction over "a stream of 16 kHz mono 16-bit PCM audio arriving from somewhere."
/// Production code only ever wires up <see cref="AudioCaptureSource"/> (real microphone
/// or real WASAPI loopback). <see cref="TestFileAudioInputSource"/> exists purely so
/// automated tests can push a real audio file through the exact same pipeline the
/// production app uses, without needing physical microphone acoustics — it is never
/// referenced by the shipped WPF app.
/// </summary>
public interface IAudioInputSource : IDisposable
{
    event EventHandler<byte[]>? PcmChunkCaptured;
    event EventHandler<Exception>? CaptureError;

    void Start();
    void Stop();
}
