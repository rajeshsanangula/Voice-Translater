using VTTranslate.Core.Providers;

namespace VTTranslate.Core.Tests;

/// <summary>
/// Covers the scenario list this gate was built to address (see the design
/// conversation): true-silence-shaped blips, intermittent noise, short noise bursts,
/// legitimate short/quiet/normal speech, consecutive utterances, and concurrency.
/// Deliberately never passes a volume/RMS signal anywhere — the gate has no such
/// parameter, which is itself part of what's being verified (requirement: never use
/// amplitude as the primary or only signal, and never penalize quiet speech).
/// </summary>
public class UtteranceEligibilityGateTests
{
    [Fact]
    public void VeryShortBlip_BelowMinimumDuration_IsRejected()
    {
        // Shaped like the kind of sub-word artifact a noise click could produce.
        var decision = UtteranceEligibilityGate.Evaluate(TimeSpan.FromMilliseconds(80), "a", confidence: null);

        Assert.False(decision.Accepted);
        Assert.Contains("minimum", decision.Reason);
    }

    [Fact]
    public void ShortNoiseBurst_LowConfidence_IsRejected()
    {
        // Duration alone wouldn't catch this (it's not a blip) — confidence must.
        var decision = UtteranceEligibilityGate.Evaluate(TimeSpan.FromMilliseconds(900), "static noise sound", confidence: 0.1);

        Assert.False(decision.Accepted);
        Assert.Contains("confidence", decision.Reason);
    }

    [Fact]
    public void LegitimateShortSpeech_OneWord_DecentConfidence_IsAccepted()
    {
        // e.g. "Yes." or "Hallo." — short, but a real word, with reasonable confidence.
        var decision = UtteranceEligibilityGate.Evaluate(TimeSpan.FromMilliseconds(400), "Yes", confidence: 0.6);

        Assert.True(decision.Accepted);
    }

    [Fact]
    public void LegitimateQuietSpeech_HighConfidenceRegardlessOfImpliedVolume_IsAccepted()
    {
        // The gate has no volume parameter at all — this test documents that a "quiet"
        // utterance is indistinguishable to the gate from a loud one; only Azure's own
        // duration/confidence for the WORDS matter, never amplitude.
        var decision = UtteranceEligibilityGate.Evaluate(TimeSpan.FromMilliseconds(1200), "I would like a coffee please", confidence: 0.75);

        Assert.True(decision.Accepted);
    }

    [Fact]
    public void NormalSpeech_HighConfidence_IsAccepted()
    {
        var decision = UtteranceEligibilityGate.Evaluate(TimeSpan.FromSeconds(2.5), "I would like to schedule a meeting tomorrow", confidence: 0.92);

        Assert.True(decision.Accepted);
    }

    [Fact]
    public void ConsecutiveUtterances_EachEvaluatedIndependently_NoSharedState()
    {
        // The gate is a pure static function — calling it repeatedly must never let an
        // earlier call's outcome leak into a later one.
        var first = UtteranceEligibilityGate.Evaluate(TimeSpan.FromMilliseconds(100), "x", confidence: 0.05); // rejected
        var second = UtteranceEligibilityGate.Evaluate(TimeSpan.FromSeconds(2), "Good morning everyone", confidence: 0.9); // accepted
        var third = UtteranceEligibilityGate.Evaluate(TimeSpan.FromMilliseconds(100), "y", confidence: 0.05); // rejected again

        Assert.False(first.Accepted);
        Assert.True(second.Accepted);
        Assert.False(third.Accepted);
    }

    [Fact]
    public void LongerUtterance_SpanningWhatWouldBeAnInternalPause_StillAcceptedOnWordDensity()
    {
        // Pauses-within-an-utterance are Azure's own segmentation's concern (it decides
        // where one utterance ends), not this gate's — this just confirms a longer
        // utterance with a lower words-per-second rate (as a natural pause would create)
        // is still accepted when duration is long enough to clear the fallback's floor.
        var decision = UtteranceEligibilityGate.Evaluate(TimeSpan.FromSeconds(3), "Well... I think... maybe tomorrow", confidence: null);

        Assert.True(decision.Accepted);
    }

    [Fact]
    public void NoConfidenceAvailable_PlausibleWordDensity_IsAccepted()
    {
        // Documents graceful degradation: when Azure doesn't supply confidence at all
        // (a real possibility for TranslationRecognizer), a normally-paced utterance is
        // still accepted via the duration/word-density fallback, never rejected merely
        // for lacking a confidence score.
        var decision = UtteranceEligibilityGate.Evaluate(TimeSpan.FromSeconds(1.5), "Please configure the cluster", confidence: null);

        Assert.True(decision.Accepted);
        Assert.Contains("no confidence signal", decision.Reason);
    }

    [Fact]
    public void NoConfidenceAvailable_ShortDurationWithAtypicalWordDensity_IsRejected()
    {
        // Many words crammed into a very short duration with no confidence to vouch for
        // it — implausible for real speech, falls back to rejection.
        var decision = UtteranceEligibilityGate.Evaluate(TimeSpan.FromMilliseconds(500), "one two three four five six seven eight nine ten", confidence: null);

        Assert.False(decision.Accepted);
    }

    [Fact]
    public void AmbiguousConfidenceBand_FallsThroughToWordDensityTiebreaker()
    {
        // Between the low and high thresholds — neither trusted nor distrusted outright.
        var accepted = UtteranceEligibilityGate.Evaluate(TimeSpan.FromSeconds(2), "That sounds like a reasonable plan", confidence: 0.35);
        var rejected = UtteranceEligibilityGate.Evaluate(TimeSpan.FromMilliseconds(400), "x y z a b c d e f", confidence: 0.35);

        Assert.True(accepted.Accepted);
        Assert.False(rejected.Accepted);
    }

    [Fact]
    public void ConcurrentCalls_FromMultipleThreads_AreIndependentAndCorrect()
    {
        // Simulates two directions (EN->DE, DE->EN) evaluating simultaneously — the
        // gate must be safe to call concurrently with no cross-contamination, since it
        // holds no mutable state at all.
        var results = new System.Collections.Concurrent.ConcurrentBag<bool>();
        Parallel.For(0, 200, i =>
        {
            var accepted = i % 2 == 0
                ? UtteranceEligibilityGate.Evaluate(TimeSpan.FromSeconds(2), "Good afternoon", confidence: 0.9).Accepted
                : UtteranceEligibilityGate.Evaluate(TimeSpan.FromMilliseconds(50), "x", confidence: 0.05).Accepted;
            results.Add(accepted);
        });

        Assert.Equal(100, results.Count(r => r));
        Assert.Equal(100, results.Count(r => !r));
    }

    [Fact]
    public void HighConfidence_OverridesShortishDurationOnceAboveMinimum()
    {
        // High confidence is trusted outright once past the absolute minimum-duration
        // floor — it doesn't also need to pass the word-density fallback.
        var decision = UtteranceEligibilityGate.Evaluate(TimeSpan.FromMilliseconds(300), "Stop", confidence: 0.95);

        Assert.True(decision.Accepted);
    }

    // ---- Phase 12A: MinimumDurationMs 250 -> 200 ----

    [Fact]
    public void RealUatCase_Ja_240ms_NoConfidence_IsNowAccepted()
    {
        // The exact, real, proven Phase 11C defect: correctly recognized, correctly
        // translated, but previously rejected outright by the 250ms floor before
        // confidence/word-density were ever consulted. Must now pass.
        var decision = UtteranceEligibilityGate.Evaluate(TimeSpan.FromMilliseconds(240), "Ja", confidence: null);

        Assert.True(decision.Accepted);
    }

    [Fact]
    public void RealUatCase_No_ComparableShortDuration_IsAccepted()
    {
        // The EN counterpart that already passed at a similar short duration in real
        // UAT — must remain accepted after the threshold change (no regression).
        var decision = UtteranceEligibilityGate.Evaluate(TimeSpan.FromMilliseconds(260), "No", confidence: null);

        Assert.True(decision.Accepted);
    }

    [Fact]
    public void ShortNoiseCase_JustAboveNewFloor_LowConfidence_IsStillRejected()
    {
        // A short burst at 210ms (above the new 200ms floor, below the old 250ms one)
        // MUST still be rejected when confidence explicitly says it's noise — the
        // threshold change only removes the unconditional block; it does not weaken
        // the existing confidence check.
        var decision = UtteranceEligibilityGate.Evaluate(TimeSpan.FromMilliseconds(210), "static", confidence: 0.05);

        Assert.False(decision.Accepted);
        Assert.Contains("confidence", decision.Reason);
    }

    [Fact]
    public void ExistingBlipCase_80ms_StillRejectedUnchanged()
    {
        // The original noise-blip protection this floor exists for is untouched —
        // 80ms remains solidly below even the new, lower 200ms floor.
        var decision = UtteranceEligibilityGate.Evaluate(TimeSpan.FromMilliseconds(80), "a", confidence: null);

        Assert.False(decision.Accepted);
        Assert.Contains("minimum", decision.Reason);
    }

    [Fact]
    public void NormalUtterance_Unaffected_ByThresholdChange()
    {
        var decision = UtteranceEligibilityGate.Evaluate(TimeSpan.FromSeconds(2.5), "I would like to schedule a meeting tomorrow", confidence: 0.92);

        Assert.True(decision.Accepted);
    }

    [Fact]
    public void JustBelowNewFloor_199ms_StillRejectedByAbsoluteFloor()
    {
        // Boundary check: the new floor is 200ms, not "anything shortish now passes."
        var decision = UtteranceEligibilityGate.Evaluate(TimeSpan.FromMilliseconds(199), "x", confidence: 0.99);

        Assert.False(decision.Accepted);
        Assert.Contains("minimum", decision.Reason);
    }
}
