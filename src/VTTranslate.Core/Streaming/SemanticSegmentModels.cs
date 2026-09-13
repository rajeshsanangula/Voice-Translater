namespace VTTranslate.Core.Streaming;

/// <summary>
/// EXPERIMENTAL, SHADOW-ONLY — Step 5.8. SOURCE-side state. Deliberately a SEPARATE,
/// finer-grained state machine than Step 5.7's <see cref="SourceCommitState"/> —
/// <see cref="SemanticallyComplete"/> sits strictly between <see cref="Stable"/> and
/// <see cref="Final"/>: a Stable prefix (Policy A says "unlikely to be revised") is NOT
/// automatically SemanticallyComplete (this experiment's heuristic says "looks like a
/// finished thought"). See <see cref="SemanticCompletionHeuristic"/>.
/// </summary>
public enum SemanticSourceState { Provisional, Stable, SemanticallyComplete, Final }

/// <summary>
/// EXPERIMENTAL, SHADOW-ONLY — Step 5.8. TRANSLATION-side state, analogous to Step 5.7's
/// <see cref="TranslationCommitState"/> but renamed/reused for this experiment's own
/// policies: <see cref="Candidate"/> (always-replaceable, e.g. Policy A's cumulative
/// re-translation), <see cref="SemanticallyStable"/> (translated only once the SOURCE
/// heuristic judged the segment complete — Policies B/C), <see cref="RevisionRequired"/>
/// (a failed call, or a later contradiction), <see cref="Authoritative"/> (the real
/// Final translation, ground truth).
/// </summary>
public enum SemanticTranslationState { Candidate, SemanticallyStable, RevisionRequired, Authoritative }

/// <summary>One realistic ASR-style partial or final source observation.</summary>
public sealed record SemanticSourceObservation(
    string UtteranceId,
    int PartialSequence,
    string SourceText,
    DateTimeOffset Timestamp,
    bool IsFinal);

/// <summary>Per-policy, per-step result. Metadata only — no candidate/source text is ever included.</summary>
public sealed record SemanticSegmentStepResult(
    string Policy,
    SemanticSourceState SourceState,
    SemanticTranslationState TranslationState,
    int PartialSequence,
    bool HasNewCandidate,
    string? HeuristicReason,
    double? FirstPartialToSemanticCommitMs,
    double? SemanticCommitToTranslationMs,
    double? TranslationLatencyMs,
    int CumulativeCandidateTokenCount);

/// <summary>Per-policy, per-utterance summary produced at Final. Null (never zero) whenever a metric could not be reliably calculated.</summary>
public sealed record SemanticSegmentUtteranceSummary(
    string UtteranceId,
    string Policy,
    int TranslationRequestCount,
    int RevisionCount,
    int ContradictionCount,
    int? ApproxMissingTokenCount,
    int? ApproxDuplicateTokenCount,
    double? EarliestSemanticCommitDelayMs,
    double? FirstPartialToFirstTranslatableResultMs,
    double? FinalTranslationLatencyMs,
    bool FinalTranslationSucceeded);
