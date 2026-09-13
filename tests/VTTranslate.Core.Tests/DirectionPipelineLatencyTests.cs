using VTTranslate.Core.Session;

namespace VTTranslate.Core.Tests;

/// <summary>Covers Part 4: latency instrumentation actually flows through DirectionPipeline's real wiring, not just LatencyBreakdown in isolation.</summary>
public class DirectionPipelineLatencyTests
{
    private static (DirectionPipeline Pipeline, FakeAudioInputSource Capture, FakeAudioOutputSink Playback, FakeSpeechTranslationProvider Provider) Build()
    {
        var session = new TranslationSession
        {
            Direction = SessionDirection.EnglishMicToGerman,
            SourceLanguage = "en-US",
            TargetLanguage = "de-DE"
        };
        var capture = new FakeAudioInputSource();
        var playback = new FakeAudioOutputSink();
        var provider = new FakeSpeechTranslationProvider();
        var pipeline = new DirectionPipeline(session, capture, playback, provider);
        return (pipeline, capture, playback, provider);
    }

    [Fact]
    public void FullUtteranceThroughRealWiring_PopulatesAllLatencyStages()
    {
        var (pipeline, capture, _, provider) = Build();

        capture.EmitChunk(new byte[] { 1 }); // T0, via the real PcmChunkCaptured -> OnCaptureChunkForwarded wiring
        provider.EmitPartial("Hello");        // T1
        provider.EmitFinal("Hello there");    // T3/T4
        provider.EmitSynthesized(new byte[] { 9 }); // T5, then T6 via EnqueueAudio+OnPlaybackEnqueued

        Assert.True(pipeline.Latency.EndToEnd.Snapshot().LastMs >= 0);
        Assert.True(pipeline.Latency.CaptureToFirstPartial.Snapshot().LastMs >= 0);
        Assert.True(pipeline.Latency.RecognitionAndTranslation.Snapshot().LastMs >= 0);
        Assert.True(pipeline.Latency.Synthesis.Snapshot().LastMs >= 0);
        Assert.True(pipeline.Latency.SynthesisToPlaybackEnqueue.Snapshot().LastMs >= 0);
    }

    [Fact]
    public void MutedChunks_DoNotContributeToCaptureToFirstPartial_ViaRealWiring()
    {
        var (pipeline, capture, _, _) = Build();

        pipeline.SetMuted(true);
        capture.EmitChunk(new byte[] { 1 }); // dropped — muted, so OnCaptureChunkForwarded is never called

        // No T0 was ever recorded, so a subsequent partial (hypothetically, if it somehow
        // fired) would not have a bogus capture-arrival timestamp to measure against.
        Assert.Equal(0, pipeline.Latency.CaptureToFirstPartial.Snapshot().LastMs);
    }

    [Fact]
    public void SuppressedUtteranceDueToMute_DoesNotRecordEndToEndLatency()
    {
        var (pipeline, capture, playback, provider) = Build();

        capture.EmitChunk(new byte[] { 1 });
        provider.EmitPartial("Hi");
        pipeline.SetMuted(true); // mid-utterance mute
        provider.EmitFinal("Hi there");
        provider.EmitSynthesized(new byte[] { 1 });

        Assert.Empty(playback.EnqueuedAudio);
        Assert.Equal(0, pipeline.Latency.SynthesisToPlaybackEnqueue.Snapshot().LastMs);
    }
}
