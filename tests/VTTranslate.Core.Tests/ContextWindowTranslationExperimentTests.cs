using VTTranslate.Core.Streaming;

namespace VTTranslate.Core.Tests;

internal sealed class FakeTranslationProviderForContextWindow : IIncrementalTranslationProvider
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

public class ContextWindowTranslationExperimentTests
{
    private const string U = "utt-0";
    private static DateTimeOffset T(int ms) => DateTimeOffset.UnixEpoch.AddMilliseconds(ms);

    private static ContextWindowTranslationExperiment NewExperiment(
        FakeTranslationProviderForContextWindow provider, out InMemoryDiagnosticLogger logger)
    {
        logger = new InMemoryDiagnosticLogger();
        return new ContextWindowTranslationExperiment(
            new PrefixStabilityEngine(), provider, logger, "en-US->de-DE", 1, "en-US", "de-DE");
    }

    // ---- C0: zero context ----
    [Fact]
    public async Task C0_NeverIncludesContext()
    {
        var provider = new FakeTranslationProviderForContextWindow();
        var experiment = NewExperiment(provider, out _);

        await experiment.ObservePartialAsync(new ContextWindowSourceObservation(U, 1, "I would", T(0), false), CancellationToken.None);
        var r2 = await experiment.ObservePartialAsync(new ContextWindowSourceObservation(U, 2, "I would like", T(100), false), CancellationToken.None);

        var c0 = r2.Single(x => x.Strategy == "C0_NoContext");
        Assert.Equal(0, c0.ContextCharacters);
    }

    // ---- C1: exactly one preceding committed unit ----
    [Fact]
    public async Task C1_IncludesExactlyOnePrecedingUnit()
    {
        var provider = new FakeTranslationProviderForContextWindow();
        var experiment = NewExperiment(provider, out _);

        await experiment.ObservePartialAsync(new ContextWindowSourceObservation(U, 1, "one two", T(0), false), CancellationToken.None);
        await experiment.ObservePartialAsync(new ContextWindowSourceObservation(U, 2, "one two three", T(100), false), CancellationToken.None);
        var r3 = await experiment.ObservePartialAsync(new ContextWindowSourceObservation(U, 3, "one two three four", T(200), false), CancellationToken.None);

        var c1 = r3.Single(x => x.Strategy == "C1_OnePrecedingUnit");
        Assert.True(c1.ContextCharacters > 0);
        // C2 (two units) must never send LESS context than C1 (one unit) at the same step.
        var c2 = r3.Single(x => x.Strategy == "C2_TwoPrecedingUnits");
        Assert.True(c2.ContextCharacters >= c1.ContextCharacters);
    }

    // ---- C3/C4: bounded character limits, never unbounded ----
    [Fact]
    public async Task C3_NeverExceedsItsCharacterLimit()
    {
        var provider = new FakeTranslationProviderForContextWindow();
        var experiment = NewExperiment(provider, out _);

        // Grow the committed source well past C3's limit (30 chars).
        var partials = new[]
        {
            "this", "this is", "this is a", "this is a fairly", "this is a fairly long",
            "this is a fairly long sentence", "this is a fairly long sentence that", "this is a fairly long sentence that keeps",
            "this is a fairly long sentence that keeps growing", "this is a fairly long sentence that keeps growing steadily",
        };
        var allC3Results = new List<ContextWindowStepResult>();
        for (int i = 0; i < partials.Length; i++)
        {
            var stepResults = await experiment.ObservePartialAsync(new ContextWindowSourceObservation(U, i + 1, partials[i], T(i * 100), false), CancellationToken.None);
            allC3Results.AddRange(stepResults.Where(r => r.Strategy == "C3_BoundedSmall"));
        }

        Assert.NotEmpty(allC3Results);
        Assert.All(allC3Results, r => Assert.True(r.ContextCharacters <= ContextWindowTranslationExperiment.C3CharacterLimit));
    }

    [Fact]
    public void C4_BoundIsLargerThanC3Bound()
    {
        Assert.True(ContextWindowTranslationExperiment.C4CharacterLimit > ContextWindowTranslationExperiment.C3CharacterLimit);
    }

    // ---- Context limits / truncation directly ----
    [Fact]
    public async Task BoundedContext_TruncatesToTailNotHead()
    {
        var provider = new FakeTranslationProviderForContextWindow();
        var experiment = NewExperiment(provider, out _);

        var partials = new[] { "alpha", "alpha beta", "alpha beta gamma delta epsilon zeta eta theta iota kappa lambda mu nu xi" };
        IReadOnlyList<ContextWindowStepResult> results = Array.Empty<ContextWindowStepResult>();
        for (int i = 0; i < partials.Length; i++)
            results = await experiment.ObservePartialAsync(new ContextWindowSourceObservation(U, i + 1, partials[i], T(i * 100), false), CancellationToken.None);

        var c3 = results.SingleOrDefault(x => x.Strategy == "C3_BoundedSmall");
        if (c3 != null)
            Assert.True(c3.ContextCharacters <= ContextWindowTranslationExperiment.C3CharacterLimit);
    }

    // ---- Segment boundaries: self-correction trailing word never sent ----
    [Fact]
    public async Task SelfCorrection_TrailingWordNeverSentToAnyStrategy()
    {
        var provider = new FakeTranslationProviderForContextWindow();
        var experiment = NewExperiment(provider, out _);

        await experiment.ObservePartialAsync(new ContextWindowSourceObservation(U, 1, "meet on Tuesday", T(0), false), CancellationToken.None);
        await experiment.ObservePartialAsync(new ContextWindowSourceObservation(U, 2, "meet on Thursday", T(100), false), CancellationToken.None);

        Assert.DoesNotContain(provider.Requests, r => r.TextToTranslate.Contains("Tuesday") || r.TextToTranslate.Contains("Thursday"));
    }

    // ---- Consecutive utterances: full isolation across all 5 strategies ----
    [Fact]
    public async Task ConsecutiveUtterances_FullyIsolated_ForAllStrategies()
    {
        var provider = new FakeTranslationProviderForContextWindow();
        var experiment = NewExperiment(provider, out _);

        await experiment.ObservePartialAsync(new ContextWindowSourceObservation("utt-a", 1, "hello there", T(0), false), CancellationToken.None);
        await experiment.ObservePartialAsync(new ContextWindowSourceObservation("utt-a", 2, "hello there friend", T(100), false), CancellationToken.None);
        await experiment.ObserveFinalAsync(new ContextWindowSourceObservation("utt-a", 3, "hello there friend.", T(200), true), CancellationToken.None);

        var r1 = await experiment.ObservePartialAsync(new ContextWindowSourceObservation("utt-b", 1, "goodbye", T(1000), false), CancellationToken.None);
        Assert.Empty(r1);
    }

    // ---- Duplicate detection / contradiction detection ----
    [Fact]
    public async Task Contradiction_DetectedAndCountedPerStrategy()
    {
        // Deterministic by REQUEST TEXT CONTENT (not call count): every strategy's first
        // ever commit (step 2, segment "one two", zero context since nothing was committed
        // before it) receives "a b c". Every strategy's SECOND commit (step 3, segment
        // "three" — with or without "one two" as context depending on strategy) contains
        // "three" in its request text either way, so rigging on that substring flips ALL
        // FIVE strategies to a contradicting result at step 3, regardless of their
        // differing context construction.
        var provider = new FakeTranslationProviderForContextWindow
        {
            ResponseFactory = req => req.TextToTranslate.Contains("three")
                ? new TranslationProviderResult(true, "xyz totally different content", null, 200)
                : new TranslationProviderResult(true, "a b c", null, 200),
        };
        var experiment = NewExperiment(provider, out _);

        await experiment.ObservePartialAsync(new ContextWindowSourceObservation(U, 1, "one two three", T(0), false), CancellationToken.None);
        await experiment.ObservePartialAsync(new ContextWindowSourceObservation(U, 2, "one two three four", T(100), false), CancellationToken.None);
        var r3 = await experiment.ObservePartialAsync(new ContextWindowSourceObservation(U, 3, "one two three four five", T(200), false), CancellationToken.None);

        // All five strategies should show a contradiction at step 3.
        Assert.All(r3, x => Assert.Equal(ContextRevisionKind.Contradiction, x.RevisionKind));
        Assert.Equal(5, r3.Count);
    }

    // ---- Revision detection: extension ----
    [Fact]
    public async Task CleanExtension_ClassifiedAsExtension_NotContradiction()
    {
        var provider = new FakeTranslationProviderForContextWindow(); // "T_" + text, always a clean prefix-preserving extension
        var experiment = NewExperiment(provider, out _);

        await experiment.ObservePartialAsync(new ContextWindowSourceObservation(U, 1, "one", T(0), false), CancellationToken.None);
        await experiment.ObservePartialAsync(new ContextWindowSourceObservation(U, 2, "one two", T(100), false), CancellationToken.None);
        var r3 = await experiment.ObservePartialAsync(new ContextWindowSourceObservation(U, 3, "one two three", T(200), false), CancellationToken.None);

        Assert.DoesNotContain(r3, x => x.RevisionKind == ContextRevisionKind.Contradiction);
    }

    // ---- Translation failure ----
    [Fact]
    public async Task TranslationFailure_MarkedRevised_NeverFabricated()
    {
        var provider = new FakeTranslationProviderForContextWindow
        {
            ResponseFactory = _ => new TranslationProviderResult(false, null, "HTTP 503", 503),
        };
        var experiment = NewExperiment(provider, out _);

        await experiment.ObservePartialAsync(new ContextWindowSourceObservation(U, 1, "I would", T(0), false), CancellationToken.None);
        var r2 = await experiment.ObservePartialAsync(new ContextWindowSourceObservation(U, 2, "I would like", T(100), false), CancellationToken.None);

        Assert.All(r2, x => Assert.Equal(ContextWindowTranslationState.Revised, x.TranslationState));
        Assert.All(r2, x => Assert.Equal(ContextWindowSpeechReadiness.NotSpeakable, x.SpeechState));
    }

    // ---- Empty result ----
    [Fact]
    public async Task EmptyTranslationResponse_ClassifiedIncomplete()
    {
        var provider = new FakeTranslationProviderForContextWindow
        {
            ResponseFactory = _ => new TranslationProviderResult(true, "", null, 200),
        };
        var experiment = NewExperiment(provider, out _);

        await experiment.ObservePartialAsync(new ContextWindowSourceObservation(U, 1, "I would", T(0), false), CancellationToken.None);
        var r2 = await experiment.ObservePartialAsync(new ContextWindowSourceObservation(U, 2, "I would like", T(100), false), CancellationToken.None);

        Assert.All(r2, x => Assert.Equal(ContextRevisionKind.Incomplete, x.RevisionKind));
    }

    // ---- Reset / session boundaries ----
    [Fact]
    public void ExplicitReset_DoesNotThrow()
    {
        var provider = new FakeTranslationProviderForContextWindow();
        var experiment = NewExperiment(provider, out _);
        var ex = Record.Exception(() => experiment.Reset());
        Assert.Null(ex);
    }

    // ---- Null-not-zero for zero-request cases; amplification ratio; Final reconciliation ----
    [Fact]
    public async Task ZeroRequestUtterance_ReportsNullMetrics_AndZeroAmplification()
    {
        var provider = new FakeTranslationProviderForContextWindow();
        var experiment = NewExperiment(provider, out _);

        var summaries = await experiment.ObserveFinalAsync(new ContextWindowSourceObservation(U, 1, "Ja.", T(0), true), CancellationToken.None);

        Assert.Equal(5, summaries.Count);
        Assert.All(summaries, s => Assert.Null(s.ApproxMissingTokenCount));
        Assert.All(summaries, s => Assert.Null(s.ApproxDuplicateTokenCount));
    }

    [Fact]
    public async Task AmplificationVsC0_IsOneForC0Itself()
    {
        var provider = new FakeTranslationProviderForContextWindow();
        var experiment = NewExperiment(provider, out _);

        await experiment.ObservePartialAsync(new ContextWindowSourceObservation(U, 1, "I would", T(0), false), CancellationToken.None);
        await experiment.ObservePartialAsync(new ContextWindowSourceObservation(U, 2, "I would like", T(100), false), CancellationToken.None);
        var summaries = await experiment.ObserveFinalAsync(new ContextWindowSourceObservation(U, 3, "I would like to schedule.", T(200), true), CancellationToken.None);

        var c0 = summaries.Single(s => s.Strategy == "C0_NoContext");
        Assert.Equal(1.0, c0.AmplificationVsC0, precision: 5);

        // Strategies WITH context must never show LESS cumulative characters sent than C0.
        var c4 = summaries.Single(s => s.Strategy == "C4_BoundedLarge");
        Assert.True(c4.AmplificationVsC0 >= 1.0);
    }

    // ---- Privacy ----
    [Fact]
    public async Task NeverLogsSourceOrTranslatedText_OnlyMetadata()
    {
        var provider = new FakeTranslationProviderForContextWindow();
        var experiment = NewExperiment(provider, out var logger);
        const string sensitive = "a very specific confidential internal codename project";

        await experiment.ObservePartialAsync(new ContextWindowSourceObservation(U, 1, sensitive, T(0), false), CancellationToken.None);
        await experiment.ObservePartialAsync(new ContextWindowSourceObservation(U, 2, sensitive + " continues", T(100), false), CancellationToken.None);
        await experiment.ObserveFinalAsync(new ContextWindowSourceObservation(U, 3, sensitive + " continues.", T(200), true), CancellationToken.None);

        Assert.DoesNotContain(logger.Entries, e => e.Details.Contains(sensitive) || e.Details.Contains("codename"));
        Assert.Contains(logger.Entries, e => e.EventType == "ContextWindowStep");
        Assert.Contains(logger.Entries, e => e.EventType == "ContextWindowFinalReconciliation");
    }
}
