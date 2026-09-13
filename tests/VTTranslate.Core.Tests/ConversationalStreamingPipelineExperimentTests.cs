using VTTranslate.Core.Streaming;

namespace VTTranslate.Core.Tests;

internal sealed class FakeTranslationProviderForPipeline : IIncrementalTranslationProvider
{
    public List<TranslationProviderRequest> Requests { get; } = new();
    public Func<TranslationProviderRequest, TranslationProviderResult>? ResponseFactory { get; set; }

    public Task<TranslationProviderResult> TranslateAsync(TranslationProviderRequest request, CancellationToken ct)
    {
        Requests.Add(request);
        var result = ResponseFactory?.Invoke(request) ?? new TranslationProviderResult(true, "T_" + request.TextToTranslate, null, 200);
        return Task.FromResult(result);
    }
}

internal sealed class FakeStreamingTtsProviderForPipeline : IStreamingTtsProvider
{
    public List<(string Text, string Lang, string Voice)> Calls { get; } = new();
    public Func<string, TtsSynthesisResult>? ResponseFactory { get; set; }

    public Task<TtsSynthesisResult> SynthesizeAsync(string text, string targetLanguage, string voiceName, Action<TtsAudioChunk> onChunk, CancellationToken ct)
    {
        Calls.Add((text, targetLanguage, voiceName));
        if (ResponseFactory != null) return Task.FromResult(ResponseFactory(text));

        var chunk = new TtsAudioChunk(new byte[100], 1, DateTimeOffset.UtcNow);
        onChunk(chunk);
        return Task.FromResult(new TtsSynthesisResult(true, 100, 1, null, chunk.Timestamp, DateTimeOffset.UtcNow));
    }
}

public class ConversationalStreamingPipelineExperimentTests
{
    private static DateTimeOffset T(int ms) => DateTimeOffset.UnixEpoch.AddMilliseconds(ms);

    private static ConversationalStreamingPipelineExperiment NewPipeline(
        out FakeTranslationProviderForPipeline translation, out FakeStreamingTtsProviderForPipeline tts,
        out InMemoryTestPlaybackSink sink, out InMemoryDiagnosticLogger logger,
        string contextStrategy = "C1", int maxQueueDepth = 5)
    {
        translation = new FakeTranslationProviderForPipeline();
        tts = new FakeStreamingTtsProviderForPipeline();
        sink = new InMemoryTestPlaybackSink();
        logger = new InMemoryDiagnosticLogger();
        return new ConversationalStreamingPipelineExperiment(
            new PrefixStabilityEngine(), translation, tts, sink, logger, "pipeline-test",
            contextStrategy: contextStrategy, maxQueueDepth: maxQueueDepth);
    }

    private static PipelineSourcePartialEvent Partial(string utt, int gen, int seq, string text, int ms) =>
        new(utt, gen, seq, text, T(ms));

    private static PipelineSourceFinalEvent Final(string utt, int gen, int seq, string text, int ms) =>
        new(utt, gen, seq, text, T(ms));

    /// <summary>
    /// Feeds `fullText` as a realistic word-by-word growing partial stream (exactly how
    /// Azure Recognizing events arrive — each partial one word longer than the last, per
    /// PrefixStabilityEngine's own doc comment) and returns every non-null segment boundary
    /// produced along the way, in emission order.
    /// </summary>
    private static List<PipelineSegmentRequest> FeedWordByWord(
        ConversationalStreamingPipelineExperiment pipeline, string uttId, int generation, string fullText,
        List<PipelineSegmentResult> dropped, int startMs = 0, int stepMs = 20)
    {
        var words = fullText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var boundaries = new List<PipelineSegmentRequest>();
        for (var i = 1; i <= words.Length; i++)
        {
            var partialText = string.Join(" ", words.Take(i));
            var seg = pipeline.ObservePartial(Partial(uttId, generation, i, partialText, startMs + i * stepMs), dropped);
            if (seg != null) boundaries.Add(seg);
        }
        return boundaries;
    }

    // ---- Concern 1: partial ASR -> stable semantic segment ----
    [Fact]
    public void ShortPunctuationFreePartials_DoNotYetProduceASegment_BelowMinTokenCount()
    {
        var pipeline = NewPipeline(out _, out _, out _, out _);
        var dropped = new List<PipelineSegmentResult>();

        var boundaries = FeedWordByWord(pipeline, "u1", 1, "Ich gehe heute", dropped);

        Assert.Empty(boundaries); // too short, no punctuation, no trailing trigger -> never semantically complete
    }

    [Fact]
    public void RealisticWordByWordStream_EventuallyProducesASentenceBoundary()
    {
        var pipeline = NewPipeline(out _, out _, out _, out _);
        var dropped = new List<PipelineSegmentResult>();

        var boundaries = FeedWordByWord(pipeline, "u1", 1, "Guten Tag. Wie geht es Ihnen?", dropped);

        Assert.NotEmpty(boundaries); // the word-by-word stream must reach at least one semantic boundary
    }

    // ---- Concern 2: bounded-context translation (C1) ----
    [Fact]
    public async Task C1Context_UsesOnlyThePrecedingSegment_AsBoundedContext()
    {
        var pipeline = NewPipeline(out var translation, out _, out _, out _, contextStrategy: "C1");
        var dropped = new List<PipelineSegmentResult>();

        var seg1 = pipeline.ObserveFinal(Final("u1", 1, 1, "Guten Tag.", 0), dropped);
        Assert.NotNull(seg1);
        Assert.Equal(string.Empty, seg1!.ContextText); // no history yet

        var seg2 = pipeline.ObserveFinal(Final("u2", 1, 1, "Ich komme aus Berlin.", 100), dropped);
        Assert.NotNull(seg2);
        Assert.Equal("Guten Tag.", seg2!.ContextText); // C1 = the one preceding segment (context persists across utterances — see concern 10)

        await pipeline.DrainAsync(CancellationToken.None);
        Assert.Equal(2, translation.Requests.Count);
        Assert.Null(translation.Requests[0].BoundedContext);
        Assert.Equal("Guten Tag.", translation.Requests[1].BoundedContext);
    }

    [Fact]
    public void C2Context_UsesTwoPrecedingSegments()
    {
        var pipeline = NewPipeline(out _, out _, out _, out _, contextStrategy: "C2");
        var dropped = new List<PipelineSegmentResult>();

        pipeline.ObserveFinal(Final("u1", 1, 1, "Eins.", 0), dropped);
        pipeline.ObserveFinal(Final("u2", 1, 1, "Zwei.", 100), dropped);
        var seg3 = pipeline.ObserveFinal(Final("u3", 1, 1, "Drei.", 200), dropped);

        Assert.NotNull(seg3);
        Assert.Equal("Eins. Zwei.", seg3!.ContextText);
    }

    [Fact]
    public void OnlyC0C1C2Accepted_C3AndC4RejectedByConstructor()
    {
        var logger = new InMemoryDiagnosticLogger();
        Assert.Throws<ArgumentException>(() => new ConversationalStreamingPipelineExperiment(
            new PrefixStabilityEngine(), new FakeTranslationProviderForPipeline(), new FakeStreamingTtsProviderForPipeline(),
            new InMemoryTestPlaybackSink(), logger, "t", contextStrategy: "C3"));
    }

    // ---- Concern 3/4: speakability + TTS scheduling — every delivered segment carries full T0-T6 timings ----
    [Fact]
    public async Task DeliveredSegment_CarriesFullLatencyBreakdown()
    {
        var pipeline = NewPipeline(out _, out _, out var sink, out _);
        var dropped = new List<PipelineSegmentResult>();
        pipeline.ObserveFinal(Final("u1", 1, 1, "Guten Tag.", 0), dropped);

        var results = await pipeline.DrainAsync(CancellationToken.None);

        Assert.Single(results);
        var r = results[0];
        Assert.Equal(PipelineSegmentOutcome.DeliveredToTestSink, r.Outcome);
        Assert.NotNull(r.T0ToT1TranslationStartMs);
        Assert.NotNull(r.T1ToT2TranslationCompleteMs);
        Assert.NotNull(r.T2ToT3TtsStartMs);
        Assert.NotNull(r.T3ToT4FirstAudioMs);
        Assert.NotNull(r.T4ToT5SynthesisCompleteMs);
        Assert.NotNull(r.T0ToT4FirstAudioTotalMs);
        Assert.NotNull(r.T0ToT6DeliveredTotalMs);
        Assert.Single(sink.Events);
    }

    // ---- Concern 5: cancellation / stale-generation protection ----
    [Fact]
    public async Task StaleGeneration_AtEnqueueTime_RejectedWithoutCallingTranslationOrTts()
    {
        var pipeline = NewPipeline(out var translation, out var tts, out _, out _);
        var dropped = new List<PipelineSegmentResult>();
        var seg = pipeline.ObserveFinal(Final("u1", 1, 1, "Guten Tag.", 0), dropped);
        Assert.NotNull(seg);

        pipeline.AdvanceGeneration(); // barge-in: generation 1 -> 2 before draining

        var results = await pipeline.DrainAsync(CancellationToken.None);

        Assert.Single(results);
        Assert.Equal(PipelineSegmentOutcome.RejectedStaleGeneration, results[0].Outcome);
        Assert.Empty(translation.Requests);
        Assert.Empty(tts.Calls);
    }

    [Fact]
    public async Task StaleGeneration_DuringTranslation_DiscardedEvenThoughTranslationSucceeded()
    {
        var pipeline = NewPipeline(out var translation, out _, out var sink, out _);
        var dropped = new List<PipelineSegmentResult>();
        pipeline.ObserveFinal(Final("u1", 1, 1, "Guten Tag.", 0), dropped);
        translation.ResponseFactory = req =>
        {
            pipeline.AdvanceGeneration(); // bump mid-flight, simulating a barge-in arriving while translation is in progress
            return new TranslationProviderResult(true, "T_" + req.TextToTranslate, null, 200);
        };

        var results = await pipeline.DrainAsync(CancellationToken.None);

        Assert.Single(results);
        Assert.Equal(PipelineSegmentOutcome.RejectedStaleGeneration, results[0].Outcome);
        Assert.Empty(sink.Events);
    }

    [Fact]
    public async Task Cancellation_DuringTranslation_ReportedAsCancelled_NeverReachesSink()
    {
        var pipeline = NewPipeline(out var translation, out _, out var sink, out _);
        using var cts = new CancellationTokenSource();
        var dropped = new List<PipelineSegmentResult>();
        pipeline.ObserveFinal(Final("u1", 1, 1, "Guten Tag.", 0), dropped);
        translation.ResponseFactory = req => throw new OperationCanceledException();

        var results = await pipeline.DrainAsync(cts.Token);

        Assert.Single(results);
        Assert.Equal(PipelineSegmentOutcome.Cancelled, results[0].Outcome);
        Assert.Empty(sink.Events);
    }

    // ---- Concern 6: duplicate prevention ----
    // The pipeline guards duplicate DELIVERY of the exact same (UtteranceId, Generation,
    // SegmentSequence) key — see ProcessSegmentAsync's _delivered check. This key
    // genuinely recurs via the public surface in exactly one scenario, proven below: the
    // pipeline resets its per-utterance SegmentSequence counter whenever a NEW UtteranceId
    // arrives (see ResetTrackingIfNewUtterance) — including a REPLAYED Final that reuses
    // the same UtteranceId a second time, since ObserveFinal always clears the "current
    // utterance" tracker at the end of processing. A replayed Final for utterance "u1"
    // therefore produces SegmentSequence=0 again (recycled), colliding with the FIRST
    // segment's key — and the guard correctly rejects it, keeping the sink at one delivery
    // instead of two.
    [Fact]
    public async Task ReplayedFinalForSameUtteranceId_RecyclesSegmentSequence_RejectedAsDuplicate()
    {
        var pipeline = NewPipeline(out _, out _, out var sink, out _);
        var dropped = new List<PipelineSegmentResult>();

        pipeline.ObserveFinal(Final("u1", 1, 1, "Guten Tag.", 0), dropped);
        var firstResults = await pipeline.DrainAsync(CancellationToken.None);
        Assert.Equal(PipelineSegmentOutcome.DeliveredToTestSink, firstResults[0].Outcome);
        Assert.Single(sink.Events);

        var second = pipeline.ObserveFinal(Final("u1", 1, 2, "Guten Tag.", 100), dropped);
        Assert.NotNull(second);
        Assert.Equal(0, second!.SegmentSequence); // recycled — collides with the first segment's key

        var secondResults = await pipeline.DrainAsync(CancellationToken.None);
        Assert.Equal(PipelineSegmentOutcome.RejectedDuplicate, secondResults[0].Outcome);
        Assert.Single(sink.Events); // still just the one — never delivered twice
    }

    // ---- Concern 7: ordering ----
    [Fact]
    public async Task MultipleSegmentsAcrossUtterances_DeliveredToSinkInFifoOrder()
    {
        var pipeline = NewPipeline(out _, out _, out var sink, out _);
        var dropped = new List<PipelineSegmentResult>();

        pipeline.ObserveFinal(Final("u1", 1, 1, "Eins.", 0), dropped);
        pipeline.ObserveFinal(Final("u2", 1, 1, "Zwei.", 100), dropped);
        pipeline.ObserveFinal(Final("u3", 1, 1, "Drei.", 200), dropped);

        await pipeline.DrainAsync(CancellationToken.None);

        Assert.Equal(3, sink.Events.Count);
        Assert.Equal(new[] { "u1", "u2", "u3" }, sink.Events.Select(e => e.UtteranceId));
    }

    [Fact]
    public async Task MultipleSegmentsWithinOneUtterance_DeliveredInSequenceOrder()
    {
        var pipeline = NewPipeline(out _, out _, out var sink, out _);
        var dropped = new List<PipelineSegmentResult>();

        var boundaries = FeedWordByWord(pipeline, "u1", 1, "Guten Tag. Wie geht es Ihnen heute?", dropped);
        Assert.True(boundaries.Count >= 1, "the realistic word-by-word stream should reach at least one boundary");

        await pipeline.DrainAsync(CancellationToken.None);

        var delivered = sink.Events.Where(e => e.UtteranceId == "u1").ToList();
        Assert.Equal(delivered.Select(e => e.SequenceNumber).OrderBy(x => x), delivered.Select(e => e.SequenceNumber));
    }

    // ---- Concern 8: interruption / barging ----
    [Fact]
    public async Task NewUtteranceWithGenerationBump_DiscardsPreviousUtterancesQueuedSegments()
    {
        var pipeline = NewPipeline(out var translation, out _, out var sink, out _);
        var dropped = new List<PipelineSegmentResult>();

        pipeline.ObserveFinal(Final("u1", 1, 1, "Guten Tag, wie geht es Ihnen heute?", 0), dropped);
        pipeline.AdvanceGeneration(); // caller detects barge-in: u1 interrupted by u2
        pipeline.ObserveFinal(Final("u2", 2, 1, "Hallo, ich habe eine Frage.", 100), dropped);

        var results = await pipeline.DrainAsync(CancellationToken.None);

        Assert.Equal(2, results.Count);
        Assert.Equal(PipelineSegmentOutcome.RejectedStaleGeneration, results[0].Outcome); // u1's segment, generation 1, now stale
        Assert.Equal(PipelineSegmentOutcome.DeliveredToTestSink, results[1].Outcome); // u2's segment, current generation
        Assert.Single(sink.Events);
        Assert.Single(translation.Requests); // stale u1 segment never even reached translation
    }

    // ---- Concern 9: backpressure under fast speech ----
    // NOTE: ObserveFinal segments are ALWAYS Final (IsFinalSegment=true) by construction —
    // only ObservePartial-produced mid-utterance boundaries are ever non-final and thus
    // ever eligible for backpressure eviction. These tests therefore drive the queue via a
    // realistic word-by-word partial stream (ObservePartial), not ObserveFinal.
    [Fact]
    public void FastSpeech_ExceedingMaxQueueDepth_DropsOldestNonFinalSegments()
    {
        var pipeline = NewPipeline(out _, out _, out _, out _, maxQueueDepth: 1);
        var dropped = new List<PipelineSegmentResult>();

        var boundaries = FeedWordByWord(pipeline, "u1", 1,
            "Erster Satz ist fertig. Zweiter Satz ist auch fertig. Dritter Satz beendet die Aussage.", dropped);

        Assert.True(boundaries.Count >= 2, "need multiple non-final boundaries to exercise eviction");
        Assert.Equal(1, pipeline.CurrentQueueDepth); // bound=1: only the most recently queued boundary remains
        Assert.True(dropped.Count >= 1);
        Assert.All(dropped, d => Assert.Equal(PipelineSegmentOutcome.DroppedForBackpressure, d.Outcome));
        // Eviction always takes the OLDEST queued entry — the surviving one is the highest sequence number seen so far.
        Assert.All(dropped, d => Assert.True(d.SegmentSequence < boundaries[^1].SegmentSequence));
        Assert.Equal(1, pipeline.MaxObservedQueueDepth);
    }

    [Fact]
    public void FinalSegment_NeverDroppedForBackpressure_EvenWhenQueueIsFull()
    {
        var pipeline = NewPipeline(out _, out _, out _, out _, maxQueueDepth: 1);
        var dropped = new List<PipelineSegmentResult>();

        // A word-by-word stream that reaches exactly one non-final boundary, filling the depth-1 queue.
        FeedWordByWord(pipeline, "u1", 1, "Guten Tag. Wie geht", dropped);
        Assert.Equal(1, pipeline.CurrentQueueDepth);
        var droppedBeforeFinal = dropped.Count;

        var finalSeg = pipeline.ObserveFinal(Final("u1", 1, 999, "Guten Tag. Wie geht es Ihnen heute wirklich sehr gut?", 1000), dropped);

        Assert.NotNull(finalSeg);
        Assert.True(finalSeg!.IsFinalSegment);
        Assert.True(dropped.Count > droppedBeforeFinal); // the earlier non-final boundary was evicted to make room for the Final
        Assert.Equal(PipelineSegmentOutcome.DroppedForBackpressure, dropped[^1].Outcome);
        Assert.Equal(1, pipeline.CurrentQueueDepth); // Final took the freed slot — the bound was never exceeded because room existed to evict
    }

    [Fact]
    public void FinalSegment_ExceedsBound_OnlyWhenQueueIsSaturatedEntirelyWithUndrainedFinals()
    {
        var pipeline = NewPipeline(out _, out _, out _, out _, maxQueueDepth: 1);
        var dropped = new List<PipelineSegmentResult>();

        pipeline.ObserveFinal(Final("u1", 1, 1, "Eins.", 0), dropped); // Final, fills the depth-1 queue
        Assert.Equal(1, pipeline.CurrentQueueDepth);

        pipeline.ObserveFinal(Final("u2", 1, 1, "Zwei.", 100), dropped); // Final again — no non-final to evict

        Assert.Empty(dropped); // neither Final was ever dropped
        Assert.Equal(2, pipeline.CurrentQueueDepth); // bound exceeded rather than losing Final content
    }

    // ---- Concern 10: consecutive utterances ----
    [Fact]
    public async Task ConsecutiveUtterances_SecondUtterancesContextIncludesFirstUtterancesLastSegment()
    {
        var pipeline = NewPipeline(out var translation, out _, out _, out _, contextStrategy: "C1");
        var dropped = new List<PipelineSegmentResult>();

        pipeline.ObserveFinal(Final("u1", 1, 1, "Guten Morgen.", 0), dropped);
        var seg2 = pipeline.ObserveFinal(Final("u2", 1, 1, "Wie geht es Ihnen?", 100), dropped);

        Assert.NotNull(seg2);
        Assert.Equal("Guten Morgen.", seg2!.ContextText); // ASSUMED: context persists across the utterance boundary — see design notes

        await pipeline.DrainAsync(CancellationToken.None);
        Assert.Equal("Guten Morgen.", translation.Requests[1].BoundedContext);
    }

    [Fact]
    public void ConsecutiveUtterances_BoundaryTrackingResetsPerUtterance_NotCumulativeAcrossUtterances()
    {
        var pipeline = NewPipeline(out _, out _, out _, out _);
        var dropped = new List<PipelineSegmentResult>();

        pipeline.ObserveFinal(Final("u1", 1, 1, "Eins zwei drei vier fünf sechs sieben acht.", 0), dropped);
        // A short new utterance with far fewer tokens than u1's final committed length must
        // still be evaluated fresh (not compared against u1's leftover token count).
        var seg = pipeline.ObserveFinal(Final("u2", 1, 1, "Hallo.", 100), dropped);

        Assert.NotNull(seg);
        Assert.Equal("Hallo.", seg!.SourceText);
    }

    // ---- 7 named speech patterns ----
    [Fact]
    public async Task NormalSpeech_PunctuatedSentence_ProducesOneSegment()
    {
        var pipeline = NewPipeline(out _, out _, out var sink, out _);
        var dropped = new List<PipelineSegmentResult>();
        pipeline.ObserveFinal(Final("u1", 1, 1, "Das ist ein normaler Satz.", 0), dropped);
        await pipeline.DrainAsync(CancellationToken.None);
        Assert.Single(sink.Events);
    }

    [Fact]
    public async Task FastSpeech_ManyShortUtterancesInQuickSuccession_AllDelivered_FinalsNeverDropped()
    {
        var pipeline = NewPipeline(out _, out _, out var sink, out _, maxQueueDepth: 3);
        var dropped = new List<PipelineSegmentResult>();
        for (var i = 1; i <= 5; i++)
            pipeline.ObserveFinal(Final($"u{i}", 1, 1, $"Satz {i}.", i * 10), dropped);

        // Each ObserveFinal call is a distinct (short) utterance's Final segment — Final
        // segments are never dropped for backpressure (see FinalSegment_ExceedsBound_*),
        // so fast consecutive short utterances all reach the queue even past MaxQueueDepth.
        Assert.Empty(dropped);
        Assert.Equal(5, pipeline.CurrentQueueDepth);

        var results = await pipeline.DrainAsync(CancellationToken.None);

        Assert.Equal(5, results.Count);
        Assert.Equal(5, sink.Events.Count);
        Assert.All(results, r => Assert.Equal(PipelineSegmentOutcome.DeliveredToTestSink, r.Outcome));
    }

    [Fact]
    public async Task LongUtterance_MultipleSentences_ProducesMultipleSegmentsFromARealisticPartialStream()
    {
        var pipeline = NewPipeline(out _, out _, out var sink, out _, maxQueueDepth: 20); // large bound: this test is about ordering/count, not backpressure
        var dropped = new List<PipelineSegmentResult>();
        var boundaries = FeedWordByWord(pipeline, "u1", 1,
            "Erster Satz ist fertig. Zweiter Satz ist auch fertig. Dritter Satz beendet die Aussage.", dropped);

        await pipeline.DrainAsync(CancellationToken.None);

        Assert.True(boundaries.Count >= 2, $"expected multiple boundaries from a 3-sentence stream, got {boundaries.Count}");
        Assert.Empty(dropped);
        Assert.Equal(boundaries.Count, sink.Events.Count(e => e.UtteranceId == "u1"));
    }

    [Fact]
    public async Task ShortUtterance_GoesStraightToFinalWithNoPartials()
    {
        var pipeline = NewPipeline(out _, out _, out var sink, out _);
        var dropped = new List<PipelineSegmentResult>();
        var seg = pipeline.ObserveFinal(Final("u1", 1, 1, "Ja.", 0), dropped);
        Assert.NotNull(seg);
        await pipeline.DrainAsync(CancellationToken.None);
        Assert.Single(sink.Events);
    }

    [Fact]
    public async Task IncompleteUtterance_NeverReachesSemanticCompletion_NoSegmentEverQueued()
    {
        var pipeline = NewPipeline(out _, out _, out var sink, out _);
        var dropped = new List<PipelineSegmentResult>();
        var boundaries = FeedWordByWord(pipeline, "u1", 1, "Ich möchte dass", dropped); // trailing conjunction -> always WAIT
        await pipeline.DrainAsync(CancellationToken.None);
        Assert.Empty(boundaries);
        Assert.Empty(sink.Events);
    }

    [Fact]
    public async Task SelfCorrectingSpeech_ContradictingLaterPartial_DoesNotRetractAlreadyCommittedSegment()
    {
        var pipeline = NewPipeline(out _, out _, out var sink, out _);
        var dropped = new List<PipelineSegmentResult>();
        var boundaries = FeedWordByWord(pipeline, "u1", 1, "Ich fahre nach Berlin. Wir sehen uns dort.", dropped);
        Assert.NotEmpty(boundaries);
        await pipeline.DrainAsync(CancellationToken.None);
        var deliveredBefore = sink.Events.Count;
        Assert.True(deliveredBefore >= 1);

        // A later, self-correcting partial contradicting an already-committed word ("Berlin"
        // -> "Hamburg") cannot retract what's already delivered — PrefixStabilityEngine's own
        // documented trade-off, inherited unchanged here (see prefix-stability-engine.md
        // "Regression handling").
        pipeline.ObservePartial(Partial("u1", 1, 100, "Ich fahre nach Hamburg. Wir sehen uns dort.", 1000), dropped);
        await pipeline.DrainAsync(CancellationToken.None);
        Assert.Equal(deliveredBefore, sink.Events.Count); // no retraction, no duplicate
    }

    [Fact]
    public async Task InterruptedSpeech_BargeInMidUtterance_OnlyNewUtteranceReachesSink()
    {
        var pipeline = NewPipeline(out _, out _, out var sink, out _);
        var dropped = new List<PipelineSegmentResult>();
        pipeline.ObserveFinal(Final("u1", 1, 1, "Warten Sie bitte einen Moment.", 0), dropped);
        pipeline.AdvanceGeneration();
        pipeline.ObserveFinal(Final("u2", 2, 1, "Nein, ich habe keine Zeit.", 100), dropped);

        var results = await pipeline.DrainAsync(CancellationToken.None);

        Assert.Single(sink.Events);
        Assert.Contains(results, r => r.Outcome == PipelineSegmentOutcome.RejectedStaleGeneration);
    }

    // ---- Privacy: no source/translated/synthesized text in diagnostic logs ----
    [Fact]
    public async Task NeverLogsSourceOrTranslatedText_OnlyMetadata()
    {
        var pipeline = NewPipeline(out _, out _, out _, out var logger);
        const string sensitive = "Ein sehr spezifischer vertraulicher Satz.";
        var dropped = new List<PipelineSegmentResult>();
        pipeline.ObserveFinal(Final("u1", 1, 1, sensitive, 0), dropped);
        await pipeline.DrainAsync(CancellationToken.None);

        Assert.DoesNotContain(logger.Entries, e => e.Details.Contains("vertraulicher") || e.Details.Contains(sensitive));
        Assert.Contains(logger.Entries, e => e.EventType == "PipelineSegmentOutcome");
    }
}
