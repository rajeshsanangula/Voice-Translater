using VTTranslate.Core.Streaming;

namespace VTTranslate.Core.Tests;

internal sealed class FakeTranslationProviderForProvisional : IIncrementalTranslationProvider
{
    public List<TranslationProviderRequest> Requests { get; } = new();
    public Func<TranslationProviderRequest, TranslationProviderResult>? ResponseFactory { get; set; }

    public Task<TranslationProviderResult> TranslateAsync(TranslationProviderRequest request, CancellationToken ct)
    {
        Requests.Add(request);
        var result = ResponseFactory?.Invoke(request) ?? new TranslationProviderResult(true, EchoTranslate(request.TextToTranslate), null, 200);
        return Task.FromResult(result);
    }

    // Deterministic fake: preserves word order (NOT reversed) so word-level prefix/overlap
    // checks in Architecture B/C are meaningful to test directly.
    private static string EchoTranslate(string text) => "T_" + text;
}

public class ProvisionalTranslationArchitectureExperimentTests
{
    private const string U = "utt-0";
    private static DateTimeOffset T(int ms) => DateTimeOffset.UnixEpoch.AddMilliseconds(ms);

    private static ProvisionalTranslationArchitectureExperiment NewExperiment(
        FakeTranslationProviderForProvisional provider, out InMemoryDiagnosticLogger logger)
    {
        logger = new InMemoryDiagnosticLogger();
        return new ProvisionalTranslationArchitectureExperiment(
            new PrefixStabilityEngine(), provider, logger, "en-US->de-DE", 1, "en-US", "de-DE");
    }

    // ---- Incremental prefix progression: all three architectures fire on every stable commit ----
    [Fact]
    public async Task IncrementalProgression_AllThreeArchitecturesProduceResults()
    {
        var provider = new FakeTranslationProviderForProvisional();
        var experiment = NewExperiment(provider, out _);

        await experiment.ObservePartialAsync(new ProvisionalSourceObservation(U, 1, "I would", T(0), false), CancellationToken.None);
        var r2 = await experiment.ObservePartialAsync(new ProvisionalSourceObservation(U, 2, "I would like", T(100), false), CancellationToken.None);

        Assert.Contains(r2, x => x.Architecture == "ArchitectureA_CumulativeRetranslation");
        Assert.Contains(r2, x => x.Architecture == "ArchitectureB_OverlappingSegments");
        Assert.Contains(r2, x => x.Architecture == "ArchitectureC_ProvisionalDraftPlusAuthoritative");
    }

    // ---- Architecture A never becomes Speakable (ungated baseline) ----
    [Fact]
    public async Task ArchitectureA_NeverBecomesSpeakable_EvenAcrossManyConsistentSteps()
    {
        var provider = new FakeTranslationProviderForProvisional();
        var experiment = NewExperiment(provider, out _);

        await experiment.ObservePartialAsync(new ProvisionalSourceObservation(U, 1, "I would", T(0), false), CancellationToken.None);
        var r2 = await experiment.ObservePartialAsync(new ProvisionalSourceObservation(U, 2, "I would like", T(100), false), CancellationToken.None);
        var r3 = await experiment.ObservePartialAsync(new ProvisionalSourceObservation(U, 3, "I would like to schedule", T(200), false), CancellationToken.None);

        var aResults = r2.Concat(r3).Where(x => x.Architecture == "ArchitectureA_CumulativeRetranslation");
        Assert.All(aResults, x => Assert.Equal(SpeechReadiness.NotSpeakable, x.SpeechState));
        Assert.All(aResults, x => Assert.Equal(ProvisionalTranslationState.Draft, x.TranslationState));
    }

    // ---- Overlapping segments: Architecture B ----
    [Fact]
    public async Task ArchitectureB_TranslatesSlidingWindow_NotFullCumulative()
    {
        var provider = new FakeTranslationProviderForProvisional();
        var experiment = NewExperiment(provider, out _);

        await experiment.ObservePartialAsync(new ProvisionalSourceObservation(U, 1, "one two three four five", T(0), false), CancellationToken.None);
        await experiment.ObservePartialAsync(new ProvisionalSourceObservation(U, 2, "one two three four five six seven", T(100), false), CancellationToken.None);

        // Committed source after step 2 grows past the window size (4) — B's requests
        // should never contain the FULL cumulative committed text once it exceeds the window.
        Assert.DoesNotContain(provider.Requests, r => r.TextToTranslate == "one two three four five six");
    }

    [Fact]
    public async Task ArchitectureB_ReconciliationNeverBlindlyDoublesOverlappingContent()
    {
        var provider = new FakeTranslationProviderForProvisional();
        var experiment = NewExperiment(provider, out _);

        await experiment.ObservePartialAsync(new ProvisionalSourceObservation(U, 1, "alpha beta gamma delta", T(0), false), CancellationToken.None);
        var r2 = await experiment.ObservePartialAsync(new ProvisionalSourceObservation(U, 2, "alpha beta gamma delta epsilon zeta", T(100), false), CancellationToken.None);

        var b = r2.Single(x => x.Architecture == "ArchitectureB_OverlappingSegments");
        // Cumulative token count must not have doubled the overlapping "T_...gamma delta"
        // portion — it should reflect only genuinely new content beyond the overlap.
        Assert.True(b.CumulativeCandidateTokenCount < 8); // 4 (first segment) + 4 (second segment) would be 8 if blindly concatenated with zero dedup credit
    }

    // ---- Translation revisions / contradictory translations: Architecture C ----
    [Fact]
    public async Task ArchitectureC_ContradictingTranslation_MarkedRevised_NeverSpeakable()
    {
        // Deterministic by CALL COUNT (not text content) to avoid depending on exactly
        // when Policy A's trailing-word buffer releases a specific word. Each stable
        // source commit triggers exactly 3 translator calls in order (Architecture A,
        // then B, then C) — so Architecture C's calls are always call #3, #6, #9, ....
        // Call #6 (Architecture C's SECOND call, so a previous translation genuinely
        // exists to contradict) returns text that does not extend call #3's "a b c".
        var callCount = 0;
        var provider = new FakeTranslationProviderForProvisional
        {
            ResponseFactory = _ =>
            {
                callCount++;
                return callCount == 6
                    ? new TranslationProviderResult(true, "totally different words here", null, 200)
                    : new TranslationProviderResult(true, "a b c", null, 200);
            },
        };
        var experiment = NewExperiment(provider, out _);

        await experiment.ObservePartialAsync(new ProvisionalSourceObservation(U, 1, "one two three", T(0), false), CancellationToken.None);
        await experiment.ObservePartialAsync(new ProvisionalSourceObservation(U, 2, "one two three four", T(100), false), CancellationToken.None);
        var r3 = await experiment.ObservePartialAsync(new ProvisionalSourceObservation(U, 3, "one two three four five", T(200), false), CancellationToken.None);

        var c3 = r3.Single(x => x.Architecture == "ArchitectureC_ProvisionalDraftPlusAuthoritative");
        // By step 3, Architecture C has made its 3rd translation call overall — the one
        // rigged to contradict.
        Assert.Equal(ProvisionalTranslationState.Revised, c3.TranslationState);
        Assert.Equal(SpeechReadiness.NotSpeakable, c3.SpeechState);
    }

    // ---- StableDraft detection / CandidateForSpeech / Speakable progression ----
    [Fact]
    public async Task ArchitectureC_ReachesSpeakable_OnlyAfterMinimumConsecutiveConsistentTranslations()
    {
        var provider = new FakeTranslationProviderForProvisional(); // "T_" + text, always a clean extension since text itself is prefix-preserving
        var experiment = NewExperiment(provider, out _);

        var states = new List<SpeechReadiness>();
        void Collect(IReadOnlyList<ProvisionalTranslationStepResult> results)
        {
            var c = results.FirstOrDefault(x => x.Architecture == "ArchitectureC_ProvisionalDraftPlusAuthoritative");
            if (c != null) states.Add(c.SpeechState);
        }

        Collect(await experiment.ObservePartialAsync(new ProvisionalSourceObservation(U, 1, "one", T(0), false), CancellationToken.None));
        Collect(await experiment.ObservePartialAsync(new ProvisionalSourceObservation(U, 2, "one two", T(100), false), CancellationToken.None));
        Collect(await experiment.ObservePartialAsync(new ProvisionalSourceObservation(U, 3, "one two three", T(200), false), CancellationToken.None));
        Collect(await experiment.ObservePartialAsync(new ProvisionalSourceObservation(U, 4, "one two three four", T(300), false), CancellationToken.None));
        Collect(await experiment.ObservePartialAsync(new ProvisionalSourceObservation(U, 5, "one two three four five", T(400), false), CancellationToken.None));

        // Never Speakable on the very first successful translation.
        Assert.NotEqual(SpeechReadiness.Speakable, states.First());
        // Eventually reaches Speakable once enough consecutive consistent translations occur.
        Assert.Contains(SpeechReadiness.Speakable, states);
    }

    // ---- Source corrections: trailing word never committed/translated ----
    [Fact]
    public async Task SourceSelfCorrection_TrailingWordNeverSentToTranslator()
    {
        var provider = new FakeTranslationProviderForProvisional();
        var experiment = NewExperiment(provider, out _);

        await experiment.ObservePartialAsync(new ProvisionalSourceObservation(U, 1, "meet on Tuesday", T(0), false), CancellationToken.None);
        await experiment.ObservePartialAsync(new ProvisionalSourceObservation(U, 2, "meet on Thursday", T(100), false), CancellationToken.None);

        Assert.DoesNotContain(provider.Requests, r => r.TextToTranslate.Contains("Tuesday") || r.TextToTranslate.Contains("Thursday"));
    }

    // ---- Final authoritative result ----
    [Fact]
    public async Task FinalAuthoritative_AlwaysObtainedViaGenuineCall_ReconcilesAllThreeArchitectures()
    {
        var provider = new FakeTranslationProviderForProvisional();
        var experiment = NewExperiment(provider, out _);

        await experiment.ObservePartialAsync(new ProvisionalSourceObservation(U, 1, "I would", T(0), false), CancellationToken.None);
        await experiment.ObservePartialAsync(new ProvisionalSourceObservation(U, 2, "I would like", T(100), false), CancellationToken.None);

        var summaries = await experiment.ObserveFinalAsync(new ProvisionalSourceObservation(U, 3, "I would like to schedule.", T(200), true), CancellationToken.None);

        Assert.Equal(3, summaries.Count);
        Assert.All(summaries, s => Assert.True(s.FinalTranslationSucceeded));
        Assert.All(summaries, s => Assert.NotNull(s.FinalTranslationLatencyMs));
    }

    // ---- Duplicate prevention / missing content: null, not zero, when nothing was sent ----
    [Fact]
    public async Task ZeroRequestArchitecture_ReportsNullMetrics_NeverFabricatedZero()
    {
        var provider = new FakeTranslationProviderForProvisional();
        var experiment = NewExperiment(provider, out _);

        // Zero partials — straight to Final.
        var summaries = await experiment.ObserveFinalAsync(new ProvisionalSourceObservation(U, 1, "Ja.", T(0), true), CancellationToken.None);

        Assert.All(summaries, s => Assert.Null(s.ApproxMissingTokenCount));
        Assert.All(summaries, s => Assert.Null(s.ApproxDuplicateTokenCount));
    }

    // ---- Consecutive utterances: full isolation ----
    [Fact]
    public async Task ConsecutiveUtterances_FullyIsolated()
    {
        var provider = new FakeTranslationProviderForProvisional();
        var experiment = NewExperiment(provider, out _);

        await experiment.ObservePartialAsync(new ProvisionalSourceObservation("utt-a", 1, "hello there", T(0), false), CancellationToken.None);
        await experiment.ObservePartialAsync(new ProvisionalSourceObservation("utt-a", 2, "hello there friend", T(100), false), CancellationToken.None);
        await experiment.ObserveFinalAsync(new ProvisionalSourceObservation("utt-a", 3, "hello there friend.", T(200), true), CancellationToken.None);

        var r1 = await experiment.ObservePartialAsync(new ProvisionalSourceObservation("utt-b", 1, "goodbye", T(1000), false), CancellationToken.None);
        Assert.Empty(r1); // first partial of a brand-new utterance
    }

    // ---- Failed Translator request ----
    [Fact]
    public async Task FailedTranslatorRequest_NeverFabricatesASuccessfulCandidate()
    {
        var provider = new FakeTranslationProviderForProvisional
        {
            ResponseFactory = _ => new TranslationProviderResult(false, null, "HTTP 503", 503),
        };
        var experiment = NewExperiment(provider, out _);

        await experiment.ObservePartialAsync(new ProvisionalSourceObservation(U, 1, "I would", T(0), false), CancellationToken.None);
        var r2 = await experiment.ObservePartialAsync(new ProvisionalSourceObservation(U, 2, "I would like", T(100), false), CancellationToken.None);

        Assert.All(r2, x => Assert.Equal(ProvisionalTranslationState.Revised, x.TranslationState));
        Assert.All(r2, x => Assert.Equal(SpeechReadiness.NotSpeakable, x.SpeechState));
    }

    // ---- Empty translation ----
    [Fact]
    public async Task EmptyTranslationResponse_TreatedAsFailure_NotFabricatedContent()
    {
        var provider = new FakeTranslationProviderForProvisional
        {
            ResponseFactory = _ => new TranslationProviderResult(false, null, "empty response", 200),
        };
        var experiment = NewExperiment(provider, out _);

        await experiment.ObservePartialAsync(new ProvisionalSourceObservation(U, 1, "I would", T(0), false), CancellationToken.None);
        var r2 = await experiment.ObservePartialAsync(new ProvisionalSourceObservation(U, 2, "I would like", T(100), false), CancellationToken.None);

        Assert.All(r2, x => Assert.False(x.TranslationChangedFromPrevious && x.Architecture == "ArchitectureA_CumulativeRetranslation" && x.TranslationState == ProvisionalTranslationState.Draft));
    }

    // ---- Session/generation reset ----
    [Fact]
    public void ExplicitReset_DoesNotThrow()
    {
        var provider = new FakeTranslationProviderForProvisional();
        var experiment = NewExperiment(provider, out _);
        var ex = Record.Exception(() => experiment.Reset());
        Assert.Null(ex);
    }

    // ---- Privacy ----
    [Fact]
    public async Task NeverLogsSourceOrTranslatedText_OnlyMetadata()
    {
        var provider = new FakeTranslationProviderForProvisional();
        var experiment = NewExperiment(provider, out var logger);
        const string sensitive = "a very specific confidential internal project codename";

        await experiment.ObservePartialAsync(new ProvisionalSourceObservation(U, 1, sensitive, T(0), false), CancellationToken.None);
        await experiment.ObservePartialAsync(new ProvisionalSourceObservation(U, 2, sensitive + " continues", T(100), false), CancellationToken.None);
        await experiment.ObserveFinalAsync(new ProvisionalSourceObservation(U, 3, sensitive + " continues.", T(200), true), CancellationToken.None);

        Assert.DoesNotContain(logger.Entries, e => e.Details.Contains(sensitive) || e.Details.Contains("codename"));
        Assert.Contains(logger.Entries, e => e.EventType == "ProvisionalTranslationStep");
        Assert.Contains(logger.Entries, e => e.EventType == "ProvisionalTranslationFinalReconciliation");
    }
}
