namespace VTTranslate.Core.Streaming;

/// <summary>
/// Step 5.7 — EXPERIMENTAL, SHADOW-ONLY. SOURCE-side commit state, produced by Policy A
/// (<see cref="PrefixStabilityEngine"/>, unmodified) — deliberately a SEPARATE state
/// machine from <see cref="TranslationCommitState"/> (see that type's doc comment for
/// why collapsing the two would be wrong).
/// </summary>
public enum SourceCommitState { Provisional, Stable, Final }

/// <summary>
/// Step 5.7 — EXPERIMENTAL, SHADOW-ONLY. TRANSLATION-side commit state. A stable SOURCE
/// prefix does NOT imply its translation is safe to speak — this is the whole point of
/// keeping this state machine separate from <see cref="SourceCommitState"/>:
/// <list type="bullet">
/// <item><description><b>Candidate</b>: a translation was returned but is always subject to
/// full replacement by construction (Strategy B1's wholesale-cumulative-re-translation
/// behavior) — never itself "safe," even though the source segment behind it is Stable.</description></item>
/// <item><description><b>StableCandidate</b>: a translation was returned incrementally
/// (Strategy B3) and is not itself subject to being silently discarded by the next step
/// (though it can still later be contradicted by the Final — see RevisionRequired).</description></item>
/// <item><description><b>RevisionRequired</b>: either the translation call failed, or a
/// later step contradicted a previously-issued candidate.</description></item>
/// <item><description><b>Authoritative</b>: the real Translator API's own translation of the
/// truly final SOURCE text — ground truth for reconciliation, never itself revised further.</description></item>
/// </list>
/// </summary>
public enum TranslationCommitState { Candidate, StableCandidate, RevisionRequired, Authoritative }

/// <summary>One realistic ASR-style partial or final source observation fed into the experiment.</summary>
public sealed record StreamingSourceObservation(
    string UtteranceId,
    int PartialSequence,
    string SourceText,
    DateTimeOffset Timestamp,
    bool IsFinal);

/// <summary>
/// Per-strategy, per-step result. Metadata only for logging purposes — callers must not
/// log <see cref="StreamingTranslationDecisionExperiment"/>'s in-memory candidate text
/// (it never returns any, by design — only counts/states/timings).
/// </summary>
public sealed record StreamingTranslationStepResult(
    string Strategy,
    SourceCommitState SourceState,
    TranslationCommitState TranslationState,
    int PartialSequence,
    bool HasNewCandidate,
    double? SourceCommitToTranslationRequestMs,
    double? TranslationRequestLatencyMs,
    int CumulativeCandidateTokenCount);

/// <summary>Per-strategy, per-utterance summary produced at Final. Null (not zero) whenever a metric could not be reliably calculated (e.g. no successful call ever occurred).</summary>
public sealed record StreamingTranslationUtteranceSummary(
    string UtteranceId,
    string Strategy,
    int TranslationRequestCount,
    int RevisionCount,
    int? ApproxMissingTokenCount,
    int? ApproxDuplicateTokenCount,
    double? EarliestCandidateLatencyMs,
    double? FinalTranslationLatencyMs,
    bool FinalTranslationSucceeded);
