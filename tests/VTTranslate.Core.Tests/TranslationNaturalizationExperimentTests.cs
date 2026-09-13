using VTTranslate.Core.Streaming;

namespace VTTranslate.Core.Tests;

internal sealed class FakeNaturalizationProvider : INaturalizationProvider
{
    public List<NaturalizationRequest> Requests { get; } = new();
    public Func<NaturalizationRequest, NaturalizationCandidateResult>? ResponseFactory { get; set; }
    public TimeSpan Delay { get; set; } = TimeSpan.Zero;
    public bool ThrowOperationCanceled { get; set; }

    public async Task<NaturalizationCandidateResult> NaturalizeAsync(NaturalizationRequest request, CancellationToken ct)
    {
        Requests.Add(request);
        if (Delay > TimeSpan.Zero) await Task.Delay(Delay, ct);
        if (ThrowOperationCanceled) throw new OperationCanceledException();
        ct.ThrowIfCancellationRequested();
        return ResponseFactory?.Invoke(request) ?? new NaturalizationCandidateResult(true, request.BaselineTranslatedText, false, null);
    }
}

public class TranslationNaturalizationExperimentTests
{
    private static TranslationNaturalizationExperiment NewPipeline(
        out FakeNaturalizationProvider provider, out InMemoryDiagnosticLogger logger)
    {
        provider = new FakeNaturalizationProvider();
        logger = new InMemoryDiagnosticLogger();
        return new TranslationNaturalizationExperiment(provider, logger, "naturalization-test");
    }

    private static NaturalizationRequest Req(
        string uttId, int gen, int seq, string baseline, string? context = null, IReadOnlyList<string>? terms = null) =>
        new(uttId, gen, seq, baseline, "de", "en", context, terms);

    // ---- Naturalization success ----
    [Fact]
    public async Task SuccessfulNaturalization_ThatPassesValidation_UsesCandidateAsFinalText()
    {
        var pipeline = NewPipeline(out var provider, out _);
        provider.ResponseFactory = _ => new NaturalizationCandidateResult(true, "We would like to arrange a meeting.", false, null);

        var result = await pipeline.ProcessAsync(Req("u1", 1, 1, "We would like to schedule a meeting."), CancellationToken.None);

        Assert.Equal(NaturalizationPipelineOutcome.Delivered, result.PipelineOutcome);
        Assert.Equal(ValidationOutcome.SafeToUse, result.ValidationOutcome);
        Assert.Equal("We would like to arrange a meeting.", result.FinalText);
    }

    // ---- Naturalization rejection (validator finds a preservation failure) ----
    [Fact]
    public async Task NaturalizationRejectedByValidator_FallsBackToBaselineText()
    {
        var pipeline = NewPipeline(out var provider, out _);
        provider.ResponseFactory = _ => new NaturalizationCandidateResult(true, "We may schedule a meeting.", false, null); // drops "must"

        var result = await pipeline.ProcessAsync(Req("u1", 1, 1, "We must schedule a meeting."), CancellationToken.None);

        Assert.Equal(NaturalizationPipelineOutcome.Delivered, result.PipelineOutcome);
        Assert.Equal(ValidationOutcome.Rejected, result.ValidationOutcome);
        Assert.Equal("We must schedule a meeting.", result.FinalText); // baseline used
        Assert.NotNull(result.FailureReason);
    }

    // ---- Baseline fallback: provider failure ----
    [Fact]
    public async Task ProviderFailure_FallsBackToBaseline()
    {
        var pipeline = NewPipeline(out var provider, out _);
        provider.ResponseFactory = _ => new NaturalizationCandidateResult(false, null, false, "HTTP 503");

        var result = await pipeline.ProcessAsync(Req("u1", 1, 1, "The meeting is at three."), CancellationToken.None);

        Assert.Equal(ValidationOutcome.FallbackToBaseline, result.ValidationOutcome);
        Assert.Equal("The meeting is at three.", result.FinalText);
        Assert.Equal("HTTP 503", result.FailureReason);
    }

    // ---- Empty response ----
    [Fact]
    public async Task EmptyNaturalizedText_TreatedAsFallback()
    {
        var pipeline = NewPipeline(out var provider, out _);
        provider.ResponseFactory = _ => new NaturalizationCandidateResult(true, "", false, null);

        var result = await pipeline.ProcessAsync(Req("u1", 1, 1, "The meeting is at three."), CancellationToken.None);

        Assert.Equal(ValidationOutcome.FallbackToBaseline, result.ValidationOutcome);
        Assert.Equal("The meeting is at three.", result.FinalText);
    }

    // ---- Malformed response (whitespace-only) ----
    [Fact]
    public async Task WhitespaceOnlyNaturalizedText_TreatedAsFallback()
    {
        var pipeline = NewPipeline(out var provider, out _);
        provider.ResponseFactory = _ => new NaturalizationCandidateResult(true, "   ", false, null);

        var result = await pipeline.ProcessAsync(Req("u1", 1, 1, "The meeting is at three."), CancellationToken.None);

        Assert.Equal(ValidationOutcome.FallbackToBaseline, result.ValidationOutcome);
        Assert.Equal("The meeting is at three.", result.FinalText);
    }

    // ---- Provider-signaled uncertainty: never validated, always falls back ----
    [Fact]
    public async Task ProviderUncertain_NeverValidated_FallsBackToBaseline()
    {
        var pipeline = NewPipeline(out var provider, out _);
        // A candidate that would otherwise PASS validation (identical text) — but the
        // provider itself flags uncertainty, which must short-circuit validation entirely.
        provider.ResponseFactory = req => new NaturalizationCandidateResult(true, req.BaselineTranslatedText, true, null);

        var result = await pipeline.ProcessAsync(Req("u1", 1, 1, "The meeting is at three."), CancellationToken.None);

        Assert.Equal(ValidationOutcome.FallbackToBaseline, result.ValidationOutcome);
        Assert.Null(result.Findings); // proves validation never ran
        Assert.Equal("The meeting is at three.", result.FinalText);
    }

    // ---- Meaning-preservation failures: negation ----
    [Fact]
    public async Task NegationPreservationFailure_FallsBackToBaseline()
    {
        var pipeline = NewPipeline(out var provider, out _);
        provider.ResponseFactory = _ => new NaturalizationCandidateResult(true, "I did say that.", false, null);

        var result = await pipeline.ProcessAsync(Req("u1", 1, 1, "I did not say that."), CancellationToken.None);

        Assert.Equal(ValidationOutcome.Rejected, result.ValidationOutcome);
        Assert.False(result.Findings!.NegationPreserved);
        Assert.Equal("I did not say that.", result.FinalText);
    }

    // ---- Number preservation ----
    [Fact]
    public async Task NumberPreservationFailure_FallsBackToBaseline()
    {
        var pipeline = NewPipeline(out var provider, out _);
        provider.ResponseFactory = _ => new NaturalizationCandidateResult(true, "We gained 43 new customers.", false, null);

        var result = await pipeline.ProcessAsync(Req("u1", 1, 1, "We gained 42 new customers."), CancellationToken.None);

        Assert.Equal(ValidationOutcome.Rejected, result.ValidationOutcome);
        Assert.False(result.Findings!.NumbersPreserved);
    }

    // ---- Date preservation ----
    [Fact]
    public async Task DatePreservationFailure_FallsBackToBaseline()
    {
        var pipeline = NewPipeline(out var provider, out _);
        provider.ResponseFactory = _ => new NaturalizationCandidateResult(true, "The meeting is planned soon.", false, null);

        var result = await pipeline.ProcessAsync(Req("u1", 1, 1, "The meeting is planned for March 14th."), CancellationToken.None);

        Assert.Equal(ValidationOutcome.Rejected, result.ValidationOutcome);
        Assert.False(result.Findings!.DatesPreserved);
    }

    // ---- Name/entity preservation ----
    [Fact]
    public async Task NameEntityPreservationFailure_FallsBackToBaseline()
    {
        var pipeline = NewPipeline(out var provider, out _);
        provider.ResponseFactory = _ => new NaturalizationCandidateResult(true, "The presentation was prepared for the team.", false, null);

        var result = await pipeline.ProcessAsync(Req("u1", 1, 1, "Mr. Müller prepared the presentation for Ms. Schmidt."), CancellationToken.None);

        Assert.Equal(ValidationOutcome.Rejected, result.ValidationOutcome);
        Assert.False(result.Findings!.NamesPreserved);
    }

    // ---- Terminology preservation ----
    [Fact]
    public async Task TerminologyPreservationFailure_FallsBackToBaseline()
    {
        var pipeline = NewPipeline(out var provider, out _);
        provider.ResponseFactory = _ => new NaturalizationCandidateResult(true, "We need to make the service respond faster.", false, null);

        var result = await pipeline.ProcessAsync(
            Req("u1", 1, 1, "We need to reduce the latency of the API.", terms: new[] { "API", "latency" }), CancellationToken.None);

        Assert.Equal(ValidationOutcome.Rejected, result.ValidationOutcome);
        Assert.False(result.Findings!.TerminologyPreserved);
    }

    // ---- Question/statement preservation ----
    [Fact]
    public async Task QuestionStatementPreservationFailure_FallsBackToBaseline()
    {
        var pipeline = NewPipeline(out var provider, out _);
        provider.ResponseFactory = _ => new NaturalizationCandidateResult(true, "You know what time the meeting starts.", false, null);

        var result = await pipeline.ProcessAsync(Req("u1", 1, 1, "Do you know what time the meeting starts?"), CancellationToken.None);

        Assert.Equal(ValidationOutcome.Rejected, result.ValidationOutcome);
        Assert.False(result.Findings!.QuestionStatementPreserved);
    }

    // ---- Modality preservation ----
    [Fact]
    public async Task ModalityPreservationFailure_FallsBackToBaseline()
    {
        var pipeline = NewPipeline(out var provider, out _);
        provider.ResponseFactory = _ => new NaturalizationCandidateResult(true, "We sign the contract by Friday.", false, null);

        var result = await pipeline.ProcessAsync(Req("u1", 1, 1, "We must sign the contract by Friday."), CancellationToken.None);

        Assert.Equal(ValidationOutcome.Rejected, result.ValidationOutcome);
        Assert.False(result.Findings!.ModalityPreserved);
    }

    // ---- Incomplete sentence handling: naturalizer receives incomplete baseline as-is ----
    [Fact]
    public async Task IncompleteBaselineSegment_StillProcessedButFallsBackIfCoverageTooLow()
    {
        var pipeline = NewPipeline(out var provider, out _);
        provider.ResponseFactory = _ => new NaturalizationCandidateResult(true, "So, in conclusion, the results were excellent overall.", false, null);

        var result = await pipeline.ProcessAsync(Req("u1", 1, 1, "So if we could just, I mean, maybe we should..."), CancellationToken.None);

        Assert.Equal(ValidationOutcome.Rejected, result.ValidationOutcome); // wildly different completion of the incomplete thought
        Assert.Equal("So if we could just, I mean, maybe we should...", result.FinalText);
    }

    // ---- Self-correction: naturalizer must not resolve/collapse a self-correction into a single claim ----
    [Fact]
    public async Task SelfCorrection_CollapsedIntoSingleClaim_RejectedViaNumberMismatch()
    {
        var pipeline = NewPipeline(out var provider, out _);
        provider.ResponseFactory = _ => new NaturalizationCandidateResult(true, "We're meeting at 4 o'clock.", false, null);

        var result = await pipeline.ProcessAsync(Req("u1", 1, 1, "We're meeting at 3 — no, sorry, at 4 o'clock."), CancellationToken.None);

        // Collapsing a self-correction into a single final claim drops the superseded
        // number ("3") — caught here because both numbers are digit-form and the
        // regex-based number check sees the mismatch. A WORD-form self-correction
        // ("three" / "four") would NOT be caught this way — a documented validator
        // limitation (see MeaningPreservationValidator's class-level doc comment).
        Assert.Equal(ValidationOutcome.Rejected, result.ValidationOutcome);
        Assert.False(result.Findings!.NumbersPreserved);
        Assert.Equal("We're meeting at 3 — no, sorry, at 4 o'clock.", result.FinalText);
    }

    [Fact]
    public void SelfCorrection_WordFormNumbers_ValidatorLimitation_NotCaughtByNumberRegex()
    {
        // DOCUMENTED LIMITATION: word-form numbers ("three"/"four") are invisible to the
        // regex-based number check, so a collapsed self-correction using WORDS (not
        // digits) can slip through as NumbersPreserved=true. This test proves the
        // limitation exists rather than hiding it.
        var findings = MeaningPreservationValidator.Evaluate(
            "We're meeting at three — no, sorry, at four o'clock.", "We're meeting at four o'clock.", null);
        Assert.True(findings.NumbersPreserved); // limitation: word-form numbers are not extracted at all
    }

    // ---- Cancellation ----
    [Fact]
    public async Task Cancellation_DuringNaturalization_ReportedAsCancelled_UsesBaselineAsFinalText()
    {
        var pipeline = NewPipeline(out var provider, out _);
        using var cts = new CancellationTokenSource();
        provider.Delay = TimeSpan.FromSeconds(5);
        cts.CancelAfter(TimeSpan.FromMilliseconds(30));

        var result = await pipeline.ProcessAsync(Req("u1", 1, 1, "The meeting is at three."), cts.Token);

        Assert.Equal(NaturalizationPipelineOutcome.Cancelled, result.PipelineOutcome);
        Assert.Equal("The meeting is at three.", result.FinalText); // safe fallback even on cancellation
    }

    // ---- Stale-generation suppression ----
    [Fact]
    public async Task StaleGeneration_AtSubmission_RejectedWithoutCallingProvider()
    {
        var pipeline = NewPipeline(out var provider, out _);
        pipeline.AdvanceGeneration();

        var result = await pipeline.ProcessAsync(Req("u1", 1, 1, "The meeting is at three."), CancellationToken.None); // still generation 1

        Assert.Equal(NaturalizationPipelineOutcome.RejectedStaleGeneration, result.PipelineOutcome);
        Assert.Empty(provider.Requests);
        Assert.Equal("The meeting is at three.", result.FinalText); // baseline still returned even on stale rejection
    }

    [Fact]
    public async Task StaleGeneration_DuringNaturalization_DiscardedEvenThoughProviderSucceeded()
    {
        var pipeline = NewPipeline(out var provider, out _);
        provider.ResponseFactory = req =>
        {
            pipeline.AdvanceGeneration(); // bump mid-flight
            return new NaturalizationCandidateResult(true, req.BaselineTranslatedText, false, null);
        };

        var result = await pipeline.ProcessAsync(Req("u1", 1, 1, "The meeting is at three."), CancellationToken.None);

        Assert.Equal(NaturalizationPipelineOutcome.RejectedStaleGeneration, result.PipelineOutcome);
    }

    // ---- Duplicate prevention ----
    [Fact]
    public async Task DuplicateKey_RejectedOnSecondSubmission()
    {
        var pipeline = NewPipeline(out var provider, out _);

        var first = await pipeline.ProcessAsync(Req("u1", 1, 1, "The meeting is at three."), CancellationToken.None);
        var second = await pipeline.ProcessAsync(Req("u1", 1, 1, "The meeting is at three."), CancellationToken.None);

        Assert.Equal(NaturalizationPipelineOutcome.Delivered, first.PipelineOutcome);
        Assert.Equal(NaturalizationPipelineOutcome.RejectedDuplicate, second.PipelineOutcome);
        Assert.Single(provider.Requests); // provider never called twice for the same key
    }

    // ---- Consecutive segments ----
    [Fact]
    public async Task ConsecutiveSegments_EachProcessedIndependently()
    {
        var pipeline = NewPipeline(out var provider, out _);

        var r1 = await pipeline.ProcessAsync(Req("u1", 1, 1, "Segment one."), CancellationToken.None);
        var r2 = await pipeline.ProcessAsync(Req("u1", 1, 2, "Segment two."), CancellationToken.None);
        var r3 = await pipeline.ProcessAsync(Req("u1", 1, 3, "Segment three."), CancellationToken.None);

        Assert.All(new[] { r1, r2, r3 }, r => Assert.Equal(NaturalizationPipelineOutcome.Delivered, r.PipelineOutcome));
        Assert.Equal(3, provider.Requests.Count);
    }

    // ---- Provider failure (exception, not a Success=false result) ----
    [Fact]
    public async Task ProviderThrowsUnexpectedException_PropagatesRatherThanSilentlyMasking()
    {
        var pipeline = NewPipeline(out var provider, out _);
        provider.ResponseFactory = _ => throw new InvalidOperationException("simulated provider crash");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            pipeline.ProcessAsync(Req("u1", 1, 1, "The meeting is at three."), CancellationToken.None));
    }

    // ---- Timeout (modeled as cancellation via a linked timeout token, the standard .NET pattern) ----
    [Fact]
    public async Task Timeout_ViaCancellationToken_ReportedAsCancelled_BaselineStillAvailable()
    {
        var pipeline = NewPipeline(out var provider, out _);
        provider.Delay = TimeSpan.FromSeconds(5);
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));

        var result = await pipeline.ProcessAsync(Req("u1", 1, 1, "The meeting is at three."), timeoutCts.Token);

        Assert.Equal(NaturalizationPipelineOutcome.Cancelled, result.PipelineOutcome);
        Assert.Equal("The meeting is at three.", result.FinalText);
    }

    // ---- Privacy: no source/baseline/candidate text ever logged ----
    [Fact]
    public async Task NeverLogsBaselineOrCandidateText_OnlyMetadata()
    {
        var pipeline = NewPipeline(out var provider, out var logger);
        const string sensitiveBaseline = "Ein sehr spezifischer vertraulicher Satz.";
        provider.ResponseFactory = _ => new NaturalizationCandidateResult(true, "Ein anderer vertraulicher Satz mit Details.", false, null);

        await pipeline.ProcessAsync(Req("u1", 1, 1, sensitiveBaseline), CancellationToken.None);

        Assert.DoesNotContain(logger.Entries, e => e.Details.Contains("vertraulich"));
        Assert.Contains(logger.Entries, e => e.EventType == "NaturalizationOutcome");
    }

    // ---- Bounded context reused, never expanded ----
    [Fact]
    public async Task BoundedContext_PassedThroughUnmodified_NeverExpanded()
    {
        var pipeline = NewPipeline(out var provider, out _);
        await pipeline.ProcessAsync(Req("u1", 1, 1, "The meeting is at three.", context: "Previous segment text."), CancellationToken.None);

        Assert.Equal("Previous segment text.", provider.Requests[0].BoundedContext);
    }
}
