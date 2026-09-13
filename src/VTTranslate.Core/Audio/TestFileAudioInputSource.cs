using NAudio.Wave;

namespace VTTranslate.Core.Audio;

/// <summary>
/// Feeds a WAV file into the pipeline through the same <see cref="IAudioInputSource"/>
/// boundary the real microphone/loopback sources use, resampled to 16 kHz mono PCM16
/// exactly like <see cref="AudioCaptureSource"/> does. This exists ONLY for automated
/// testing where physical microphone acoustics aren't available (e.g. an unattended
/// agent has no mouth). It is never wired into the shipped WPF application — see
/// docs/architecture.md and docs/test-methodology.md.
///
/// Chunks are paced out at real-time speed (not dumped instantly) so the provider sees
/// the same streaming cadence it would from a live microphone.
/// </summary>
public sealed class TestFileAudioInputSource : IAudioInputSource
{
    private const int TargetSampleRate = 16000;
    private readonly string _wavPath;
    private Thread? _pumpThread;
    private volatile bool _running;

    public event EventHandler<byte[]>? PcmChunkCaptured;
    public event EventHandler<Exception>? CaptureError;

    /// <summary>Raised once the whole file has been pumped through.</summary>
    public event EventHandler? PlaybackCompleted;

    public TestFileAudioInputSource(string wavPath)
    {
        if (!File.Exists(wavPath))
            throw new FileNotFoundException("Test audio file not found.", wavPath);
        _wavPath = wavPath;
    }

    public void Start()
    {
        if (_running) return;
        _running = true;
        _pumpThread = new Thread(PumpLoop) { IsBackground = true, Name = "TestFileAudioPump" };
        _pumpThread.Start();
    }

    private void PumpLoop()
    {
        try
        {
            using var reader = new AudioFileReader(_wavPath);
            var targetFormat = new WaveFormat(TargetSampleRate, 16, 1);
            using var resampler = new MediaFoundationResampler(reader, targetFormat) { ResamplerQuality = 60 };

            var chunkBytes = 3200; // 100ms @ 16kHz/16-bit/mono, matches AudioCaptureSource's cadence
            var buffer = new byte[chunkBytes];
            int read;
            while (_running && (read = resampler.Read(buffer, 0, buffer.Length)) > 0)
            {
                var chunk = new byte[read];
                Array.Copy(buffer, chunk, read);
                PcmChunkCaptured?.Invoke(this, chunk);
                Thread.Sleep(100); // real-time pacing, matches live capture cadence
            }

            // Trailing silence gives the recognizer room to finalize the last segment.
            var silence = new byte[chunkBytes];
            for (int i = 0; i < 20 && _running; i++)
            {
                PcmChunkCaptured?.Invoke(this, silence);
                Thread.Sleep(100);
            }

            PlaybackCompleted?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            CaptureError?.Invoke(this, ex);
        }
    }

    public void Stop()
    {
        _running = false;
        _pumpThread?.Join(500);
    }

    public void Dispose() => Stop();
}
