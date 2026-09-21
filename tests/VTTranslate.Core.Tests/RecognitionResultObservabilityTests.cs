using Microsoft.CognitiveServices.Speech;
using VTTranslate.Core.Providers;

namespace VTTranslate.Core.Tests;

/// <summary>
/// Phase 14B — deterministic, no-real-Azure-network tests for the pure formatter behind
/// the new NoMatch/non-final observability logging in
/// <see cref="AzureSpeechTranslationProvider"/>. The formatter is the only Azure-facing
/// logic that decides WHAT gets logged for a non-TranslatedSpeech Recognized event; the
/// wiring itself (that this formatter's output reaches _logger.Log, that TranslatedSpeech
/// is unaffected, that the result never becomes FinalResult) cannot be exercised without a
/// live Azure connection (TranslationRecognizer is sealed) and is covered instead by code
/// review + the existing real-Azure test conventions in this project (see
/// AzureSpeechTranslationProviderRealAzureRenewalTests.cs for that pattern).
/// </summary>
public class RecognitionResultObservabilityTests
{
    [Fact]
    public void TranslatedSpeech_ReturnsNull_NotLoggedByThisPath()
    {
        // Confirms requirement 1 (Part B): the TranslatedSpeech case is explicitly
        // excluded here — it continues to be logged by the existing, unchanged
        // "RecognitionEvent"/"UtteranceEvaluated" lines, not duplicated by this new path.
        var detail = RecognitionResultObservability.DescribeNonFinalReason(
            ResultReason.TranslatedSpeech, TimeSpan.FromSeconds(2), offsetInTicks: 123456789);

        Assert.Null(detail);
    }

    [Fact]
    public void NoMatch_ProducesNonNullMetadataOnlyDetail()
    {
        // Confirms requirement 2: NoMatch is logged (produces a non-null detail string).
        var detail = RecognitionResultObservability.DescribeNonFinalReason(
            ResultReason.NoMatch, TimeSpan.FromMilliseconds(7141), offsetInTicks: 5000000);

        Assert.NotNull(detail);
        Assert.Contains("reason=NoMatch", detail);
        Assert.Contains("durationMs=7141", detail);
        Assert.Contains("offsetTicks=5000000", detail);
    }

    [Fact]
    public void NoMatch_DetailContainsOnlyNumericAndEnumMetadata_NeverTranscriptContent()
    {
        // Confirms requirement 4: no transcript/translation text can appear in the output,
        // because the function's signature accepts no text parameter at all — this test
        // documents that contract explicitly by asserting the exact, fully-enumerable
        // shape of the string (three metadata fields only).
        var detail = RecognitionResultObservability.DescribeNonFinalReason(
            ResultReason.NoMatch, TimeSpan.FromMilliseconds(500), offsetInTicks: 0);

        Assert.Equal("reason=NoMatch durationMs=500 offsetTicks=0", detail);
    }

    [Theory]
    [InlineData(ResultReason.RecognizedSpeech)]
    [InlineData(ResultReason.RecognizingSpeech)]
    [InlineData(ResultReason.Canceled)]
    public void OtherNonTranslatedSpeechReasons_AreSafelyHandled_ProduceNonNullDetail(ResultReason reason)
    {
        // Confirms requirement 5: any reason other than TranslatedSpeech is handled
        // uniformly (no exceptions, no special-casing per reason) and produces a
        // loggable, metadata-only detail string.
        var detail = RecognitionResultObservability.DescribeNonFinalReason(
            reason, TimeSpan.FromMilliseconds(1000), offsetInTicks: 10);

        Assert.NotNull(detail);
        Assert.Contains($"reason={reason}", detail);
    }
}
