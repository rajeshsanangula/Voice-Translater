namespace VTTranslate.Core.Streaming;

/// <summary>EXPERIMENTAL, SHADOW-ONLY — Step 5.10. SOURCE-side state, mirroring prior Streaming/ experiments by name but kept as a distinct type for this experiment's own isolation.</summary>
public enum ContextAwareSourceState { Provisional, Stable, Final }

/// <summary>EXPERIMENTAL, SHADOW-ONLY — Step 5.10. Classifies how one translation candidate relates to the immediately preceding one for the SAME approach/utterance. This is a token-level HEURISTIC classification, not a linguistic/semantic judgment — see <see cref="ContextAwareTranslationExperiment"/>'s doc comment for exactly how each category is computed and its stated limitations.</summary>
public enum TranslationRevisionKind
{
    /// <summary>No previous candidate exists — nothing to compare against.</summary>
    FirstCandidate,
    /// <summary>Byte-for-byte identical to the previous candidate.</summary>
    Identical,
    /// <summary>The previous candidate is an exact token-level prefix of the new one (clean growth).</summary>
    Extension,
    /// <summary>Substantial (&gt;=50%) token overlap with the previous candidate, but not a clean prefix — heuristically labeled "paraphrase/restructuring," not a real parse.</summary>
    ParaphraseOrRestructuring,
    /// <summary>The very first token differs from the previous candidate's first token — the strongest, cheapest contradiction signal.</summary>
    Contradiction,
    /// <summary>Some overlap exists but it is below the 50% threshold — treated as a near-total replacement.</summary>
    Replacement,
    /// <summary>The candidate is empty or the call failed — nothing usable to classify.</summary>
    Incomplete,
}

/// <summary>EXPERIMENTAL, SHADOW-ONLY — Step 5.10. Speech-readiness, identical in shape and conservatism to Step 5.9's <see cref="SpeechReadiness"/> (kept as its own enum for this experiment's isolation, not implying an assignable relationship).</summary>
public enum ContextAwareSpeechReadiness { NotSpeakable, CandidateForSpeech, Speakable }

public sealed record ContextAwareSourceObservation(
    string UtteranceId, int PartialSequence, string SourceText, DateTimeOffset Timestamp, bool IsFinal);

/// <summary>Per-approach, per-step result. Metadata only — no source/context/translated text is ever included.</summary>
public sealed record ContextAwareTranslationStepResult(
    string Approach,
    ContextAwareSourceState SourceState,
    TranslationRevisionKind RevisionKind,
    ContextAwareSpeechReadiness SpeechState,
    int PartialSequence,
    int RequestNumber,
    int ContextCharactersSent,
    int SegmentCharactersSent,
    double? LatencyMs,
    int CumulativeCandidateTokenCount);

/// <summary>Per-approach, per-utterance summary produced at Final. Null (never fabricated zero) whenever a metric could not be reliably calculated.</summary>
public sealed record ContextAwareUtteranceSummary(
    string UtteranceId,
    string Approach,
    int RequestCount,
    int ContradictionCount,
    int RevisionCount,
    int TotalContextCharactersSent,
    int TotalSegmentCharactersSent,
    int? ApproxMissingTokenCount,
    int? ApproxDuplicateTokenCount,
    double? EarliestLatencyMs,
    double? EarliestCandidateForSpeechDelayMs,
    double? FinalTranslationLatencyMs,
    bool FinalTranslationSucceeded,
    bool AnyContentReachedSpeakable);
