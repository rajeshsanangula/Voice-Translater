namespace VTTranslate.Core.Providers;

/// <summary>
/// Phase 14B — pure, static, deterministic formatter for the diagnostic detail string
/// logged when Azure's <c>Recognized</c> event fires with a result that is NOT
/// <c>ResultReason.TranslatedSpeech</c> (e.g. <c>NoMatch</c>). Extracted as a standalone
/// pure function (rather than inlined in <see cref="AzureSpeechTranslationProvider"/>)
/// specifically so this metadata-formatting logic can be unit-tested deterministically,
/// without a real Azure connection — the SDK's <c>TranslationRecognizer</c> is sealed and
/// its events cannot otherwise be raised outside a live connection.
///
/// Metadata only: takes the result's <c>Reason</c>, <c>Duration</c>, and offset — never
/// the recognized/translated text. Returns null for <c>TranslatedSpeech</c> (that case is
/// handled and logged separately, unchanged, by the existing "RecognitionEvent"/
/// "UtteranceEvaluated" log lines) so callers have a single, obvious signal for "nothing
/// to log here."
/// </summary>
public static class RecognitionResultObservability
{
    public static string? DescribeNonFinalReason(Microsoft.CognitiveServices.Speech.ResultReason reason, TimeSpan duration, long offsetInTicks)
    {
        if (reason == Microsoft.CognitiveServices.Speech.ResultReason.TranslatedSpeech)
            return null;

        return $"reason={reason} durationMs={duration.TotalMilliseconds:F0} offsetTicks={offsetInTicks}";
    }
}
