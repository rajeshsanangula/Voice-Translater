using VTTranslate.Core.Audio;

namespace VTTranslate.Core.Tests;

/// <summary>Test double for IAudioInputSource — lets tests drive PcmChunkCaptured/CaptureError directly, with no real hardware.</summary>
internal sealed class FakeAudioInputSource : IAudioInputSource
{
    public event EventHandler<byte[]>? PcmChunkCaptured;
    public event EventHandler<Exception>? CaptureError;

    public int StartCallCount { get; private set; }
    public int StopCallCount { get; private set; }
    public bool Disposed { get; private set; }

    public void Start() => StartCallCount++;
    public void Stop() => StopCallCount++;
    public void Dispose() => Disposed = true;

    public void EmitChunk(byte[] chunk) => PcmChunkCaptured?.Invoke(this, chunk);
    public void EmitError(Exception ex) => CaptureError?.Invoke(this, ex);
}
