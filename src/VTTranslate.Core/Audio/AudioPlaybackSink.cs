using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace VTTranslate.Core.Audio;

/// <summary>Plays synthesized PCM speech to a chosen real output device (speaker or virtual mic).</summary>
public sealed class AudioPlaybackSink : IAudioOutputSink
{
    private readonly MMDevice _device;
    private WasapiOut? _output;
    private BufferedWaveProvider? _buffer;
    private volatile bool _stopRequested;

    public event EventHandler<Exception>? PlaybackError;

    public AudioPlaybackSink(string deviceId)
    {
        using var enumerator = new MMDeviceEnumerator();
        _device = enumerator.GetDevice(deviceId);
    }

    public void Start(int sampleRateHz)
    {
        _stopRequested = false;
        var format = new WaveFormat(sampleRateHz, 16, 1);
        _buffer = new BufferedWaveProvider(format)
        {
            DiscardOnBufferOverflow = true,
            BufferDuration = TimeSpan.FromSeconds(10)
        };
        _output = new WasapiOut(_device, AudioClientShareMode.Shared, true, 50);
        _output.PlaybackStopped += OnPlaybackStopped;
        _output.Init(_buffer);
        _output.Play();
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        // A normal Stop() call also raises this event (with Exception == null) — only
        // an unexpected stop (device disappeared, driver error, etc.) is a real error.
        if (_stopRequested) return;
        if (e.Exception != null)
            PlaybackError?.Invoke(this, e.Exception);
    }

    public void EnqueueAudio(byte[] pcm16) => _buffer?.AddSamples(pcm16, 0, pcm16.Length);

    public void Stop()
    {
        _stopRequested = true;
        if (_output != null) _output.PlaybackStopped -= OnPlaybackStopped;
        _output?.Stop();
        _output?.Dispose();
        _output = null;
    }

    public void Dispose()
    {
        Stop();
        _device.Dispose();
    }
}
