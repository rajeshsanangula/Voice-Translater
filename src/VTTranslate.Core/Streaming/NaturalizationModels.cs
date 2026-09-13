namespace VTTranslate.Core.Streaming;

/// <summary>
/// EXPERIMENTAL, SHADOW-ONLY — Step 5.14. One request to naturalize an already-translated
/// baseline segment. Carries the SAME bounded-context text the translation call itself
/// used (Step 5.11's C1/C2 concept, reused unmodified — no new/unrestricted context is
/// introduced here). <see cref="TerminologyTermsToPreserve"/> is an optional, caller-
/// supplied list of domain terms the validator (see <see cref="MeaningPreservationValidator"/>)
/// checks are not dropped by naturalization; it is NOT a translation memory and is never
/// persisted.
/// </summary>
public sealed record NaturalizationRequest(
    string UtteranceId,
    int Generation,
    int SegmentSequence,
    string BaselineTranslatedText,
    string SourceLanguage,
    string TargetLanguage,
    string? BoundedContext,
    IReadOnlyList<string>? TerminologyTermsToPreserve = null);

/// <summary>
/// Result of one raw <see cref="INaturalizationProvider.NaturalizeAsync"/> call.
/// <see cref="ProviderUncertain"/> is a first-class, explicit signal — per the Step 5.14
/// contract, a provider that is uncertain about its own rewrite must say so, and the
/// pipeline treats that as an automatic fallback to baseline (see
/// <see cref="TranslationNaturalizationExperiment"/>), never as a "maybe good enough"
/// candidate to validate.
/// </summary>
public sealed record NaturalizationCandidateResult(
    bool Success,
    string? NaturalizedText,
    bool ProviderUncertain,
    string? FailureReason);

/// <summary>
/// EXPERIMENTAL, SHADOW-ONLY — Step 5.14. Isolated abstraction over ANY naturalization
/// backend (LLM-based or otherwise) — this experiment does NOT assume an LLM is used, and
/// does NOT assume an LLM is automatically better than the Step 5.5/5.6a Azure Translator
/// baseline; see docs/design-notes/translation-naturalization-experiment.md for the
/// objective comparison. Implementations must never log the text they send/receive, must
/// never persist it beyond the single call, and must load any credential exclusively from
/// environment variables (this project's existing convention — see
/// <see cref="TranslatorCredentialConfig"/> for the established pattern).
/// </summary>
public interface INaturalizationProvider
{
    Task<NaturalizationCandidateResult> NaturalizeAsync(NaturalizationRequest request, CancellationToken ct);
}

/// <summary>
/// EXPERIMENTAL, SHADOW-ONLY — Step 5.14. Outcome of the deterministic, NON-semantic
/// meaning-preservation validator (see <see cref="MeaningPreservationValidator"/>).
/// <b>REJECTED</b> — one or more hard preservation checks failed; the naturalized
/// candidate must never be used. <b>SAFE_TO_USE</b> — every check passed; the naturalized
/// candidate may replace the baseline. <b>FALLBACK_TO_BASELINE</b> — the candidate was
/// never even validated (empty/failed provider result, or the provider itself signaled
/// uncertainty) — a distinct state from REJECTED because no preservation violation was
/// found, there was simply nothing usable to check.
/// </summary>
public enum ValidationOutcome { Rejected, SafeToUse, FallbackToBaseline }

/// <summary>
/// Per-check findings from <see cref="MeaningPreservationValidator.Evaluate"/>. Every
/// boolean is true iff that check found baseline/candidate consistent — this is
/// DETERMINISTIC TEXT COMPARISON ONLY, not semantic understanding; see the class-level
/// doc comment on <see cref="MeaningPreservationValidator"/> for exactly what each check
/// can and cannot detect.
/// </summary>
public sealed record ValidationFindings(
    bool TokenCoverageOk,
    bool NegationPreserved,
    bool NumbersPreserved,
    bool DatesPreserved,
    bool NamesPreserved,
    bool TerminologyPreserved,
    bool QuestionStatementPreserved,
    bool ModalityPreserved,
    bool QualifiersPreserved,
    string? FailureReason);

/// <summary>Terminal pipeline-level outcome for one naturalization request — distinct from <see cref="ValidationOutcome"/>, which only applies once a candidate was actually validated.</summary>
public enum NaturalizationPipelineOutcome { Delivered, RejectedStaleGeneration, RejectedDuplicate, Cancelled }

/// <summary>
/// Full result for one segment's naturalization attempt. Metadata + FINAL TEXT only —
/// per Step 5.14 §12, this experiment does NOT log baseline/candidate/final text to any
/// diagnostic logger (only lengths/booleans), even though the result record itself
/// necessarily carries the text in memory for the caller (e.g., a live-test console
/// printout, never a persistent log) to use.
/// </summary>
public sealed record NaturalizationOutcomeResult(
    string UtteranceId,
    int Generation,
    int SegmentSequence,
    NaturalizationPipelineOutcome PipelineOutcome,
    ValidationOutcome? ValidationOutcome,
    string FinalText,
    string BaselineText,
    string? NaturalizedCandidateText,
    ValidationFindings? Findings,
    double? T0ToT1RequestStartMs,
    double? T1ToT2ResponseMs,
    double? T2ToT3ValidationMs,
    double? T0ToT3TotalMs,
    string? FailureReason);
