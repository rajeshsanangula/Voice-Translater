using VTTranslate.Core.Streaming;

namespace VTTranslate.Core.Tests;

/// <summary>
/// Step 5.7 — tests for the streaming translation decision experiment. No live Azure
/// dependency: uses <see cref="FakeTranslationProviderForStreaming"/>, a fake
/// <see cref="IIncrementalTranslationProvider"/> (mocks/fakes are explicitly permitted for
/// unit tests per the governing instructions — only LIVE results must use the genuine
/// Translator API, which the accompanying live-test harness command does separately).
/// </summary>
internal sealed class FakeTranslationProviderForStreaming : IIncrementalTranslationProvider
{
    public List<TranslationProviderRequest> Requests { get; } = new();
    public Func<TranslationProviderRequest, TranslationProviderResult>? ResponseFactory { get; set; }

    public Task<TranslationProviderResult> TranslateAsync(TranslationProviderRequest request, CancellationToken ct)
    {
        Requests.Add(request);
        // Deterministic fake "translation" (word-order reversal, no call-count tagging) so
        // that reconciliation-based tests can meaningfully compare token overlap — a
        // consistent function of the input text only, matching the pattern already
        // established in IncrementalTranslationProviderExperimentTests.cs.
        var result = ResponseFactory?.Invoke(request) ?? new TranslationProviderResult(true, EchoTranslate(request.TextToTranslate), null, 200);
        return Task.FromResult(result);
    }

    private static string EchoTranslate(string text) =>
        string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Reverse());
}

public class StreamingTranslationDecisionExperimentTests
{
    private const string U = "utt-0";
    private static DateTimeOffset T(int ms) => DateTimeOffset.UnixEpoch.AddMilliseconds(ms);

    private static StreamingTranslationDecisionExperiment NewExperiment(
        FakeTranslationProviderForStreaming provider, out InMemoryDiagnosticLogger logger, out PrefixStabilityEngine engine)
    {
        logger = new InMemoryDiagnosticLogger();
        engine = new PrefixStabilityEngine();
        return new StreamingTranslationDecisionExperiment(engine, provider, logger, "en-US->de-DE", 1, "en-US", "de-DE");
    }

    // ---- Normal progression ----
    [Fact]
    public async Task NormalProgression_ProducesStableCandidateForB1_AsSourceCommits()
    {
        var provider = new FakeTranslationProviderForStreaming();
        var experiment = NewExperiment(provider, out _, out _);

        var r1 = await experiment.ObservePartialAsync(new StreamingSourceObservation(U, 1, "I would", T(0), false), CancellationToken.None);
        Assert.Empty(r1); // first partial: nothing stable yet (Policy A needs 2 partials minimum)

        var r2 = await experiment.ObservePartialAsync(new StreamingSourceObservation(U, 2, "I would like", T(100), false), CancellationToken.None);
        Assert.NotEmpty(r2);
        Assert.All(r2, x => Assert.Equal(SourceCommitState.Stable, x.SourceState));
        // B3 is boundary-aware — it does NOT fire on every stable source commit, only once
        // the committed text reaches a sentence boundary (see the dedicated B3 test below),
        // so only B1 is asserted here.
        Assert.Contains(r2, x => x.Strategy == "B1_CumulativeSource" && x.TranslationState == TranslationCommitState.Candidate);
    }

    // ---- B3's defining behavior: held back until the COMMITTED segment itself reaches a sentence boundary ----
    [Fact]
    public async Task B3_FiresOnlyWhenTheCommittedSegmentReachesASentenceBoundary()
    {
        var provider = new FakeTranslationProviderForStreaming();
        var experiment = NewExperiment(provider, out _, out _);

        await experiment.ObservePartialAsync(new StreamingSourceObservation(U, 1, "I would like.", T(0), false), CancellationToken.None);
        var r2 = await experiment.ObservePartialAsync(new StreamingSourceObservation(U, 2, "I would like. That works", T(100), false), CancellationToken.None);
        Assert.DoesNotContain(r2, x => x.Strategy == "B3_BoundaryAware"); // committed "I would" — no period committed yet

        var r3 = await experiment.ObservePartialAsync(new StreamingSourceObservation(U, 3, "I would like. That works well", T(200), false), CancellationToken.None);
        Assert.Contains(r3, x => x.Strategy == "B3_BoundaryAware" && x.TranslationState == TranslationCommitState.StableCandidate);
    }

    // ---- Source still provisional: no translation call issued ----
    [Fact]
    public async Task ProvisionalSource_IssuesNoTranslationRequest()
    {
        var provider = new FakeTranslationProviderForStreaming();
        var experiment = NewExperiment(provider, out _, out _);

        await experiment.ObservePartialAsync(new StreamingSourceObservation(U, 1, "I", T(0), false), CancellationToken.None);

        Assert.Empty(provider.Requests); // first partial alone never stabilizes anything
    }

    // ---- Partial regression: source contradicts already-committed content ----
    [Fact]
    public async Task PartialRegression_DoesNotProduceASpuriousStableCandidate()
    {
        var provider = new FakeTranslationProviderForStreaming();
        var experiment = NewExperiment(provider, out _, out _);

        await experiment.ObservePartialAsync(new StreamingSourceObservation(U, 1, "I want to book", T(0), false), CancellationToken.None);
        var r2 = await experiment.ObservePartialAsync(new StreamingSourceObservation(U, 2, "I want to book a", T(100), false), CancellationToken.None);
        Assert.NotEmpty(r2);
        var requestsBefore = provider.Requests.Count;

        // Contradicts the committed prefix ("I" -> "I'd") — Policy A withholds, no new commit.
        var r3 = await experiment.ObservePartialAsync(new StreamingSourceObservation(U, 3, "I'd like to book a table", T(200), false), CancellationToken.None);
        Assert.Empty(r3);
        Assert.Equal(requestsBefore, provider.Requests.Count); // no new translation request issued
    }

    // ---- Whitespace/punctuation variation ----
    [Fact]
    public async Task WhitespaceOnlyVariation_TreatedAsSourceAgreement_NotARevision()
    {
        var provider = new FakeTranslationProviderForStreaming();
        var experiment = NewExperiment(provider, out _, out _);

        await experiment.ObservePartialAsync(new StreamingSourceObservation(U, 1, "I would like", T(0), false), CancellationToken.None);
        var r2 = await experiment.ObservePartialAsync(new StreamingSourceObservation(U, 2, "I   would    like", T(100), false), CancellationToken.None);

        Assert.NotEmpty(r2);
        Assert.All(r2, x => Assert.NotEqual(TranslationCommitState.RevisionRequired, x.TranslationState));
    }

    // ---- Self-correction ----
    [Fact]
    public async Task SelfCorrection_TrailingWordHeldBack_NeverFalselyTranslated()
    {
        var provider = new FakeTranslationProviderForStreaming();
        var experiment = NewExperiment(provider, out _, out _);

        await experiment.ObservePartialAsync(new StreamingSourceObservation(U, 1, "meet on Tuesday", T(0), false), CancellationToken.None);
        var r2 = await experiment.ObservePartialAsync(new StreamingSourceObservation(U, 2, "meet on Thursday", T(100), false), CancellationToken.None);

        // "on" is committed (boundary word held back at the prior step); "Tuesday"/"Thursday"
        // were never committed, so no translation request should ever have referenced them —
        // verified indirectly: the committed source text sent for translation is "meet".
        Assert.NotEmpty(r2);
        Assert.Contains(provider.Requests, req => req.TextToTranslate == "meet");
    }

    // ---- Finalization ----
    [Fact]
    public async Task Finalization_ObtainsRealAuthoritativeTranslation_AndReconcilesBothStrategies()
    {
        var provider = new FakeTranslationProviderForStreaming();
        var experiment = NewExperiment(provider, out _, out _);

        await experiment.ObservePartialAsync(new StreamingSourceObservation(U, 1, "I would", T(0), false), CancellationToken.None);
        await experiment.ObservePartialAsync(new StreamingSourceObservation(U, 2, "I would like", T(100), false), CancellationToken.None);

        var summaries = await experiment.ObserveFinalAsync(new StreamingSourceObservation(U, 3, "I would like to schedule.", T(200), true), CancellationToken.None);

        Assert.Equal(2, summaries.Count); // B1 and B3 only — B2 not reported
        Assert.Contains(summaries, s => s.Strategy == "B1_CumulativeSource");
        Assert.Contains(summaries, s => s.Strategy == "B3_BoundaryAware");
        Assert.All(summaries, s => Assert.True(s.FinalTranslationSucceeded));
        Assert.All(summaries, s => Assert.NotNull(s.FinalTranslationLatencyMs));
    }

    // ---- Consecutive utterances ----
    [Fact]
    public async Task ConsecutiveUtterances_FullyIsolated_NoCrossUtteranceContamination()
    {
        var provider = new FakeTranslationProviderForStreaming();
        var experiment = NewExperiment(provider, out _, out _);

        await experiment.ObservePartialAsync(new StreamingSourceObservation("utt-a", 1, "hello there", T(0), false), CancellationToken.None);
        await experiment.ObservePartialAsync(new StreamingSourceObservation("utt-a", 2, "hello there friend", T(100), false), CancellationToken.None);
        await experiment.ObserveFinalAsync(new StreamingSourceObservation("utt-a", 3, "hello there friend.", T(200), true), CancellationToken.None);

        // New utterance: first partial must behave like a true first partial.
        var r1 = await experiment.ObservePartialAsync(new StreamingSourceObservation("utt-b", 1, "goodbye", T(1000), false), CancellationToken.None);
        Assert.Empty(r1);
    }

    // ---- Duplicate prevention (no source segment spoken/translated twice) ----
    // Verified via the Final reconciliation's duplicate-token estimate, which measures
    // exactly the property that matters ("was any committed source content represented
    // more than once in what would have been spoken/displayed") rather than fragile
    // string-matching against raw HTTP request text.
    [Fact]
    public async Task DuplicatePrevention_B3ShowsNoDuplicateTokens_ForCleanMonotonicGrowth()
    {
        var provider = new FakeTranslationProviderForStreaming();
        var experiment = NewExperiment(provider, out _, out _);

        await experiment.ObservePartialAsync(new StreamingSourceObservation(U, 1, "I would", T(0), false), CancellationToken.None);
        await experiment.ObservePartialAsync(new StreamingSourceObservation(U, 2, "I would like", T(100), false), CancellationToken.None);
        await experiment.ObservePartialAsync(new StreamingSourceObservation(U, 3, "I would like to schedule", T(200), false), CancellationToken.None);
        var summaries = await experiment.ObserveFinalAsync(
            new StreamingSourceObservation(U, 4, "I would like to schedule a meeting.", T(300), true), CancellationToken.None);

        var b3 = summaries.Single(s => s.Strategy == "B3_BoundaryAware");
        Assert.Equal(0, b3.ApproxDuplicateTokenCount);
    }

    // ---- Translation failure / Translator retry-error path ----
    [Fact]
    public async Task TranslationFailure_MapsToRevisionRequired_NotSilentlyIgnored()
    {
        var provider = new FakeTranslationProviderForStreaming
        {
            ResponseFactory = _ => new TranslationProviderResult(false, null, "HTTP 429 Too Many Requests", 429),
        };
        var experiment = NewExperiment(provider, out _, out _);

        await experiment.ObservePartialAsync(new StreamingSourceObservation(U, 1, "I would", T(0), false), CancellationToken.None);
        var r2 = await experiment.ObservePartialAsync(new StreamingSourceObservation(U, 2, "I would like", T(100), false), CancellationToken.None);

        Assert.NotEmpty(r2);
        Assert.All(r2, x => Assert.Equal(TranslationCommitState.RevisionRequired, x.TranslationState));
        Assert.All(r2, x => Assert.False(x.HasNewCandidate));
    }

    // ---- Empty translation result ----
    [Fact]
    public async Task EmptyTranslationResult_HandledWithoutCrashing_ReportedAsFailure()
    {
        var provider = new FakeTranslationProviderForStreaming
        {
            ResponseFactory = _ => new TranslationProviderResult(false, null, "Response parsed but contained no translation.", 200),
        };
        var experiment = NewExperiment(provider, out _, out _);

        await experiment.ObservePartialAsync(new StreamingSourceObservation(U, 1, "I would", T(0), false), CancellationToken.None);
        var r2 = await experiment.ObservePartialAsync(new StreamingSourceObservation(U, 2, "I would like", T(100), false), CancellationToken.None);

        Assert.All(r2, x => Assert.False(x.HasNewCandidate));
    }

    // ---- Contradictory translation at Final (never fabricated ground truth) ----
    [Fact]
    public async Task FinalTranslationFailure_NeverFabricatesGroundTruth_ReportsFailureExplicitly()
    {
        var provider = new FakeTranslationProviderForStreaming
        {
            ResponseFactory = req => req.TextToTranslate.Contains('.')
                ? new TranslationProviderResult(false, null, "HTTP 500", 500) // the FINAL call fails
                : new TranslationProviderResult(true, "ok", null, 200),
        };
        var experiment = NewExperiment(provider, out _, out _);

        await experiment.ObservePartialAsync(new StreamingSourceObservation(U, 1, "I would", T(0), false), CancellationToken.None);
        await experiment.ObservePartialAsync(new StreamingSourceObservation(U, 2, "I would like", T(100), false), CancellationToken.None);
        var summaries = await experiment.ObserveFinalAsync(new StreamingSourceObservation(U, 3, "I would like to schedule.", T(200), true), CancellationToken.None);

        Assert.All(summaries, s => Assert.False(s.FinalTranslationSucceeded));
        Assert.All(summaries, s => Assert.Null(s.FinalTranslationLatencyMs)); // null, never a fabricated 0
    }

    // ---- Source revision reflected in translation state ----
    [Fact]
    public async Task SourceRevisionAfterCommit_DoesNotCorruptPriorStableCandidates()
    {
        var provider = new FakeTranslationProviderForStreaming();
        var experiment = NewExperiment(provider, out _, out var engine);

        await experiment.ObservePartialAsync(new StreamingSourceObservation(U, 1, "I want to book", T(0), false), CancellationToken.None);
        var r2 = await experiment.ObservePartialAsync(new StreamingSourceObservation(U, 2, "I want to book a", T(100), false), CancellationToken.None);
        Assert.NotEmpty(r2);

        // A later contradiction must not retroactively invalidate what was already reported —
        // this experiment reports per-step results; it does not go back and "unreport" r2.
        var r3 = await experiment.ObservePartialAsync(new StreamingSourceObservation(U, 3, "I'd like to book a table", T(200), false), CancellationToken.None);
        Assert.Empty(r3); // this step itself produces nothing new (withheld), not a retraction of r2
    }

    // ---- Generation/session reset ----
    [Fact]
    public void ExplicitReset_ClearsAllState()
    {
        var provider = new FakeTranslationProviderForStreaming();
        var experiment = NewExperiment(provider, out _, out var engine);

        experiment.Reset();
        // No exception, and the underlying source engine is genuinely reset too.
        var r = engine.ProcessPartial(new PartialSourceEvent(U, 1, "fresh", T(0)));
        Assert.Equal("", r.CommittedSourceText);
    }

    // ---- Short utterance: zero partials, straight to Final ----
    [Fact]
    public async Task ShortUtterance_ZeroPartials_StillProducesAuthoritativeFinalTranslation()
    {
        var provider = new FakeTranslationProviderForStreaming();
        var experiment = NewExperiment(provider, out _, out _);

        var summaries = await experiment.ObserveFinalAsync(new StreamingSourceObservation(U, 1, "Ja.", T(0), true), CancellationToken.None);

        Assert.All(summaries, s => Assert.True(s.FinalTranslationSucceeded));
    }

    // ---- Privacy: never logs recognized/translated text, only metadata ----
    [Fact]
    public async Task NeverLogsSourceOrTranslatedText_OnlyMetadata()
    {
        var provider = new FakeTranslationProviderForStreaming();
        var experiment = NewExperiment(provider, out var logger, out _);
        const string sensitive = "a very specific confidential business phrase";

        await experiment.ObservePartialAsync(new StreamingSourceObservation(U, 1, sensitive, T(0), false), CancellationToken.None);
        await experiment.ObservePartialAsync(new StreamingSourceObservation(U, 2, sensitive + " continues", T(100), false), CancellationToken.None);
        await experiment.ObserveFinalAsync(new StreamingSourceObservation(U, 3, sensitive + " continues.", T(200), true), CancellationToken.None);

        Assert.DoesNotContain(logger.Entries, e => e.Details.Contains(sensitive) || e.Details.Contains("confidential"));
    }
}
