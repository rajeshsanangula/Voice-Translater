using VTTranslate.Core.Session;

namespace VTTranslate.Core.Tests;

/// <summary>
/// Covers Layer 2 (explicit user-intent mute/PTT) end-to-end at the DirectionPipeline
/// level using fully synchronous, deterministic fakes — no timing/sleep anywhere,
/// which itself demonstrates the mid-utterance correlation mechanism is event-count
/// based, not timing-based (see DirectionPipeline's class doc comment).
/// </summary>
public class DirectionPipelineMuteTests
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
    public void DefaultState_IsUnmuted()
    {
        var (pipeline, capture, _, provider) = Build();

        Assert.False(pipeline.IsMuted);
        capture.EmitChunk(new byte[] { 1, 2, 3, 4 });

        Assert.Single(provider.PushedAudio);
    }

    [Fact]
    public void Muted_ZeroPushAudioCalls()
    {
        var (pipeline, capture, _, provider) = Build();

        pipeline.SetMuted(true);
        capture.EmitChunk(new byte[] { 1, 2, 3, 4 });

        Assert.Empty(provider.PushedAudio);
    }

    [Fact]
    public void Unmute_ResumesForwarding()
    {
        var (pipeline, capture, _, provider) = Build();

        pipeline.SetMuted(true);
        capture.EmitChunk(new byte[] { 1 });
        pipeline.SetMuted(false);
        capture.EmitChunk(new byte[] { 2 });

        Assert.Single(provider.PushedAudio);
        Assert.Equal(2, provider.PushedAudio[0][0]);
    }

    [Fact]
    public void MuteUnmute_Repeatedly_OnlyForwardsWhileUnmuted()
    {
        var (pipeline, capture, _, provider) = Build();
        var forwardedCount = 0;

        for (byte i = 0; i < 20; i++)
        {
            pipeline.SetMuted(i % 2 == 0); // muted on even, unmuted on odd
            capture.EmitChunk(new byte[] { i });
            if (i % 2 != 0) forwardedCount++;
        }

        Assert.Equal(forwardedCount, provider.PushedAudio.Count);
    }

    [Fact]
    public void MuteDuringActiveUtterance_SuppressesThatUtterancesAudio()
    {
        var (pipeline, _, playback, provider) = Build();

        provider.EmitPartial("I would");           // utterance now in progress
        pipeline.SetMuted(true);                    // muted mid-utterance
        provider.EmitFinal("I would like coffee");  // utterance concludes while flagged
        provider.EmitSynthesized(new byte[] { 9 }); // this utterance's audio

        Assert.Empty(playback.EnqueuedAudio);
    }

    [Fact]
    public void NoPostMuteSynthesizedAudioLeakage_AllChunksOfSuppressedUtteranceAreDropped()
    {
        // Azure can stream TTS in multiple chunks per utterance — every one of them
        // for the suppressed utterance must be dropped, not just the first.
        var (pipeline, _, playback, provider) = Build();

        provider.EmitPartial("Good");
        pipeline.SetMuted(true);
        provider.EmitFinal("Good morning everyone");
        provider.EmitSynthesized(new byte[] { 1 });
        provider.EmitSynthesized(new byte[] { 2 });
        provider.EmitSynthesized(new byte[] { 3 });

        Assert.Empty(playback.EnqueuedAudio);
    }

    [Fact]
    public void NoSuppressionLeakage_IntoNextUtteranceAfterUnmute()
    {
        var (pipeline, _, playback, provider) = Build();

        // Utterance 1: muted mid-utterance — suppressed.
        provider.EmitPartial("First");
        pipeline.SetMuted(true);
        provider.EmitFinal("First utterance");
        provider.EmitSynthesized(new byte[] { 1 });

        // Unmute, then a full second utterance — must NOT be suppressed.
        pipeline.SetMuted(false);
        provider.EmitPartial("Second");
        provider.EmitFinal("Second utterance");
        provider.EmitSynthesized(new byte[] { 2 });

        Assert.Single(playback.EnqueuedAudio);
        Assert.Equal(2, playback.EnqueuedAudio[0][0]);
    }

    [Fact]
    public void Transcript_RemainsVisible_EvenWhenAudioIsSuppressedByMute()
    {
        var (pipeline, _, playback, provider) = Build();
        var transcriptEvents = new List<string>();
        pipeline.Transcript += (_, r) => transcriptEvents.Add(r.SourceText);

        provider.EmitPartial("Hello");
        pipeline.SetMuted(true);
        provider.EmitFinal("Hello there");
        provider.EmitSynthesized(new byte[] { 1 });

        Assert.Equal(new[] { "Hello", "Hello there" }, transcriptEvents);
        Assert.Empty(playback.EnqueuedAudio); // audio still suppressed
    }

    [Fact]
    public void Mute_NeverCallsStartAsyncOrStopAsync()
    {
        var (pipeline, _, _, provider) = Build();

        pipeline.SetMuted(true);
        pipeline.SetMuted(false);
        pipeline.SetMuted(true);

        Assert.Equal(0, provider.StartAsyncCallCount);
        Assert.Equal(0, provider.StopAsyncCallCount);
    }

    [Fact]
    public void Mute_NeverTouchesProviderGenerationOrReconnectLogic()
    {
        // Generation/reconnect only ever change inside CreateAndStartRecognizerLockedAsync,
        // which is only reachable via StartAsync or the provider's own internal reconnect —
        // proving mute never calls StartAsync (above) transitively proves it can't affect
        // generation either, since DirectionPipeline has no other path to the provider's
        // connection lifecycle.
        var (pipeline, _, _, provider) = Build();

        for (int i = 0; i < 10; i++) pipeline.SetMuted(i % 2 == 0);

        Assert.Equal(0, provider.StartAsyncCallCount);
        Assert.Equal(0, provider.StopAsyncCallCount);
        Assert.Equal(0, provider.DisposeAsyncCallCount);
    }

    [Fact]
    public void ReconnectWhileMuted_MuteStateUnaffected()
    {
        var (pipeline, _, _, provider) = Build();
        var statusEvents = new List<string>();
        pipeline.StatusChanged += (_, s) => statusEvents.Add(s);

        pipeline.SetMuted(true);
        provider.EmitStatusChanged("Connection lost, reconnecting (1/5)");
        provider.EmitStatusChanged("Connected");

        Assert.True(pipeline.IsMuted);
        Assert.Equal(2, statusEvents.Count);
    }

    [Fact]
    public void ReconnectWhileUnmuted_UnmuteStateUnaffected()
    {
        var (pipeline, capture, _, provider) = Build();

        provider.EmitStatusChanged("Connection lost, reconnecting (1/5)");
        provider.EmitStatusChanged("Connected");
        capture.EmitChunk(new byte[] { 5 });

        Assert.False(pipeline.IsMuted);
        Assert.Single(provider.PushedAudio);
    }

    [Fact]
    public async Task StopWhileMuted_FullyTearsDownRegardlessOfMuteState()
    {
        var (pipeline, capture, playback, provider) = Build();
        pipeline.SetMuted(true);

        await pipeline.StopAsync();

        Assert.Equal(1, capture.StopCallCount);
        Assert.Equal(1, provider.StopAsyncCallCount);
        Assert.Equal(1, playback.StopCallCount);
    }

    [Fact]
    public async Task StopWhileUnmuted_FullyTearsDown()
    {
        var (pipeline, capture, playback, provider) = Build();

        await pipeline.StopAsync();

        Assert.Equal(1, capture.StopCallCount);
        Assert.Equal(1, provider.StopAsyncCallCount);
        Assert.Equal(1, playback.StopCallCount);
    }

    [Fact]
    public void TwoIndependentPipelineInstances_MutingOneDoesNotAffectTheOther()
    {
        var (pipelineA, captureA, _, providerA) = Build();
        var (pipelineB, captureB, _, providerB) = Build();

        pipelineA.SetMuted(true);
        captureA.EmitChunk(new byte[] { 1 });
        captureB.EmitChunk(new byte[] { 2 });

        Assert.Empty(providerA.PushedAudio);
        Assert.Single(providerB.PushedAudio);
        Assert.False(pipelineB.IsMuted);
    }

    [Fact]
    public void RapidToggleStressTest_NoExceptionsNoDuplicates()
    {
        var (pipeline, capture, playback, provider) = Build();
        var exception = Record.Exception(() =>
        {
            for (int i = 0; i < 500; i++)
            {
                pipeline.SetMuted(i % 3 == 0);
                capture.EmitChunk(new byte[] { (byte)(i % 256) });
                if (i % 50 == 0)
                {
                    provider.EmitPartial("x");
                    provider.EmitFinal("x y");
                    provider.EmitSynthesized(new byte[] { 1 });
                }
            }
        });

        Assert.Null(exception);
        // Every pushed chunk and every enqueued audio entry must be a single, distinct
        // add — no event handler fired twice for one emission.
        Assert.True(provider.PushedAudio.Count <= 500);
        Assert.True(playback.EnqueuedAudio.Count <= 10);
    }

    [Fact]
    public void NoDuplicatePushAudioOrPlaybackEvents_ForASingleEmission()
    {
        var (pipeline, capture, playback, provider) = Build();

        capture.EmitChunk(new byte[] { 42 });
        provider.EmitSynthesized(new byte[] { 7 });

        Assert.Single(provider.PushedAudio);
        Assert.Single(playback.EnqueuedAudio);
    }

    [Fact]
    public async Task CancellationAndShutdown_DisposesCleanlyRegardlessOfMuteState()
    {
        var (pipeline, capture, playback, provider) = Build();
        pipeline.SetMuted(true);

        await pipeline.DisposeAsync();

        Assert.True(capture.Disposed);
        Assert.True(playback.Disposed);
        Assert.Equal(1, provider.DisposeAsyncCallCount);
    }
}
