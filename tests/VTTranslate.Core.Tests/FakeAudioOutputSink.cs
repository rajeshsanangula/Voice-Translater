using VTTranslate.Core.Audio;

namespace VTTranslate.Core.Tests;

/// <summary>Test double for IAudioOutputSink — records what was enqueued instead of touching real hardware.</summary>
internal sealed class FakeAudioOutputSink : IAudioOutputSink
{
    public event EventHandler<Exception>? PlaybackError;

    public List<byte[]> EnqueuedAudio { get; } = new();
    public int StartCallCount { get; private set; }
    public int StopCallCount { get; private set; }
    public bool Disposed { get; private set; }

    public void Start(int sampleRateHz) => StartCallCount++;
    public void EnqueueAudio(byte[] pcm16) => EnqueuedAudio.Add(pcm16);
    public void Stop() => StopCallCount++;
    public void Dispose() => Disposed = true;

    public void EmitPlaybackError(Exception ex) => PlaybackError?.Invoke(this, ex);
}
