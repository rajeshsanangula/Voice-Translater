namespace VTTranslate.Core.Streaming;

/// <summary>EXPERIMENTAL, SHADOW-ONLY — Step 5.9. SOURCE-side state (mirrors Step 5.7's SourceCommitState by name, kept as a distinct type for this experiment's own independence/isolation).</summary>
public enum ProvisionalSourceState { Provisional, Stable, Final }

/// <summary>
/// EXPERIMENTAL, SHADOW-ONLY — Step 5.9. TRANSLATION-side state.
/// <see cref="Draft"/>: a successful translation, not yet confirmed against anything.
/// <see cref="StableDraft"/>: this translation did not contradict the immediately
/// preceding one (word-level agreement) — but NOT itself Speakable (see <see cref="SpeechReadiness"/>).
/// <see cref="Revised"/>: this translation contradicted the preceding one.
/// <see cref="Authoritative"/>: the real Translator result for the true final SOURCE text.
/// </summary>
public enum ProvisionalTranslationState { Draft, StableDraft, Revised, Authoritative }

/// <summary>
/// EXPERIMENTAL, SHADOW-ONLY — Step 5.9. SPEECH-readiness — deliberately a THIRD,
/// separate state machine from <see cref="ProvisionalSourceState"/> and
/// <see cref="ProvisionalTranslationState"/>. A translation is never
/// <see cref="Speakable"/> merely because it is a successful, grammatically-plausible
/// <see cref="ProvisionalTranslationState.Draft"/> — see
/// <see cref="ProvisionalTranslationTracker"/>'s doc comment for the exact, conservative,
/// defensible criterion used.
/// </summary>
public enum SpeechReadiness { NotSpeakable, CandidateForSpeech, Speakable }

public sealed record ProvisionalSourceObservation(
    string UtteranceId, int PartialSequence, string SourceText, DateTimeOffset Timestamp, bool IsFinal);

/// <summary>Per-architecture, per-step result. Metadata only — no source/translated text is ever included.</summary>
public sealed record ProvisionalTranslationStepResult(
    string Architecture,
    ProvisionalSourceState SourceState,
    ProvisionalTranslationState TranslationState,
    SpeechReadiness SpeechState,
    int PartialSequence,
    int RequestNumber,
    bool TranslationChangedFromPrevious,
    bool SupersedesPrevious,
    bool PreviousContentStillValid,
    double? LatencyMs,
    int CumulativeCandidateTokenCount);

/// <summary>Per-architecture, per-utterance summary produced at Final. Null (never zero/fabricated) whenever a metric could not be reliably calculated.</summary>
public sealed record ProvisionalTranslationUtteranceSummary(
    string UtteranceId,
    string Architecture,
    int TranslationRequestCount,
    int RevisionCount,
    int ContradictionCount,
    int CharactersSentTotal,
    int? ApproxMissingTokenCount,
    int? ApproxDuplicateTokenCount,
    double? EarliestTranslationLatencyMs,
    double? EarliestStableDraftDelayMs,
    double? EarliestCandidateForSpeechDelayMs,
    double? FinalTranslationLatencyMs,
    bool FinalTranslationSucceeded,
    bool AnyContentReachedSpeakable);
