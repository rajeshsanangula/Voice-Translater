namespace VTTranslate.Core.Providers;

public sealed record EligibilityDecision(bool Accepted, string Reason);

/// <summary>
/// An UTTERANCE-QUALITY gate — decides whether a recognized+translated utterance is
/// reliable enough to forward its synthesized audio downstream (to the far end of a
/// call). This is explicitly NOT speaker identification and does not claim to be: it
/// has no way to distinguish the intended user's own speech from someone else's
/// clearly-spoken speech picked up by the same microphone. It only filters
/// low-quality signals — noise blips and low-confidence misrecognitions. See
/// docs/design-notes/utterance-gating-and-push-to-talk.md for the known residual gap
/// (genuine third-party speech) and the planned future mitigation (a user-controlled
/// mute/push-to-talk control), which is a product decision, not implemented here.
///
/// Deliberately never looks at audio amplitude/RMS/volume — a quiet-but-clearly-
/// spoken utterance must pass exactly like a loud one; only Azure's own duration and
/// (when available) confidence signals are used.
/// </summary>
public static class UtteranceEligibilityGate
{
    /// <summary>
    /// Below this, an utterance is almost certainly a noise blip, not a real word.
    /// Phase 12A: lowered from 250 to 200 — real UAT evidence (Phase 11C) proved a
    /// correctly-recognized, correctly-translated genuine short word ("Ja.", 240ms,
    /// no confidence signal available) was being rejected here unconditionally, with
    /// no escape hatch, since this check runs before confidence/word-density are ever
    /// consulted. 200ms preserves 120ms of margin over the documented 80ms noise-blip
    /// test case (VeryShortBlip_BelowMinimumDuration_IsRejected, still rejected
    /// unchanged) while letting genuine short words in the 200-250ms range reach the
    /// existing confidence/word-density evaluation below — the same evaluation that
    /// already accepts longer no-confidence utterances like "Yes" at 400ms. This does
    /// narrow blip protection in the 200-250ms band specifically (a single-syllable
    /// noise event with no confidence signal could now pass the word-density fallback
    /// there) — a deliberate, disclosed trade-off, not a hidden one.
    /// </summary>
    public const double MinimumDurationMs = 200;

    /// <summary>Azure's own confidence at or above this is trusted outright.</summary>
    public const double HighConfidenceThreshold = 0.5;

    /// <summary>Azure's own confidence below this is rejected outright.</summary>
    public const double LowConfidenceThreshold = 0.2;

    // Fallback heuristic (used only when confidence is unavailable, or falls in the
    // ambiguous band between the two thresholds above) — deliberately generous so
    // real speech is not penalized: real fluent speech is roughly 1.5-4 words/sec;
    // this band is far wider than that on both sides.
    private const double FallbackMinWordsPerSecond = 0.3;
    private const double FallbackMaxWordsPerSecond = 8.0;
    private const double FallbackAppliesBelowDurationMs = 800;

    public static EligibilityDecision Evaluate(TimeSpan duration, string text, double? confidence)
    {
        if (duration.TotalMilliseconds < MinimumDurationMs)
            return new EligibilityDecision(false, $"duration {duration.TotalMilliseconds:F0}ms below minimum {MinimumDurationMs:F0}ms");

        if (confidence.HasValue)
        {
            if (confidence.Value >= HighConfidenceThreshold)
                return new EligibilityDecision(true, $"confidence {confidence.Value:F2} >= {HighConfidenceThreshold:F2}");
            if (confidence.Value < LowConfidenceThreshold)
                return new EligibilityDecision(false, $"confidence {confidence.Value:F2} < {LowConfidenceThreshold:F2}");
            // Ambiguous band — fall through to the duration/word-density tiebreaker.
        }

        var wordCount = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        var wordsPerSecond = duration.TotalSeconds > 0 ? wordCount / duration.TotalSeconds : 0;

        if (duration.TotalMilliseconds < FallbackAppliesBelowDurationMs &&
            (wordsPerSecond < FallbackMinWordsPerSecond || wordsPerSecond > FallbackMaxWordsPerSecond))
        {
            return new EligibilityDecision(false,
                $"short utterance ({duration.TotalMilliseconds:F0}ms) with atypical word density ({wordsPerSecond:F1} words/sec)");
        }

        return new EligibilityDecision(true, confidence.HasValue
            ? $"confidence {confidence.Value:F2} ambiguous but duration/word-density plausible"
            : "no confidence signal available; duration/word-density plausible");
    }
}
