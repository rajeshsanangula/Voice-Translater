using VTTranslate.Core.Streaming;

namespace VTTranslate.Core.Tests;

/// <summary>Test double — never makes a real network call, so these tests need no credentials and run identically regardless of Azure Translator authorization.</summary>
internal sealed class FakeTranslationProvider : IIncrementalTranslationProvider
{
    public List<TranslationProviderRequest> Requests { get; } = new();
    public Func<TranslationProviderRequest, TranslationProviderResult>? ResponseFactory { get; set; }

    public Task<TranslationProviderResult> TranslateAsync(TranslationProviderRequest request, CancellationToken ct)
    {
        Requests.Add(request);
        var result = ResponseFactory?.Invoke(request)
            ?? new TranslationProviderResult(true, EchoTranslate(request.TextToTranslate), null, 200);
        return Task.FromResult(result);
    }

    // Deterministic fake "translation": reverses word order, so tests can assert on shape/count without pretending to model real semantics.
    private static string EchoTranslate(string text) =>
        string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Reverse());
}

public class IncrementalTranslationProviderExperimentTests
{
    private static IncrementalTranslationProviderExperiment NewExperiment(FakeTranslationProvider provider, out InMemoryDiagnosticLogger logger)
    {
        logger = new InMemoryDiagnosticLogger();
        return new IncrementalTranslationProviderExperiment(provider, logger, "en-US->de-DE", generation: 1);
    }

    // ---- B1: cumulative source translation — always sends the FULL cumulative source ----
    [Fact]
    public async Task B1_AlwaysSendsFullCumulativeSource_NotJustTheNewSegment()
    {
        var provider = new FakeTranslationProvider();
        var experiment = NewExperiment(provider, out _);

        await experiment.ObserveStableSegmentAsync("u1", 1, "I would like", "I would like", "en-US", "de-DE", CancellationToken.None);
        await experiment.ObserveStableSegmentAsync("u1", 2, "to schedule", "I would like to schedule", "en-US", "de-DE", CancellationToken.None);

        var b1Requests = provider.Requests.Where(r => r.TextToTranslate.Contains("I would like")).ToList();
        Assert.Contains(b1Requests, r => r.TextToTranslate == "I would like to schedule");
    }

    // ---- B2: bounded preceding context ----
    [Fact]
    public async Task B2_SendsNewSegmentWithBoundedPrecedingContext_NotTheFullCumulativeText()
    {
        var provider = new FakeTranslationProvider();
        var experiment = NewExperiment(provider, out _);

        await experiment.ObserveStableSegmentAsync("u1", 1, "I would like", "I would like", "en-US", "de-DE", CancellationToken.None);
        await experiment.ObserveStableSegmentAsync("u1", 2, "to schedule", "I would like to schedule", "en-US", "de-DE", CancellationToken.None);

        var b2Request = provider.Requests.Single(r => r.TextToTranslate == "to schedule");
        Assert.Equal("I would like", b2Request.BoundedContext);
    }

    [Fact]
    public async Task B2_ContextWindow_IsBounded_NotUnboundedGrowth()
    {
        var provider = new FakeTranslationProvider();
        var experiment = NewExperiment(provider, out _);

        var cumulative = "";
        for (int i = 1; i <= 15; i++)
        {
            var seg = $"word{i}";
            cumulative = (cumulative + " " + seg).Trim();
            await experiment.ObserveStableSegmentAsync("u1", i, seg, cumulative, "en-US", "de-DE", CancellationToken.None);
        }

        var lastB2Request = provider.Requests.Last(r => r.TextToTranslate == "word15");
        var contextWordCount = (lastB2Request.BoundedContext ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        Assert.True(contextWordCount <= 8); // bounded window, not all 14 preceding words
    }

    // ---- B3: boundary-aware — held back until a sentence ending appears ----
    [Fact]
    public async Task B3_HoldsBackUntilSentenceBoundary_ThenTranslatesTheWholePhrase()
    {
        var provider = new FakeTranslationProvider();
        var experiment = NewExperiment(provider, out _);

        var r1 = await experiment.ObserveStableSegmentAsync("u1", 1, "I would like", "I would like", "en-US", "de-DE", CancellationToken.None);
        Assert.DoesNotContain(r1, x => x.StrategyName == "B3_BoundaryAware"); // no sentence ending yet

        var r2 = await experiment.ObserveStableSegmentAsync("u1", 2, "to schedule.", "I would like to schedule.", "en-US", "de-DE", CancellationToken.None);
        var b3 = Assert.Single(r2, x => x.StrategyName == "B3_BoundaryAware");
        Assert.True(b3.CallSucceeded);

        Assert.Contains(provider.Requests, r => r.TextToTranslate == "I would like to schedule.");
    }

    [Fact]
    public async Task B3_PendingPhrase_FlushedAtFinal_EvenWithoutASentenceEnding()
    {
        var provider = new FakeTranslationProvider();
        var experiment = NewExperiment(provider, out _);

        await experiment.ObserveStableSegmentAsync("u1", 1, "incomplete phrase", "incomplete phrase", "en-US", "de-DE", CancellationToken.None);
        var results = await experiment.ObserveFinalAsync("u1", "incomplete phrase without a period", "unvollständiger Satz ohne Punkt", "en-US", "de-DE", CancellationToken.None);

        var b3 = results.Single(r => r.StrategyName == "B3_BoundaryAware");
        Assert.True(b3.AnySuccessfulCall); // the pending phrase was flushed and translated at Final
    }

    // ---- Provider failure (e.g. 401 Unauthorized) handled honestly, not fabricated ----
    [Fact]
    public async Task ProviderFailure_ReportedHonestly_NoFabricatedCandidate()
    {
        var provider = new FakeTranslationProvider
        {
            ResponseFactory = _ => new TranslationProviderResult(false, null, "HTTP 401 Unauthorized", 401),
        };
        var experiment = NewExperiment(provider, out var logger);

        var results = await experiment.ObserveStableSegmentAsync("u1", 1, "I would like", "I would like", "en-US", "de-DE", CancellationToken.None);

        Assert.All(results, r => Assert.False(r.CallSucceeded));
        Assert.All(results, r => Assert.Equal(ProviderStrategyStability.UnsafeToSpeak, r.Stability));
        Assert.All(results, r => Assert.Equal(401, r.HttpStatusCode));

        var final = await experiment.ObserveFinalAsync("u1", "final source", "final translation", "en-US", "de-DE", CancellationToken.None);
        Assert.All(final, r => Assert.False(r.AnySuccessfulCall));
        Assert.All(final, r => Assert.Null(r.ApproxMissingTokenCount)); // not fabricated when there's no real candidate to compare
    }

    // ---- Final reconciliation, multiple utterances / reset isolation ----
    [Fact]
    public async Task MultipleUtterances_StateFullyResetsBetweenUtterances()
    {
        var provider = new FakeTranslationProvider();
        var experiment = NewExperiment(provider, out _);

        await experiment.ObserveStableSegmentAsync("u1", 1, "hello", "hello", "en-US", "de-DE", CancellationToken.None);
        await experiment.ObserveFinalAsync("u1", "hello there", "hallo dort", "en-US", "de-DE", CancellationToken.None);

        var r = await experiment.ObserveStableSegmentAsync("u2", 1, "goodbye", "goodbye", "en-US", "de-DE", CancellationToken.None);
        var b1 = r.Single(x => x.StrategyName == "B1_CumulativeSource");
        Assert.Equal(1, b1.CumulativeCandidateTokenCount); // just "goodbye" translated, no leftover from u1
    }

    [Fact]
    public async Task ObserveFinal_AlwaysReconcilesAllThreeStrategies()
    {
        var provider = new FakeTranslationProvider();
        var experiment = NewExperiment(provider, out _);
        await experiment.ObserveStableSegmentAsync("u1", 1, "hi", "hi", "en-US", "de-DE", CancellationToken.None);
        var results = await experiment.ObserveFinalAsync("u1", "hi there", "hallo da", "en-US", "de-DE", CancellationToken.None);

        Assert.Equal(3, results.Count);
        Assert.Contains(results, r => r.StrategyName == "B1_CumulativeSource");
        Assert.Contains(results, r => r.StrategyName == "B2_ContextualSegment");
        Assert.Contains(results, r => r.StrategyName == "B3_BoundaryAware");
    }

    // ---- Privacy: candidate text never logged, only metadata ----
    [Fact]
    public async Task NeverLogsCandidateOrSourceText_OnlyMetadata()
    {
        var provider = new FakeTranslationProvider();
        var experiment = NewExperiment(provider, out var logger);
        const string sensitive = "a very specific confidential business term";

        await experiment.ObserveStableSegmentAsync("u1", 1, sensitive, sensitive, "en-US", "de-DE", CancellationToken.None);
        await experiment.ObserveFinalAsync("u1", sensitive, "ein sehr spezifischer vertraulicher Geschäftsbegriff", "en-US", "de-DE", CancellationToken.None);

        Assert.DoesNotContain(logger.Entries, e => e.Details.Contains(sensitive) || e.Details.Contains("Geschäftsbegriff"));
        Assert.Contains(logger.Entries, e => e.EventType == "TranslationProviderStep");
        Assert.Contains(logger.Entries, e => e.EventType == "TranslationProviderFinalReconciliation");
    }

    // ---- Incomplete/interrupted sentence: B3 never force-translates a fragment mid-utterance ----
    [Fact]
    public async Task IncompleteSentence_B3NeverTranslatesFragmentBeforeBoundaryOrFinal()
    {
        var provider = new FakeTranslationProvider();
        var experiment = NewExperiment(provider, out _);

        var r1 = await experiment.ObserveStableSegmentAsync("u1", 1, "I was going to say", "I was going to say", "en-US", "de-DE", CancellationToken.None);
        var r2 = await experiment.ObserveStableSegmentAsync("u1", 2, "something but", "I was going to say something but", "en-US", "de-DE", CancellationToken.None);

        Assert.DoesNotContain(r1, x => x.StrategyName == "B3_BoundaryAware");
        Assert.DoesNotContain(r2, x => x.StrategyName == "B3_BoundaryAware");
    }
}
