namespace VTTranslate.Core.Streaming;

/// <summary>EXPERIMENTAL, SHADOW-ONLY — Step 5.11. SOURCE-side state, mirroring prior Streaming/ experiments by name but a distinct type for this experiment's own isolation.</summary>
public enum ContextWindowSourceState { Provisional, Stable, Final }

/// <summary>EXPERIMENTAL, SHADOW-ONLY — Step 5.11. TRANSLATION-side state (same shape as Steps 5.9/5.10's equivalents, a distinct type here).</summary>
public enum ContextWindowTranslationState { Draft, StableDraft, Revised, Authoritative }

/// <summary>EXPERIMENTAL, SHADOW-ONLY — Step 5.11. Speech-readiness (same conservative shape/thresholds as Steps 5.9/5.10, a distinct type here). A successful translation never becomes Speakable on its own — see <see cref="ContextWindowTranslationExperiment"/>.</summary>
public enum ContextWindowSpeechReadiness { NotSpeakable, CandidateForSpeech, Speakable }

/// <summary>Token-level HEURISTIC revision classification — not a linguistic parse. Same categories as Step 5.10's <see cref="TranslationRevisionKind"/>, kept as a distinct type for isolation, with an added "NoUsableChange" category for an identical-but-uninformative repeat.</summary>
public enum ContextRevisionKind
{
    FirstCandidate, Identical, Extension, ParaphraseOrRestructuring,
    GrammaticalRestructuring, Replacement, Contradiction, Incomplete, NoUsableChange,
}

public sealed record ContextWindowSourceObservation(
    string UtteranceId, int PartialSequence, string SourceText, DateTimeOffset Timestamp, bool IsFinal);

/// <summary>Per-strategy, per-step result. Metadata only — no source/context/translated text is ever included.</summary>
public sealed record ContextWindowStepResult(
    string Strategy,
    ContextWindowSourceState SourceState,
    ContextWindowTranslationState TranslationState,
    ContextRevisionKind RevisionKind,
    ContextWindowSpeechReadiness SpeechState,
    int PartialSequence,
    int RequestNumber,
    int SegmentCharacters,
    int ContextCharacters,
    int TotalRequestCharacters,
    double? SourceToRequestMs,
    double? TranslationLatencyMs);

/// <summary>Per-strategy, per-utterance summary produced at Final. Null (never fabricated zero) whenever a metric could not be reliably calculated.</summary>
public sealed record ContextWindowUtteranceSummary(
    string UtteranceId,
    string Strategy,
    int RequestCount,
    int ContradictionCount,
    int RevisionCount,
    int CumulativeCharactersSent,
    int MaxRequestCharacters,
    double AmplificationVsC0,
    int? ApproxMissingTokenCount,
    int? ApproxDuplicateTokenCount,
    double? EarliestLatencyMs,
    double? EarliestCandidateForSpeechDelayMs,
    double? FinalTranslationLatencyMs,
    bool FinalTranslationSucceeded,
    bool AnyContentReachedSpeakable);
