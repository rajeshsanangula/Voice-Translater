using VTTranslate.Core.Diagnostics;

namespace VTTranslate.Core.Streaming;

/// <summary>
/// EXPERIMENTAL, SHADOW-ONLY — Step 5.14. Orchestrates ONE baseline-translated segment's
/// journey through naturalization and deterministic validation — composing, never
/// modifying, <see cref="INaturalizationProvider"/> and <see cref="MeaningPreservationValidator"/>
/// (both new to this step) plus the SAME generation-guard/duplicate-prevention pattern
/// used by every prior Streaming/ pipeline experiment since Step 5.12.
///
/// SAFETY RULES, enforced structurally (per the Step 5.14 contract):
/// <list type="bullet">
/// <item><description><b>Naturalization is never a single point of failure.</b> Provider
/// failure, timeout, cancellation, empty/malformed result, provider-signaled uncertainty,
/// or a REJECTED validation all resolve to <see cref="NaturalizationOutcomeResult.FinalText"/>
/// = the BASELINE translation — never a fabricated substitute, never silence.</description></item>
/// <item><description><b>An uncertain provider result is never validated.</b> If
/// <see cref="NaturalizationCandidateResult.ProviderUncertain"/> is true, the pipeline
/// falls back to baseline immediately, without running <see cref="MeaningPreservationValidator"/>
/// at all — per the explicit instruction "Do not allow an uncertain generated rewrite to
/// replace the baseline."</description></item>
/// <item><description><b>Stale-generation and duplicate protection</b> mirror Step
/// 5.12/5.13 exactly: the request's generation is captured at submission time and
/// re-checked both before and after the (potentially slow) naturalization call; a
/// (UtteranceId, Generation, SegmentSequence) key already processed is rejected on
/// resubmission.</description></item>
/// <item><description><b>No source, baseline, candidate, or final text is ever passed to
/// <see cref="IDiagnosticLogger"/></b> — only lengths, booleans, and the outcome
/// enum.</description></item>
/// </list>
///
/// Has no reference to production translation/TTS/playback/UI. Never wired into
/// <c>AzureSpeechTranslationProvider</c> or the production <c>DirectionPipeline</c>.
/// </summary>
public sealed class TranslationNaturalizationExperiment
{
    private readonly INaturalizationProvider _provider;
    private readonly IDiagnosticLogger _logger;
    private readonly string _sessionTag;
    private readonly HashSet<(string UtteranceId, int Generation, int SegmentSequence)> _processed = new();

    public int CurrentGeneration { get; private set; } = 1;

    public TranslationNaturalizationExperiment(INaturalizationProvider provider, IDiagnosticLogger logger, string sessionTag)
    {
        _provider = provider;
        _logger = logger;
        _sessionTag = sessionTag;
    }

    public void AdvanceGeneration() => CurrentGeneration++;

    public void Reset()
    {
        CurrentGeneration++;
        _processed.Clear();
    }

    public async Task<NaturalizationOutcomeResult> ProcessAsync(NaturalizationRequest request, CancellationToken ct)
    {
        var key = (request.UtteranceId, request.Generation, request.SegmentSequence);

        if (request.Generation != CurrentGeneration)
            return Reject(request, NaturalizationPipelineOutcome.RejectedStaleGeneration, "generation superseded before naturalization started");

        if (_processed.Contains(key))
            return Reject(request, NaturalizationPipelineOutcome.RejectedDuplicate, "already processed for this (utterance, generation, segment) key");

        var t0 = DateTimeOffset.UtcNow;
        var t1 = DateTimeOffset.UtcNow;

        NaturalizationCandidateResult candidate;
        try
        {
            candidate = await _provider.NaturalizeAsync(request, ct);
        }
        catch (OperationCanceledException)
        {
            LogOutcome(request, NaturalizationPipelineOutcome.Cancelled, null);
            return BuildResult(request, NaturalizationPipelineOutcome.Cancelled, null, request.BaselineTranslatedText, null, null,
                (t1 - t0).TotalMilliseconds, null, null, null, "cancelled during naturalization");
        }

        var t2 = DateTimeOffset.UtcNow;

        _processed.Add(key);

        if (request.Generation != CurrentGeneration)
            return Reject(request, NaturalizationPipelineOutcome.RejectedStaleGeneration, "generation superseded during naturalization — discarded, baseline never even considered");

        // Empty/failed/malformed provider result -> fall back to baseline. Never validated.
        if (!candidate.Success || string.IsNullOrWhiteSpace(candidate.NaturalizedText))
        {
            var reason = candidate.FailureReason ?? "empty or malformed naturalization result";
            LogOutcome(request, NaturalizationPipelineOutcome.Delivered, Streaming.ValidationOutcome.FallbackToBaseline);
            return BuildResult(request, NaturalizationPipelineOutcome.Delivered, Streaming.ValidationOutcome.FallbackToBaseline,
                request.BaselineTranslatedText, candidate.NaturalizedText, null,
                (t1 - t0).TotalMilliseconds, (t2 - t1).TotalMilliseconds, null, (t2 - t0).TotalMilliseconds, reason);
        }

        // Provider-signaled uncertainty -> fall back to baseline WITHOUT validating.
        // Per the explicit contract: never let an uncertain rewrite even reach validation.
        if (candidate.ProviderUncertain)
        {
            LogOutcome(request, NaturalizationPipelineOutcome.Delivered, Streaming.ValidationOutcome.FallbackToBaseline);
            return BuildResult(request, NaturalizationPipelineOutcome.Delivered, Streaming.ValidationOutcome.FallbackToBaseline,
                request.BaselineTranslatedText, candidate.NaturalizedText, null,
                (t1 - t0).TotalMilliseconds, (t2 - t1).TotalMilliseconds, null, (t2 - t0).TotalMilliseconds, "provider signaled uncertainty");
        }

        var t2b = DateTimeOffset.UtcNow;
        var findings = MeaningPreservationValidator.Evaluate(request.BaselineTranslatedText, candidate.NaturalizedText, request.TerminologyTermsToPreserve);
        var validationOutcome = MeaningPreservationValidator.Classify(findings);
        var t3 = DateTimeOffset.UtcNow;

        var finalText = validationOutcome == Streaming.ValidationOutcome.SafeToUse ? candidate.NaturalizedText : request.BaselineTranslatedText;

        LogOutcome(request, NaturalizationPipelineOutcome.Delivered, validationOutcome);
        return BuildResult(request, NaturalizationPipelineOutcome.Delivered, validationOutcome,
            finalText, candidate.NaturalizedText, findings,
            (t1 - t0).TotalMilliseconds, (t2 - t1).TotalMilliseconds, (t3 - t2b).TotalMilliseconds, (t3 - t0).TotalMilliseconds,
            validationOutcome == Streaming.ValidationOutcome.Rejected ? findings.FailureReason : null);
    }

    private NaturalizationOutcomeResult Reject(NaturalizationRequest request, NaturalizationPipelineOutcome outcome, string reason)
    {
        LogOutcome(request, outcome, null);
        return BuildResult(request, outcome, null, request.BaselineTranslatedText, null, null, null, null, null, null, reason);
    }

    private static NaturalizationOutcomeResult BuildResult(
        NaturalizationRequest request, NaturalizationPipelineOutcome pipelineOutcome, ValidationOutcome? validationOutcome,
        string finalText, string? candidateText, ValidationFindings? findings,
        double? t0ToT1, double? t1ToT2, double? t2ToT3, double? t0ToT3, string? failureReason) =>
        new(request.UtteranceId, request.Generation, request.SegmentSequence, pipelineOutcome, validationOutcome,
            finalText, request.BaselineTranslatedText, candidateText, findings, t0ToT1, t1ToT2, t2ToT3, t0ToT3, failureReason);

    private void LogOutcome(NaturalizationRequest request, NaturalizationPipelineOutcome pipelineOutcome, ValidationOutcome? validationOutcome) =>
        _logger.Log(_sessionTag, "NaturalizationOutcome",
            $"utteranceId={request.UtteranceId} generation={request.Generation} segmentSequence={request.SegmentSequence} " +
            $"baselineLength={request.BaselineTranslatedText.Length} pipelineOutcome={pipelineOutcome} validationOutcome={validationOutcome?.ToString() ?? "n/a"}");
}
