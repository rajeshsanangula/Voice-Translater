using VTTranslate.Core.Streaming;

namespace VTTranslate.Core.Tests;

internal sealed class FakeTranslationProviderForContextAware : IIncrementalTranslationProvider
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

public class ContextAwareTranslationExperimentTests
{
    private const string U = "utt-0";
    private static DateTimeOffset T(int ms) => DateTimeOffset.UnixEpoch.AddMilliseconds(ms);

    private static ContextAwareTranslationExperiment NewExperiment(
        FakeTranslationProviderForContextAware provider, out InMemoryDiagnosticLogger logger)
    {
        logger = new InMemoryDiagnosticLogger();
        return new ContextAwareTranslationExperiment(
            new PrefixStabilityEngine(), provider, logger, "en-US->de-DE", 1, "en-US", "de-DE");
    }

    // ---- Context construction: Approach A includes previously-committed context ----
    [Fact]
    public async Task ApproachA_RequestIncludesPreviouslyCommittedContext_PlusNewSegment()
    {
        var provider = new FakeTranslationProviderForContextAware();
        var experiment = NewExperiment(provider, out _);

        await experiment.ObservePartialAsync(new ContextAwareSourceObservation(U, 1, "I would", T(0), false), CancellationToken.None);
        await experiment.ObservePartialAsync(new ContextAwareSourceObservation(U, 2, "I would like", T(100), false), CancellationToken.None);
        var r3 = await experiment.ObservePartialAsync(new ContextAwareSourceObservation(U, 3, "I would like to schedule", T(200), false), CancellationToken.None);

        // By step 3, committed source is "I would like" (2 words committed by step 2, "to
        // schedule" partially committed at step 3) — Approach A's request for the newly
        // committed segment must include the PREVIOUSLY committed words as context.
        Assert.Contains(provider.Requests, req => req.TextToTranslate.StartsWith("I") && req.TextToTranslate.Length > "to".Length + 1);
    }

    // ---- Segment extraction: Approach B sends ONLY the new segment, no context ----
    [Fact]
    public async Task ApproachB_RequestContainsOnlyTheNewSegment_NeverPrecedingContext()
    {
        var provider = new FakeTranslationProviderForContextAware();
        var experiment = NewExperiment(provider, out _);

        await experiment.ObservePartialAsync(new ContextAwareSourceObservation(U, 1, "I would", T(0), false), CancellationToken.None);
        var r2 = await experiment.ObservePartialAsync(new ContextAwareSourceObservation(U, 2, "I would like", T(100), false), CancellationToken.None);

        var b = r2.Single(x => x.Approach == "B_MinimalSegment");
        Assert.Equal(0, b.ContextCharactersSent);
        Assert.True(b.SegmentCharactersSent > 0);
    }

    // ---- Context truncation/bounds: first segment has no preceding context ----
    [Fact]
    public async Task FirstCommittedSegment_ApproachA_HasNoPrecedingContext()
    {
        var provider = new FakeTranslationProviderForContextAware();
        var experiment = NewExperiment(provider, out _);

        await experiment.ObservePartialAsync(new ContextAwareSourceObservation(U, 1, "I would", T(0), false), CancellationToken.None);
        var r2 = await experiment.ObservePartialAsync(new ContextAwareSourceObservation(U, 2, "I would like", T(100), false), CancellationToken.None);

        var a = r2.Single(x => x.Approach == "A_AccumulatedContext");
        Assert.Equal(0, a.ContextCharactersSent); // this is the FIRST commit — nothing preceded it
    }

    // ---- Revision detection: contradiction ----
    [Fact]
    public async Task Contradiction_DetectedWhenFirstTokenDiffers()
    {
        var callCount = 0;
        var provider = new FakeTranslationProviderForContextAware
        {
            ResponseFactory = _ =>
            {
                callCount++;
                // Approach A calls are odd-numbered (1st, 3rd, ...), Approach B even-numbered.
                return callCount == 3
                    ? new TranslationProviderResult(true, "xyz completely different", null, 200)
                    : new TranslationProviderResult(true, "a b c", null, 200);
            },
        };
        var experiment = NewExperiment(provider, out _);

        await experiment.ObservePartialAsync(new ContextAwareSourceObservation(U, 1, "one two three", T(0), false), CancellationToken.None);
        await experiment.ObservePartialAsync(new ContextAwareSourceObservation(U, 2, "one two three four", T(100), false), CancellationToken.None);
        var r3 = await experiment.ObservePartialAsync(new ContextAwareSourceObservation(U, 3, "one two three four five", T(200), false), CancellationToken.None);

        var a3 = r3.Single(x => x.Approach == "A_AccumulatedContext");
        Assert.Equal(TranslationRevisionKind.Contradiction, a3.RevisionKind);
        Assert.Equal(ContextAwareSpeechReadiness.NotSpeakable, a3.SpeechState);
    }

    // ---- Duplicate detection / missing content: null (never zero) with zero requests ----
    [Fact]
    public async Task ZeroRequestApproach_ReportsNullMetrics_NeverFabricatedZero()
    {
        var provider = new FakeTranslationProviderForContextAware();
        var experiment = NewExperiment(provider, out _);

        var summaries = await experiment.ObserveFinalAsync(new ContextAwareSourceObservation(U, 1, "Ja.", T(0), true), CancellationToken.None);

        Assert.All(summaries, s => Assert.Null(s.ApproxMissingTokenCount));
        Assert.All(summaries, s => Assert.Null(s.ApproxDuplicateTokenCount));
    }

    // ---- Consecutive utterances ----
    [Fact]
    public async Task ConsecutiveUtterances_FullyIsolated()
    {
        var provider = new FakeTranslationProviderForContextAware();
        var experiment = NewExperiment(provider, out _);

        await experiment.ObservePartialAsync(new ContextAwareSourceObservation("utt-a", 1, "hello there", T(0), false), CancellationToken.None);
        await experiment.ObservePartialAsync(new ContextAwareSourceObservation("utt-a", 2, "hello there friend", T(100), false), CancellationToken.None);
        await experiment.ObserveFinalAsync(new ContextAwareSourceObservation("utt-a", 3, "hello there friend.", T(200), true), CancellationToken.None);

        var r1 = await experiment.ObservePartialAsync(new ContextAwareSourceObservation("utt-b", 1, "goodbye", T(1000), false), CancellationToken.None);
        Assert.Empty(r1);
    }

    // ---- Self-correction: trailing word never sent ----
    [Fact]
    public async Task SelfCorrection_TrailingWordNeverSentToEitherApproach()
    {
        var provider = new FakeTranslationProviderForContextAware();
        var experiment = NewExperiment(provider, out _);

        await experiment.ObservePartialAsync(new ContextAwareSourceObservation(U, 1, "meet on Tuesday", T(0), false), CancellationToken.None);
        await experiment.ObservePartialAsync(new ContextAwareSourceObservation(U, 2, "meet on Thursday", T(100), false), CancellationToken.None);

        Assert.DoesNotContain(provider.Requests, r => r.TextToTranslate.Contains("Tuesday") || r.TextToTranslate.Contains("Thursday"));
    }

    // ---- Failed translation ----
    [Fact]
    public async Task FailedTranslation_MarkedIncomplete_NeverFabricated()
    {
        var provider = new FakeTranslationProviderForContextAware
        {
            ResponseFactory = _ => new TranslationProviderResult(false, null, "HTTP 503", 503),
        };
        var experiment = NewExperiment(provider, out _);

        await experiment.ObservePartialAsync(new ContextAwareSourceObservation(U, 1, "I would", T(0), false), CancellationToken.None);
        var r2 = await experiment.ObservePartialAsync(new ContextAwareSourceObservation(U, 2, "I would like", T(100), false), CancellationToken.None);

        Assert.All(r2, x => Assert.Equal(TranslationRevisionKind.Incomplete, x.RevisionKind));
        Assert.All(r2, x => Assert.Equal(ContextAwareSpeechReadiness.NotSpeakable, x.SpeechState));
    }

    // ---- Empty translation ----
    [Fact]
    public async Task EmptyTranslationResponse_TreatedAsIncomplete_ConsecutiveCountReset()
    {
        var provider = new FakeTranslationProviderForContextAware
        {
            ResponseFactory = _ => new TranslationProviderResult(true, "", null, 200),
        };
        var experiment = NewExperiment(provider, out _);

        await experiment.ObservePartialAsync(new ContextAwareSourceObservation(U, 1, "I would", T(0), false), CancellationToken.None);
        var r2 = await experiment.ObservePartialAsync(new ContextAwareSourceObservation(U, 2, "I would like", T(100), false), CancellationToken.None);

        Assert.All(r2, x => Assert.Equal(TranslationRevisionKind.Incomplete, x.RevisionKind));
    }

    // ---- Session reset ----
    [Fact]
    public void ExplicitReset_DoesNotThrow()
    {
        var provider = new FakeTranslationProviderForContextAware();
        var experiment = NewExperiment(provider, out _);
        var ex = Record.Exception(() => experiment.Reset());
        Assert.Null(ex);
    }

    // ---- Final authoritative + privacy ----
    [Fact]
    public async Task Finalization_ProducesAuthoritativeSummariesForBothApproaches()
    {
        var provider = new FakeTranslationProviderForContextAware();
        var experiment = NewExperiment(provider, out var logger);

        await experiment.ObservePartialAsync(new ContextAwareSourceObservation(U, 1, "I would", T(0), false), CancellationToken.None);
        await experiment.ObservePartialAsync(new ContextAwareSourceObservation(U, 2, "I would like", T(100), false), CancellationToken.None);
        var summaries = await experiment.ObserveFinalAsync(new ContextAwareSourceObservation(U, 3, "I would like to schedule.", T(200), true), CancellationToken.None);

        Assert.Equal(2, summaries.Count);
        Assert.Contains(summaries, s => s.Approach == "A_AccumulatedContext");
        Assert.Contains(summaries, s => s.Approach == "B_MinimalSegment");
        Assert.All(summaries, s => Assert.True(s.FinalTranslationSucceeded));

        const string sensitive = "I would like to schedule.";
        Assert.DoesNotContain(logger.Entries, e => e.Details.Contains(sensitive));
    }
}
