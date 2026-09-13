using System.Diagnostics;
using VTTranslate.Core.Audio;

namespace VTTranslate.Core.Tests;

/// <summary>
/// Regression tests for the production defect described in RealTimeAudioPump's doc
/// comment: a capture pump loop that free-ran at CPU speed (measured live: ~437,000
/// reads in 5 seconds, ~87,500/sec) instead of pacing itself to real time whenever the
/// audio source was starved. These tests prove the fixed wait-based pacing genuinely
/// blocks when starved and wakes promptly when signaled — the two properties whose
/// absence caused the bug.
/// </summary>
public class RealTimeAudioPumpTests
{
    [Fact]
    public void SimulatedPumpLoop_DoesNotSpin_WhenSourceIsAlwaysStarved()
    {
        // Mirrors AudioCaptureSource.PumpLoop's exact shape: attempt a read (here,
        // always "0 available"), and if nothing came back, wait for the pump before
        // trying again. A correctly-paced loop bounded by a 20ms poll timeout run for
        // ~200ms should attempt on the order of 200/20 = ~10 reads. The pre-fix defect
        // produced roughly 17,500 read attempts in this same window (487,000/sec observed
        // live, scaled to 200ms) — three orders of magnitude more.
        using var pump = new RealTimeAudioPump(TimeSpan.FromMilliseconds(20));
        var readAttempts = 0;
        var sw = Stopwatch.StartNew();

        while (sw.ElapsedMilliseconds < 200)
        {
            const int simulatedRead = 0; // source is starved — this is the "read > 0" check from PumpLoop
            if (simulatedRead > 0)
            {
                // would emit a chunk here
            }
            else
            {
                pump.WaitForData();
            }
            readAttempts++;
        }

        Assert.InRange(readAttempts, 1, 40);
    }

    [Fact]
    public void WaitForData_WakesPromptly_WhenSignaled_RatherThanWaitingOutTheFullTimeout()
    {
        // Timeout is deliberately long (2s) so a prompt wake can only be explained by
        // the signal actually working, not by the bounded-wait timeout coincidentally
        // elapsing first.
        using var pump = new RealTimeAudioPump(TimeSpan.FromSeconds(2));
        var sw = Stopwatch.StartNew();

        var signalThread = new Thread(() =>
        {
            Thread.Sleep(30);
            pump.SignalDataAvailable();
        });
        signalThread.Start();

        pump.WaitForData();
        sw.Stop();
        signalThread.Join();

        Assert.True(sw.ElapsedMilliseconds < 500, $"Expected a prompt wake well under the 2s timeout, took {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public void WaitForData_ReturnsAfterPollTimeout_WhenNeverSignaled()
    {
        // Confirms the shutdown-responsiveness backstop: even with zero signals ever
        // sent, a single WaitForData() call returns (doesn't block forever) once its
        // poll timeout elapses.
        using var pump = new RealTimeAudioPump(TimeSpan.FromMilliseconds(50));
        var sw = Stopwatch.StartNew();

        pump.WaitForData();
        sw.Stop();

        Assert.InRange(sw.ElapsedMilliseconds, 30, 500);
    }

    [Fact]
    public void SignalDataAvailable_BeforeWait_IsNotLost()
    {
        // AutoResetEvent semantics: a signal sent before anyone waits should still be
        // observed by the next WaitForData() call (returns promptly, not after the
        // full timeout) — this is what makes Stop()'s "wake the pump immediately"
        // call correct even if the pump thread hasn't reached WaitForData() yet.
        using var pump = new RealTimeAudioPump(TimeSpan.FromSeconds(2));
        pump.SignalDataAvailable();

        var sw = Stopwatch.StartNew();
        pump.WaitForData();
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 200, $"Expected the pre-set signal to be observed immediately, took {sw.ElapsedMilliseconds}ms");
    }
}
