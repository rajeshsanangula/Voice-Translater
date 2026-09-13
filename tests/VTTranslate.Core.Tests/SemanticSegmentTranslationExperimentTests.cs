using VTTranslate.Core.Streaming;

namespace VTTranslate.Core.Tests;

internal sealed class FakeTranslationProviderForSemantic : IIncrementalTranslationProvider
{
    public List<TranslationProviderRequest> Requests { get; } = new();
    public Func<TranslationProviderRequest, TranslationProviderResult>? ResponseFactory { get; set; }

    public Task<TranslationProviderResult> TranslateAsync(TranslationProviderRequest request, CancellationToken ct)
    {
        Requests.Add(request);
        var result = ResponseFactory?.Invoke(request) ?? new TranslationProviderResult(true, EchoTranslate(request.TextToTranslate), null, 200);
        return Task.FromResult(result);
    }

    private static string EchoTranslate(string text) =>
        string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Reverse());
}

public class SemanticSegmentTranslationExperimentTests
{
    private const string U = "utt-0";
    private static DateTimeOffset T(int ms) => DateTimeOffset.UnixEpoch.AddMilliseconds(ms);

    private static SemanticSegmentTranslationExperiment NewExperiment(
        FakeTranslationProviderForSemantic provider, out InMemoryDiagnosticLogger logger)
    {
        logger = new InMemoryDiagnosticLogger();
        return new SemanticSegmentTranslationExperiment(
            new PrefixStabilityEngine(), provider, logger, "en-US->de-DE", 1, "en-US", "de-DE");
    }

    // ---- Semantic completion drives Policy B/C, not just source stability ----
    [Fact]
    public async Task PolicyA_FiresOnEverySourceCommit_RegardlessOfSemanticCompleteness()
    {
        var provider = new FakeTranslationProviderForSemantic();
        var experiment = NewExperiment(provider, out _);

        await experiment.ObservePartialAsync(new SemanticSourceObservation(U, 1, "I would", T(0), false), CancellationToken.None);
        var r2 = await experiment.ObservePartialAsync(new SemanticSourceObservation(U, 2, "I would like", T(100), false), CancellationToken.None);

        var a = r2.Single(x => x.Policy == "PolicyA_Cumulative");
        Assert.Equal(SemanticTranslationState.Candidate, a.TranslationState);
        Assert.True(a.HasNewCandidate);
    }

    [Fact]
    public async Task PolicyB_WaitsWhileSourceEndsOnModalVerb_EvenThoughSourceIsStable()
    {
        var provider = new FakeTranslationProviderForSemantic();
        var experiment = NewExperiment(provider, out _);

        await experiment.ObservePartialAsync(new SemanticSourceObservation(U, 1, "I would", T(0), false), CancellationToken.None);
        var r2 = await experiment.ObservePartialAsync(new SemanticSourceObservation(U, 2, "I would like", T(100), false), CancellationToken.None);

        // Committed source at this point is "I" — a single token, not remotely complete —
        // Policy B must not have fired.
        Assert.DoesNotContain(r2, x => x.Policy == "PolicyB_ConservativeSemantic");
    }

    // ---- Conservative WAIT decisions: conjunctions ----
    [Fact]
    public async Task PolicyB_WaitsOnTrailingConjunction()
    {
        var provider = new FakeTranslationProviderForSemantic();
        var experiment = NewExperiment(provider, out _);

        // Build up committed source ending in "and" (a conjunction) across several partials.
        await experiment.ObservePartialAsync(new SemanticSourceObservation(U, 1, "Please review the report", T(0), false), CancellationToken.None);
        var r2 = await experiment.ObservePartialAsync(new SemanticSourceObservation(U, 2, "Please review the report and", T(100), false), CancellationToken.None);
        var r3 = await experiment.ObservePartialAsync(new SemanticSourceObservation(U, 3, "Please review the report and send", T(200), false), CancellationToken.None);

        // At no point should Policy B have translated a prefix ending in "and".
        Assert.DoesNotContain(provider.Requests, req => req.TextToTranslate.TrimEnd().EndsWith(" and") || req.TextToTranslate.Trim() == "and");
    }

    // ---- Modal verbs (dedicated) ----
    [Fact]
    public async Task PolicyB_WaitsOnGermanModalVerb_Moechte()
    {
        var provider = new FakeTranslationProviderForSemantic();
        var experiment = NewExperiment(provider, out _);

        await experiment.ObservePartialAsync(new SemanticSourceObservation(U, 1, "Ich", T(0), false), CancellationToken.None);
        var r2 = await experiment.ObservePartialAsync(new SemanticSourceObservation(U, 2, "Ich möchte", T(100), false), CancellationToken.None);

        Assert.DoesNotContain(r2, x => x.Policy is "PolicyB_ConservativeSemantic" or "PolicyC_SemanticBoundaryAware");
    }

    // ---- Subordinate clauses (German "dass") ----
    [Fact]
    public async Task PolicyB_WaitsOnSubordinateClauseIntroducer()
    {
        var provider = new FakeTranslationProviderForSemantic();
        var experiment = NewExperiment(provider, out _);

        await experiment.ObservePartialAsync(new SemanticSourceObservation(U, 1, "Ich denke", T(0), false), CancellationToken.None);
        var r2 = await experiment.ObservePartialAsync(new SemanticSourceObservation(U, 2, "Ich denke, dass", T(100), false), CancellationToken.None);

        Assert.DoesNotContain(provider.Requests, req => req.TextToTranslate.Contains("dass"));
    }

    // ---- Separable verbs ----
    [Fact]
    public async Task PolicyB_WaitsWhileSeparableVerbParticlePending()
    {
        var provider = new FakeTranslationProviderForSemantic();
        var experiment = NewExperiment(provider, out _);

        await experiment.ObservePartialAsync(new SemanticSourceObservation(U, 1, "Ich rufe dich", T(0), false), CancellationToken.None);
        var r2 = await experiment.ObservePartialAsync(new SemanticSourceObservation(U, 2, "Ich rufe dich morgen früh", T(100), false), CancellationToken.None);

        Assert.DoesNotContain(r2, x => x.Policy == "PolicyB_ConservativeSemantic");
    }

    // ---- Self-correction: trailing word held back by Policy A's own source engine ----
    [Fact]
    public async Task SelfCorrection_TrailingWordNeverCommittedOrTranslated()
    {
        var provider = new FakeTranslationProviderForSemantic();
        var experiment = NewExperiment(provider, out _);

        await experiment.ObservePartialAsync(new SemanticSourceObservation(U, 1, "meet on Tuesday", T(0), false), CancellationToken.None);
        await experiment.ObservePartialAsync(new SemanticSourceObservation(U, 2, "meet on Thursday", T(100), false), CancellationToken.None);

        Assert.DoesNotContain(provider.Requests, req => req.TextToTranslate.Contains("Tuesday") || req.TextToTranslate.Contains("Thursday"));
    }

    // ---- Finalization: force-flushes Policy C's pending buffer, obtains real authoritative translation ----
    [Fact]
    public async Task Finalization_FlushesPendingBufferAndObtainsAuthoritativeTranslation()
    {
        var provider = new FakeTranslationProviderForSemantic();
        var experiment = NewExperiment(provider, out _);

        await experiment.ObservePartialAsync(new SemanticSourceObservation(U, 1, "I would", T(0), false), CancellationToken.None);
        await experiment.ObservePartialAsync(new SemanticSourceObservation(U, 2, "I would like", T(100), false), CancellationToken.None);

        var summaries = await experiment.ObserveFinalAsync(
            new SemanticSourceObservation(U, 3, "I would like to schedule.", T(200), true), CancellationToken.None);

        Assert.Equal(3, summaries.Count);
        Assert.All(summaries, s => Assert.True(s.FinalTranslationSucceeded));
        Assert.All(summaries, s => Assert.NotNull(s.FinalTranslationLatencyMs));
    }

    // ---- Consecutive utterances ----
    [Fact]
    public async Task ConsecutiveUtterances_FullyIsolated()
    {
        var provider = new FakeTranslationProviderForSemantic();
        var experiment = NewExperiment(provider, out _);

        await experiment.ObservePartialAsync(new SemanticSourceObservation("utt-a", 1, "hello there", T(0), false), CancellationToken.None);
        await experiment.ObservePartialAsync(new SemanticSourceObservation("utt-a", 2, "hello there friend", T(100), false), CancellationToken.None);
        await experiment.ObserveFinalAsync(new SemanticSourceObservation("utt-a", 3, "hello there friend.", T(200), true), CancellationToken.None);

        var r1 = await experiment.ObservePartialAsync(new SemanticSourceObservation("utt-b", 1, "goodbye", T(1000), false), CancellationToken.None);
        Assert.Empty(r1); // first partial of a brand-new utterance: no commit possible yet
    }

    // ---- Duplicate prevention ----
    [Fact]
    public async Task DuplicatePrevention_PolicyC_NoDuplicateTokensForCleanGrowth()
    {
        var provider = new FakeTranslationProviderForSemantic();
        var experiment = NewExperiment(provider, out _);

        await experiment.ObservePartialAsync(new SemanticSourceObservation(U, 1, "Please review the quarterly", T(0), false), CancellationToken.None);
        await experiment.ObservePartialAsync(new SemanticSourceObservation(U, 2, "Please review the quarterly budget", T(100), false), CancellationToken.None);
        await experiment.ObservePartialAsync(new SemanticSourceObservation(U, 3, "Please review the quarterly budget report", T(200), false), CancellationToken.None);

        var summaries = await experiment.ObserveFinalAsync(
            new SemanticSourceObservation(U, 4, "Please review the quarterly budget report.", T(300), true), CancellationToken.None);

        var c = summaries.Single(s => s.Policy == "PolicyC_SemanticBoundaryAware");
        Assert.True(c.ApproxDuplicateTokenCount is null or 0);
    }

    // ---- Translation failure ----
    [Fact]
    public async Task TranslationFailure_MapsToRevisionRequired()
    {
        var provider = new FakeTranslationProviderForSemantic
        {
            ResponseFactory = _ => new TranslationProviderResult(false, null, "HTTP 429", 429),
        };
        var experiment = NewExperiment(provider, out _);

        await experiment.ObservePartialAsync(new SemanticSourceObservation(U, 1, "I would", T(0), false), CancellationToken.None);
        var r2 = await experiment.ObservePartialAsync(new SemanticSourceObservation(U, 2, "I would like", T(100), false), CancellationToken.None);

        var a = r2.Single(x => x.Policy == "PolicyA_Cumulative");
        Assert.Equal(SemanticTranslationState.RevisionRequired, a.TranslationState);
        Assert.False(a.HasNewCandidate);
    }

    // ---- Session/generation reset ----
    [Fact]
    public void ExplicitReset_DoesNotThrow()
    {
        var provider = new FakeTranslationProviderForSemantic();
        var experiment = NewExperiment(provider, out _);
        var ex = Record.Exception(() => experiment.Reset());
        Assert.Null(ex);
    }

    // ---- Privacy ----
    [Fact]
    public async Task NeverLogsSourceOrTranslatedText_OnlyMetadata()
    {
        var provider = new FakeTranslationProviderForSemantic();
        var experiment = NewExperiment(provider, out var logger);
        const string sensitive = "a very specific confidential business phrase indeed";

        await experiment.ObservePartialAsync(new SemanticSourceObservation(U, 1, sensitive, T(0), false), CancellationToken.None);
        await experiment.ObservePartialAsync(new SemanticSourceObservation(U, 2, sensitive + " continues", T(100), false), CancellationToken.None);
        await experiment.ObserveFinalAsync(new SemanticSourceObservation(U, 3, sensitive + " continues.", T(200), true), CancellationToken.None);

        Assert.DoesNotContain(logger.Entries, e => e.Details.Contains(sensitive) || e.Details.Contains("confidential"));
        Assert.Contains(logger.Entries, e => e.EventType == "SemanticSegmentStep");
        Assert.Contains(logger.Entries, e => e.EventType == "SemanticSegmentFinalReconciliation");
    }

    // ---- Short utterance: zero partials ----
    [Fact]
    public async Task ShortUtterance_ZeroPartials_StillProducesAuthoritativeTranslation()
    {
        var provider = new FakeTranslationProviderForSemantic();
        var experiment = NewExperiment(provider, out _);

        var summaries = await experiment.ObserveFinalAsync(new SemanticSourceObservation(U, 1, "Ja.", T(0), true), CancellationToken.None);

        Assert.All(summaries, s => Assert.True(s.FinalTranslationSucceeded));
    }
}
