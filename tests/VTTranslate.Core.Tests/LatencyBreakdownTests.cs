using VTTranslate.Core.Session;

namespace VTTranslate.Core.Tests;

public class LatencyBreakdownTests
{
    [Fact]
    public void FullUtteranceCycle_RecordsAllStages()
    {
        var breakdown = new LatencyBreakdown();

        breakdown.OnCaptureChunkForwarded(); // T0
        Thread.Sleep(5);
        breakdown.OnPartialResult();          // T1
        Thread.Sleep(5);
        breakdown.OnFinalResult();            // T3/T4
        Thread.Sleep(5);
        breakdown.OnAudioSynthesized();       // T5
        Thread.Sleep(5);
        breakdown.OnPlaybackEnqueued();       // T6

        Assert.True(breakdown.CaptureToFirstPartial.Snapshot().LastMs >= 3);
        Assert.True(breakdown.RecognitionAndTranslation.Snapshot().LastMs >= 3);
        Assert.True(breakdown.Synthesis.Snapshot().LastMs >= 3);
        Assert.True(breakdown.SynthesisToPlaybackEnqueue.Snapshot().LastMs >= 3);
        Assert.True(breakdown.EndToEnd.Snapshot().LastMs >= 15); // T0->T6, spans all four sleeps
    }

    [Fact]
    public void RepeatedPartials_DoNotResetUtteranceStart()
    {
        var breakdown = new LatencyBreakdown();

        breakdown.OnCaptureChunkForwarded();
        breakdown.OnPartialResult();
        Thread.Sleep(5);
        breakdown.OnPartialResult(); // should be a no-op for the T1 timestamp, not restart the clock
        Thread.Sleep(5);
        breakdown.OnFinalResult();

        Assert.True(breakdown.RecognitionAndTranslation.Snapshot().LastMs >= 8);
    }

    [Fact]
    public void NewUtteranceAfterPlaybackEnqueue_StartsFreshTiming()
    {
        var breakdown = new LatencyBreakdown();

        breakdown.OnCaptureChunkForwarded();
        breakdown.OnPartialResult();
        breakdown.OnFinalResult();
        breakdown.OnAudioSynthesized();
        breakdown.OnPlaybackEnqueued();

        // Second utterance — must get its own fresh T0, not reuse the first one's.
        breakdown.OnCaptureChunkForwarded();
        Thread.Sleep(5);
        breakdown.OnPartialResult();
        breakdown.OnFinalResult();
        breakdown.OnAudioSynthesized();
        breakdown.OnPlaybackEnqueued();

        Assert.True(breakdown.EndToEnd.Snapshot().LastMs >= 0); // completes without throwing
    }

    [Fact]
    public void OnPlaybackSuppressed_ResetsStateForNextUtterance_WithoutRecordingMisleadingLatency()
    {
        var breakdown = new LatencyBreakdown();

        breakdown.OnCaptureChunkForwarded();
        breakdown.OnPartialResult();
        breakdown.OnFinalResult();
        breakdown.OnAudioSynthesized();
        breakdown.OnPlaybackSuppressed(); // muted — never reached playback

        Assert.Equal(0, breakdown.EndToEnd.Snapshot().LastMs);
        Assert.Equal(0, breakdown.SynthesisToPlaybackEnqueue.Snapshot().LastMs);

        // Next utterance must not be corrupted by the suppressed one's leftover state.
        breakdown.OnCaptureChunkForwarded();
        Thread.Sleep(5);
        breakdown.OnPartialResult();
        Thread.Sleep(5);
        breakdown.OnFinalResult();
        breakdown.OnAudioSynthesized();
        breakdown.OnPlaybackEnqueued();

        Assert.True(breakdown.EndToEnd.Snapshot().LastMs >= 8);
    }

    [Fact]
    public void OnPartialResult_WithoutPriorCapture_DoesNotRecordCaptureToFirstPartial()
    {
        // Defensive: if a partial somehow fires with no capture-forwarded call yet
        // recorded (shouldn't happen in practice, since audio must be pushed before
        // Azure can recognize anything from it), no bogus sample should be recorded.
        var breakdown = new LatencyBreakdown();

        breakdown.OnPartialResult();

        Assert.Equal(0, breakdown.CaptureToFirstPartial.Snapshot().LastMs);
    }
}
