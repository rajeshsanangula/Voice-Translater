namespace VTTranslate.Core.Streaming;

/// <summary>
/// Step 5 — SHADOW/OBSERVATION only. Input to <see cref="IIncrementalTranslationShadow"/>
/// for one partial or final event. Deliberately does NOT trigger any new Azure API call —
/// <see cref="LatestPartialTranslatedText"/>/<see cref="FinalTranslatedText"/> are Azure's
/// own, already-produced per-partial/final translated text (the same
/// <c>e.Result.Translations</c> value the production pipeline already reads), correlated
/// here with Policy A's (<see cref="PrefixStabilityEngine"/>, unmodified) source-stability
/// decisions. See docs/design-notes/incremental-translation-shadow-evaluation.md §2 for
/// why this design avoids introducing a new external translation dependency.
/// </summary>
public sealed record IncrementalTranslationInput(
    string UtteranceId,
    int SegmentSequence,
    string SourceLanguage,
    string TargetLanguage,
    string? NewlyStableSourceSegment,
    string CumulativeStableSource,
    string LatestPartialTranslatedText,
    bool IsFinal,
    string? FinalSourceText = null,
    string? FinalTranslatedText = null);

public enum IncrementalCandidateRelation { None, Extends, Replaces, Revises }

/// <summary>
/// One strategy's diagnostic result for one step. <see cref="CandidateTranslatedText"/> and
/// <see cref="CumulativeTranslatedText"/> exist ONLY in memory for comparison/testing —
/// never passed to a logger. See <see cref="IIncrementalTranslationShadow"/>.
/// </summary>
public sealed record StrategyCandidateResult(
    string StrategyName,
    string CandidateTranslatedText,
    int SegmentSequence,
    int CumulativeSourcePositionTokens,
    string CumulativeTranslatedText,
    IncrementalCandidateRelation RelationToPrevious,
    bool FinalReconciled = false,
    bool FinalReconciliationHadDivergence = false);

/// <summary>
/// Structural (token-multiset, NOT semantic) reconciliation of one strategy's cumulative
/// candidate against the true final translation. No LLM/semantic judgment is made — this
/// is an honest, explicitly-labeled approximation using token overlap only. See
/// docs/design-notes/incremental-translation-shadow-evaluation.md §10.
/// </summary>
public sealed record StrategyFinalReconciliation(
    string StrategyName,
    int CumulativeTokenCount,
    int FinalTokenCount,
    int ApproxMissingTokenCount,
    int ApproxDuplicateTokenCount,
    int RevisionCount,
    int ChunkCount);
