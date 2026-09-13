using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace VTTranslate.Core.Audio;

public enum CaptureKind { Microphone, SystemLoopback }

/// <summary>
/// Captures real audio from a Windows device (microphone or system-loopback) and
/// republishes it as 16 kHz mono 16-bit PCM, which is what the Azure Speech SDK's
/// streaming push format expects. Resampling uses NAudio's MediaFoundationResampler
/// so we never hand the recognizer audio at the wrong rate/format.
///
/// Pacing: the pump loop is throttled by <see cref="RealTimeAudioPump"/>, and
/// <see cref="BufferedWaveProvider.ReadFully"/> is explicitly set to <c>false</c> —
/// see RealTimeAudioPump's doc comment for the production defect this fixes (the
/// default ReadFully=true silently zero-pads reads, defeating any sleep-based
/// throttle and causing the pump to free-run at CPU speed instead of real time).
/// </summary>
public sealed class AudioCaptureSource : IAudioInputSource
{
    private const int TargetSampleRate = 16000;

    private readonly MMDevice _device;
    private readonly CaptureKind _kind;
    private readonly RealTimeAudioPump _pump = new();
    private WasapiCapture? _capture;
    private BufferedWaveProvider? _buffered;
    private MediaFoundationResampler? _resampler;
    private Thread? _pumpThread;
    private volatile bool _running;

    public event EventHandler<byte[]>? PcmChunkCaptured;
    public event EventHandler<Exception>? CaptureError;

    public AudioCaptureSource(string deviceId, CaptureKind kind)
    {
        using var enumerator = new MMDeviceEnumerator();
        _device = enumerator.GetDevice(deviceId);
        _kind = kind;
    }

    public void Start()
    {
        if (_running) return;

        _capture = _kind == CaptureKind.SystemLoopback
            ? new WasapiLoopbackCapture(_device)
            : new WasapiCapture(_device);

        _buffered = new BufferedWaveProvider(_capture.WaveFormat)
        {
            DiscardOnBufferOverflow = true,
            BufferDuration = TimeSpan.FromSeconds(5),
            // Must be false: true (the default) zero-pads every Read() to the full
            // requested length instead of honestly reporting "nothing available yet"
            // as 0. That's what let the pump loop's throttle go dead — see
            // RealTimeAudioPump's doc comment for the full story.
            ReadFully = false
        };

        var targetFormat = new WaveFormat(TargetSampleRate, 16, 1);
        _resampler = new MediaFoundationResampler(_buffered, targetFormat) { ResamplerQuality = 60 };

        _capture.DataAvailable += (_, e) =>
        {
            if (e.BytesRecorded > 0)
            {
                _buffered.AddSamples(e.Buffer, 0, e.BytesRecorded);
                _pump.SignalDataAvailable();
            }
        };
        _capture.RecordingStopped += (_, e) =>
        {
            if (e.Exception != null)
                CaptureError?.Invoke(this, e.Exception);
        };

        _capture.StartRecording();
        _running = true;

        _pumpThread = new Thread(PumpLoop) { IsBackground = true, Name = $"AudioPump-{_kind}" };
        _pumpThread.Start();
    }

    private void PumpLoop()
    {
        var readBuffer = new byte[3200]; // 100ms @ 16kHz/16-bit/mono
        try
        {
            while (_running)
            {
                var read = _resampler!.Read(readBuffer, 0, readBuffer.Length);
                if (read > 0)
                {
                    var chunk = new byte[read];
                    Array.Copy(readBuffer, chunk, read);
                    PcmChunkCaptured?.Invoke(this, chunk);
                }
                else
                {
                    // Genuinely nothing available right now — block until the capture
                    // callback signals real new data (bounded so shutdown stays
                    // responsive), instead of busy-polling.
                    _pump.WaitForData();
                }
            }
        }
        catch (ObjectDisposedException)
        {
            // Stop() disposed the resampler/capture out from under an in-flight Read()
            // call — this is the expected shape of a clean shutdown race, not a fault.
        }
        catch (Exception ex)
        {
            CaptureError?.Invoke(this, ex);
        }
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        // Wake the pump immediately rather than waiting up to _pump's poll timeout —
        // it's blocked in WaitForData() whenever the source is starved.
        _pump.SignalDataAvailable();
        // Give the pump thread a real chance to exit its own loop before we pull the
        // resampler/capture out from under it; the ObjectDisposedException handler
        // above is the backstop if a Read() call was already in flight when we disposed.
        _pumpThread?.Join(TimeSpan.FromSeconds(2));
        _capture?.StopRecording();
        _capture?.Dispose();
        _resampler?.Dispose();
        _capture = null;
    }

    public void Dispose()
    {
        Stop();
        _pump.Dispose();
        _device.Dispose();
    }
}
