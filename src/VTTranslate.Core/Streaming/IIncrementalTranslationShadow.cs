namespace VTTranslate.Core.Streaming;

/// <summary>
/// Step 5 — SHADOW/OBSERVATION only. Evaluates candidate incremental-translation
/// strategies against real (but never newly-called) Azure per-partial translated text,
/// correlated with Policy A's source-stability decisions. Never wired to TTS/playback —
/// see docs/design-notes/incremental-translation-shadow-evaluation.md.
/// </summary>
public interface IIncrementalTranslationShadow
{
    /// <summary>Observes one partial. Returns zero or more strategy candidates for this step (diagnostic/testing only).</summary>
    IReadOnlyList<StrategyCandidateResult> ObservePartial(IncrementalTranslationInput input);

    /// <summary>Observes the Final, reconciles every strategy's cumulative candidate against it, and resets for the next utterance.</summary>
    IReadOnlyList<StrategyFinalReconciliation> ObserveFinal(IncrementalTranslationInput input);

    void Reset();
}
