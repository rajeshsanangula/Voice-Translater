namespace VTTranslate.Core.Audio;

/// <summary>
/// Paces a capture pump loop to real time using an event signal instead of polling.
///
/// Extracted specifically to fix and regression-test a production defect: a capture
/// pump loop called <c>resampler.Read()</c> in a tight loop, falling back to
/// <c>Thread.Sleep(10)</c> only when <c>Read()</c> returned 0. But the underlying
/// <c>BufferedWaveProvider</c> defaulted to <c>ReadFully = true</c>, which zero-pads
/// every read to the full requested length instead of returning less (or 0) when the
/// real captured-audio queue is empty. That made <c>Read() &gt; 0</c> true on
/// essentially every call, so the sleep branch was dead code — the loop free-ran at
/// CPU speed, emitting silence-padded "audio" to Azure at ~8700x real-time speed
/// whenever the microphone/loopback source was starved. Measured live: ~437,000 reads
/// in 5 seconds instead of the intended ~50.
///
/// The fix has two parts: (1) the caller must set the underlying provider's
/// <c>ReadFully = false</c> so <c>Read()</c> honestly reports "nothing available" as
/// 0 rather than padding; (2) this class replaces the blind poll-sleep with a proper
/// wait on a signal set only when real data actually arrived, bounded by a timeout so
/// shutdown stays responsive even if no more signals ever come.
/// </summary>
public sealed class RealTimeAudioPump : IDisposable
{
    private readonly AutoResetEvent _dataAvailable = new(false);
    private readonly TimeSpan _pollTimeout;

    /// <param name="pollTimeout">
    /// Upper bound on how long <see cref="WaitForData"/> blocks with no signal — this
    /// is a shutdown-responsiveness backstop, not the normal wake path (the normal
    /// path is <see cref="SignalDataAvailable"/> waking it immediately). Defaults to
    /// 50ms, well under any latency budget this app cares about.
    /// </param>
    public RealTimeAudioPump(TimeSpan? pollTimeout = null)
    {
        _pollTimeout = pollTimeout ?? TimeSpan.FromMilliseconds(50);
    }

    /// <summary>Call when the underlying capture source reports real new data (e.g. from a DataAvailable event).</summary>
    public void SignalDataAvailable() => _dataAvailable.Set();

    /// <summary>
    /// Blocks until either new data has been signaled or the poll timeout elapses —
    /// never busy-spins. Safe to call from a single dedicated pump thread.
    /// </summary>
    public void WaitForData() => _dataAvailable.WaitOne(_pollTimeout);

    public void Dispose() => _dataAvailable.Dispose();
}
