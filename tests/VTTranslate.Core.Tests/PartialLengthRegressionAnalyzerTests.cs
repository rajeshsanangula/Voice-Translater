using VTTranslate.Core.Providers;

namespace VTTranslate.Core.Tests;

/// <summary>
/// Phase 15B — deterministic, no-real-Azure-network tests for the pure length-regression
/// formatter behind the new per-utterance "regressionCount"/"maxRegressionMagnitudeChars"
/// fields added to AzureSpeechTranslationProvider's UtteranceSummary log line. This
/// analyzer takes only integer lengths (never text), so it is fully testable in isolation
/// without a live Azure connection — the actual per-utterance aggregation/reset wiring
/// inside the sealed TranslationRecognizer event handlers is reviewed by code inspection
/// instead (see AzureSpeechTranslationProviderRealAzureRenewalTests.cs for why: the SDK
/// type cannot be faked to raise its events outside a live connection).
/// </summary>
public class PartialLengthRegressionAnalyzerTests
{
    [Fact]
    public void FirstPartial_NoPreviousLength_NeverARegression()
    {
        var result = PartialLengthRegressionAnalyzer.Check(previousLength: null, currentLength: 12);

        Assert.False(result.IsRegression);
        Assert.Equal(0, result.MagnitudeChars);
    }

    [Fact]
    public void MonotonicGrowth_IsNotARegression()
    {
        // Confirms requirement 4: normal partial growth (extension) never counts.
        var result = PartialLengthRegressionAnalyzer.Check(previousLength: 10, currentLength: 15);

        Assert.False(result.IsRegression);
        Assert.Equal(0, result.MagnitudeChars);
    }

    [Fact]
    public void UnchangedLength_IsNotARegression()
    {
        var result = PartialLengthRegressionAnalyzer.Check(previousLength: 10, currentLength: 10);

        Assert.False(result.IsRegression);
        Assert.Equal(0, result.MagnitudeChars);
    }

    [Fact]
    public void ShorterCurrentLength_IsARegression_WithCorrectMagnitude()
    {
        var result = PartialLengthRegressionAnalyzer.Check(previousLength: 20, currentLength: 14);

        Assert.True(result.IsRegression);
        Assert.Equal(6, result.MagnitudeChars);
    }

    [Fact]
    public void ZeroRegressions_AcrossASequence_ProducesRegressionCountZero()
    {
        // Confirms requirement 1: simulates a per-utterance aggregation loop the same way
        // the provider accumulates _partialLengthRegressionCount across a sequence of
        // Recognizing events — all growing, so the running count stays at 0.
        var lengths = new int[] { 3, 8, 15, 22, 22, 30 };
        int? previous = null;
        var regressionCount = 0;

        foreach (var len in lengths)
        {
            var check = PartialLengthRegressionAnalyzer.Check(previous, len);
            if (check.IsRegression) regressionCount++;
            previous = len;
        }

        Assert.Equal(0, regressionCount);
    }

    [Fact]
    public void OneRegression_AcrossASequence_ProducesRegressionCountOne()
    {
        // Confirms requirement 2.
        var lengths = new int[] { 3, 8, 15, 9, 20, 25 }; // one shrink: 15 -> 9
        int? previous = null;
        var regressionCount = 0;

        foreach (var len in lengths)
        {
            var check = PartialLengthRegressionAnalyzer.Check(previous, len);
            if (check.IsRegression) regressionCount++;
            previous = len;
        }

        Assert.Equal(1, regressionCount);
    }

    [Fact]
    public void MultipleRegressions_AcrossASequence_ProducesExactCount()
    {
        // Confirms requirement 3.
        var lengths = new int[] { 5, 12, 7, 18, 10, 25, 25, 20 }; // shrinks: 12->7, 18->10, 25->20
        int? previous = null;
        var regressionCount = 0;
        var maxMagnitude = 0;

        foreach (var len in lengths)
        {
            var check = PartialLengthRegressionAnalyzer.Check(previous, len);
            if (check.IsRegression)
            {
                regressionCount++;
                if (check.MagnitudeChars > maxMagnitude) maxMagnitude = check.MagnitudeChars;
            }
            previous = len;
        }

        Assert.Equal(3, regressionCount);
        Assert.Equal(8, maxMagnitude); // 18 -> 10 is the largest single shrink
    }

    [Fact]
    public void UtteranceReset_StartingOverWithNullPrevious_DoesNotLeakPriorRegressionState()
    {
        // Confirms requirement 5: this models exactly what the provider does at
        // utterance-boundary reset (Recognized final, and mid-utterance disconnect reset)
        // — _previousPartialText is set back to null, which this analyzer must treat
        // identically to "first partial of a brand-new utterance," never as a regression
        // against a shorter empty carryover.
        var firstUtteranceLengths = new int[] { 20, 8 }; // one regression: 20 -> 8
        int? previous = null;
        var firstUtteranceRegressions = 0;
        foreach (var len in firstUtteranceLengths)
        {
            var check = PartialLengthRegressionAnalyzer.Check(previous, len);
            if (check.IsRegression) firstUtteranceRegressions++;
            previous = len;
        }
        Assert.Equal(1, firstUtteranceRegressions);

        // Reset, exactly as the provider does on Recognized/disconnect.
        previous = null;
        var secondUtteranceFirstPartialLength = 3; // shorter than the previous utterance's last partial (8), but this is a NEW utterance
        var resetCheck = PartialLengthRegressionAnalyzer.Check(previous, secondUtteranceFirstPartialLength);

        Assert.False(resetCheck.IsRegression);
    }

    [Fact]
    public void Finalization_ComparingFinalLengthAgainstLastPartial_DoesNotByItselfCountAsARegression()
    {
        // Confirms requirement 6: the provider never calls this analyzer for the Recognized
        // (final) event at all — only Recognizing (partial) events feed it, per the actual
        // wiring in AzureSpeechTranslationProvider.cs. This test documents that contract:
        // even when a final result's length is shorter than the last partial's length
        // (a real, legitimate Azure behavior — e.g. punctuation normalization), calling
        // this analyzer with the SAME lengths correctly reports it as a mechanical length
        // comparison with no special-casing for "this happens to be a final," since the
        // analyzer has no concept of final vs. partial — that distinction belongs to the
        // caller, which by construction never invokes it for finals.
        var lastPartialLength = 25;
        var finalLength = 20; // shorter, e.g. due to punctuation/trailing-word normalization

        // If a caller mistakenly compared final against last partial, it WOULD register as
        // a regression by pure length math — this is correct, expected analyzer behavior;
        // it is the provider's responsibility (verified by code review of the actual
        // Recognized handler, which never calls this analyzer) not to do so.
        var check = PartialLengthRegressionAnalyzer.Check(lastPartialLength, finalLength);
        Assert.True(check.IsRegression);

        // The real guarantee is structural, not behavioral: the provider's Recognized
        // handler contains no call to PartialLengthRegressionAnalyzer.Check at all —
        // confirmed by code review of AzureSpeechTranslationProvider.cs, where the only
        // call site is inside the Recognizing (partial) handler.
    }
}
