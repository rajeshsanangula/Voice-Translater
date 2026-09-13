using VTTranslate.Core.Streaming;

namespace VTTranslate.Core.Tests;

internal sealed class FakeStreamingTtsProvider : IStreamingTtsProvider
{
    public List<(string Text, string Lang, string Voice)> Calls { get; } = new();
    public Func<string, TtsSynthesisResult>? ResponseFactory { get; set; }
    public int ChunkCountToEmit { get; set; } = 3;
    public int BytesPerChunk { get; set; } = 100;
    public TimeSpan DelayBeforeFirstChunk { get; set; } = TimeSpan.Zero;
    public bool ThrowOperationCanceled { get; set; }

    public async Task<TtsSynthesisResult> SynthesizeAsync(string text, string targetLanguage, string voiceName, Action<TtsAudioChunk> onChunk, CancellationToken ct)
    {
        Calls.Add((text, targetLanguage, voiceName));

        if (ThrowOperationCanceled) throw new OperationCanceledException();

        if (ResponseFactory != null)
            return ResponseFactory(text);

        if (DelayBeforeFirstChunk > TimeSpan.Zero)
            await Task.Delay(DelayBeforeFirstChunk, ct);

        var totalBytes = 0;
        DateTimeOffset? firstChunkAt = null;
        for (var i = 1; i <= ChunkCountToEmit; i++)
        {
            ct.ThrowIfCancellationRequested();
            var chunk = new TtsAudioChunk(new byte[BytesPerChunk], i, DateTimeOffset.UtcNow);
            firstChunkAt ??= chunk.Timestamp;
            totalBytes += BytesPerChunk;
            onChunk(chunk);
        }

        return new TtsSynthesisResult(true, totalBytes, ChunkCountToEmit, null, firstChunkAt, DateTimeOffset.UtcNow);
    }
}

public class StreamingTtsPipelineExperimentTests
{
    private static DateTimeOffset T(int ms) => DateTimeOffset.UnixEpoch.AddMilliseconds(ms);

    private static StreamingTtsPipelineExperiment NewPipeline(
        FakeStreamingTtsProvider provider, out InMemoryTestPlaybackSink sink, out InMemoryDiagnosticLogger logger)
    {
        sink = new InMemoryTestPlaybackSink();
        logger = new InMemoryDiagnosticLogger();
        return new StreamingTtsPipelineExperiment(provider, sink, logger, "en-US->de-DE");
    }

    private static TtsPipelineRequest Req(string uttId, int gen, int seq, bool stable, DateTimeOffset t0, string text = "Hallo") =>
        new(uttId, gen, seq, text, stable, "de-DE", "de-DE-KatjaNeural", t0);

    // ---- First audio chunk / full synthesis ----
    [Fact]
    public async Task StableCandidate_SynthesizesAndDeliversToTestSink()
    {
        var provider = new FakeStreamingTtsProvider();
        var pipeline = NewPipeline(provider, out var sink, out _);

        var result = await pipeline.SubmitAsync(Req("u1", 1, 1, true, T(0)), CancellationToken.None);

        Assert.Equal(TtsPipelineOutcome.DeliveredToTestSink, result.Outcome);
        Assert.Single(sink.Events);
        Assert.NotNull(result.T0ToT2Ms); // first-chunk timing was captured
        Assert.NotNull(result.T0ToT4Ms);
        Assert.True(result.TotalBytes > 0);
        Assert.True(result.ChunkCount > 0);
    }

    // ---- Unstable draft is never sent to TTS ----
    [Fact]
    public async Task UnstableCandidate_NeverCallsTts_NeverReachesSink()
    {
        var provider = new FakeStreamingTtsProvider();
        var pipeline = NewPipeline(provider, out var sink, out _);

        var result = await pipeline.SubmitAsync(Req("u1", 1, 1, stable: false, T(0)), CancellationToken.None);

        Assert.Equal(TtsPipelineOutcome.RejectedUnstable, result.Outcome);
        Assert.Empty(provider.Calls); // TTS was never even invoked
        Assert.Empty(sink.Events);
    }

    // ---- Cancellation: stop before first audio ----
    [Fact]
    public async Task Cancellation_BeforeFirstAudio_NeverReachesSink()
    {
        var provider = new FakeStreamingTtsProvider { DelayBeforeFirstChunk = TimeSpan.FromSeconds(5) };
        var pipeline = NewPipeline(provider, out var sink, out _);
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(50));

        var result = await pipeline.SubmitAsync(Req("u1", 1, 1, true, T(0)), cts.Token);

        Assert.Equal(TtsPipelineOutcome.Cancelled, result.Outcome);
        Assert.Empty(sink.Events);
    }

    // ---- Timeout / failure ----
    [Fact]
    public async Task SynthesisFailure_NeverReachesSink_ReasonReported()
    {
        var provider = new FakeStreamingTtsProvider
        {
            ResponseFactory = _ => new TtsSynthesisResult(false, 0, 0, "HTTP 503", null, DateTimeOffset.UtcNow),
        };
        var pipeline = NewPipeline(provider, out var sink, out _);

        var result = await pipeline.SubmitAsync(Req("u1", 1, 1, true, T(0)), CancellationToken.None);

        Assert.Equal(TtsPipelineOutcome.SynthesisFailed, result.Outcome);
        Assert.Empty(sink.Events);
        Assert.Equal("HTTP 503", result.FailureReason);
    }

    // ---- Empty audio ----
    [Fact]
    public async Task EmptySynthesis_TreatedAsFailure_NeverReachesSink()
    {
        var provider = new FakeStreamingTtsProvider
        {
            ResponseFactory = _ => new TtsSynthesisResult(false, 0, 0, "Empty synthesis", null, DateTimeOffset.UtcNow),
        };
        var pipeline = NewPipeline(provider, out var sink, out _);

        var result = await pipeline.SubmitAsync(Req("u1", 1, 1, true, T(0)), CancellationToken.None);

        Assert.Equal(TtsPipelineOutcome.SynthesisFailed, result.Outcome);
        Assert.Empty(sink.Events);
    }

    // ---- Stale generation: revision safety — B revises A before A is spoken ----
    [Fact]
    public async Task StaleGeneration_DiscardedAfterSynthesisCompletes_NeverReachesSink()
    {
        var provider = new FakeStreamingTtsProvider();
        var pipeline = NewPipeline(provider, out var sink, out _);

        // Simulate: candidate A's synthesis is submitted for generation 1, but by the time
        // it "completes" (we bump the generation mid-flight via a custom provider hook),
        // the pipeline has already moved to generation 2 (B superseded A). Since our fake
        // provider is synchronous/fast, we bump the generation BEFORE awaiting by using a
        // provider whose ResponseFactory bumps generation as a side effect, simulating a
        // generation change that happens concurrently with synthesis.
        var providerThatBumpsGeneration = new FakeStreamingTtsProvider();
        var pipeline2 = NewPipeline(providerThatBumpsGeneration, out var sink2, out _);
        providerThatBumpsGeneration.ResponseFactory = _ =>
        {
            pipeline2.AdvanceGeneration(); // generation changes WHILE this synthesis is "in flight"
            return new TtsSynthesisResult(true, 300, 3, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        };

        var result = await pipeline2.SubmitAsync(Req("u1", 1, 1, true, T(0)), CancellationToken.None);

        Assert.Equal(TtsPipelineOutcome.RejectedStaleGeneration, result.Outcome);
        Assert.Empty(sink2.Events); // stale synthesis NEVER reached the test sink, despite succeeding
    }

    [Fact]
    public async Task StaleGeneration_RejectedBeforeSynthesis_WhenAlreadyStaleAtSubmission()
    {
        var provider = new FakeStreamingTtsProvider();
        var pipeline = NewPipeline(provider, out var sink, out _);
        pipeline.AdvanceGeneration(); // now generation 2

        var result = await pipeline.SubmitAsync(Req("u1", 1, 1, true, T(0)), CancellationToken.None); // request still says generation 1

        Assert.Equal(TtsPipelineOutcome.RejectedStaleGeneration, result.Outcome);
        Assert.Empty(provider.Calls); // never even attempted synthesis for a request already known stale
        Assert.Empty(sink.Events);
    }

    // ---- Consecutive chunks: ordering, no duplicates ----
    [Fact]
    public async Task ConsecutiveChunks_DeliveredInOrder_NoDuplicates()
    {
        var provider = new FakeStreamingTtsProvider();
        var pipeline = NewPipeline(provider, out var sink, out _);

        await pipeline.SubmitAsync(Req("u1", 1, 1, true, T(0), "Chunk one"), CancellationToken.None);
        await pipeline.SubmitAsync(Req("u1", 1, 2, true, T(100), "Chunk two"), CancellationToken.None);
        await pipeline.SubmitAsync(Req("u1", 1, 3, true, T(200), "Chunk three"), CancellationToken.None);

        Assert.Equal(3, sink.Events.Count);
        Assert.Equal(new[] { 1, 2, 3 }, sink.Events.Select(e => e.SequenceNumber));
    }

    // ---- Duplicate prevention: same (utterance, generation, sequence) submitted twice ----
    [Fact]
    public async Task DuplicateSubmission_RejectedOnSecondAttempt_NeverDoubleDelivered()
    {
        var provider = new FakeStreamingTtsProvider();
        var pipeline = NewPipeline(provider, out var sink, out _);

        var first = await pipeline.SubmitAsync(Req("u1", 1, 1, true, T(0)), CancellationToken.None);
        var second = await pipeline.SubmitAsync(Req("u1", 1, 1, true, T(0)), CancellationToken.None); // identical key resubmitted

        Assert.Equal(TtsPipelineOutcome.DeliveredToTestSink, first.Outcome);
        Assert.Equal(TtsPipelineOutcome.RejectedDuplicate, second.Outcome);
        Assert.Single(sink.Events); // only ONE playback enqueue, never two
    }

    // ---- Stop / reset: stale chunks rejected after reset ----
    [Fact]
    public async Task Reset_RejectsSubsequentRequestsFromThePreResetGeneration()
    {
        var provider = new FakeStreamingTtsProvider();
        var pipeline = NewPipeline(provider, out var sink, out _);

        var beforeReset = await pipeline.SubmitAsync(Req("u1", 1, 1, true, T(0)), CancellationToken.None);
        Assert.Equal(TtsPipelineOutcome.DeliveredToTestSink, beforeReset.Outcome);

        pipeline.Reset();

        var afterReset = await pipeline.SubmitAsync(Req("u1", 1, 2, true, T(100)), CancellationToken.None); // still generation 1 — now stale
        Assert.Equal(TtsPipelineOutcome.RejectedStaleGeneration, afterReset.Outcome);
        Assert.Single(sink.Events); // still just the one from before reset
    }

    [Fact]
    public async Task Reset_EvenAResubmissionOfAnAlreadyDeliveredKeyNoLongerMatters_StaleGenerationCaughtFirst()
    {
        var provider = new FakeStreamingTtsProvider();
        var pipeline = NewPipeline(provider, out var sink, out _);
        await pipeline.SubmitAsync(Req("u1", 1, 1, true, T(0)), CancellationToken.None);
        pipeline.Reset();

        var result = await pipeline.SubmitAsync(Req("u1", 1, 1, true, T(0)), CancellationToken.None); // same key, but generation now stale
        Assert.Equal(TtsPipelineOutcome.RejectedStaleGeneration, result.Outcome);
    }

    // ---- Playback sink failure: sink throwing must not corrupt pipeline state for subsequent calls ----
    [Fact]
    public async Task PlaybackSinkFailure_DoesNotPreventSubsequentSubmissions()
    {
        var provider = new FakeStreamingTtsProvider();
        var throwingSink = new ThrowOnceSink();
        var logger = new InMemoryDiagnosticLogger();
        var pipeline = new StreamingTtsPipelineExperiment(provider, throwingSink, logger, "en-US->de-DE");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            pipeline.SubmitAsync(Req("u1", 1, 1, true, T(0)), CancellationToken.None));

        throwingSink.ShouldThrow = false;
        var second = await pipeline.SubmitAsync(Req("u1", 1, 2, true, T(100)), CancellationToken.None);
        Assert.Equal(TtsPipelineOutcome.DeliveredToTestSink, second.Outcome);
    }

    private sealed class ThrowOnceSink : ITestPlaybackSink
    {
        public bool ShouldThrow = true;
        public void Enqueue(string utteranceId, int generation, int sequenceNumber, int byteCount, DateTimeOffset t4)
        {
            if (ShouldThrow) throw new InvalidOperationException("simulated sink failure");
        }
    }

    // ---- Privacy ----
    [Fact]
    public async Task NeverLogsSynthesizedText_OnlyMetadata()
    {
        var provider = new FakeStreamingTtsProvider();
        var pipeline = NewPipeline(provider, out _, out var logger);
        const string sensitive = "Ein sehr spezifischer vertraulicher Satz";

        await pipeline.SubmitAsync(Req("u1", 1, 1, true, T(0), sensitive), CancellationToken.None);

        Assert.DoesNotContain(logger.Entries, e => e.Details.Contains(sensitive) || e.Details.Contains("vertraulicher"));
        Assert.Contains(logger.Entries, e => e.EventType == "TtsPipelineOutcome");
    }
}
