using System.Text.Json;

namespace VTTranslate.Core.Providers;

/// <summary>
/// Best-effort extraction of Azure's own NBest[0].Confidence from the raw detailed-
/// format JSON response (PropertyId.SpeechServiceResponse_JsonResult), when present.
/// Returns null — never a fabricated/inferred value — if the JSON is absent,
/// malformed, or doesn't contain a confidence field. Confidence support in this raw
/// JSON is documented for SpeechRecognizer; whether TranslationRecognizer populates
/// it is not guaranteed by the SDK's typed surface (its NBest() convenience helper is
/// typed for SpeechRecognitionResult and does not accept a TranslationRecognitionResult
/// at all), so this parser exists specifically to use it IF the service happens to
/// include it, and to degrade to null — never a guess — otherwise.
/// </summary>
public static class AzureConfidenceParser
{
    public static double? TryParse(string? detailedJson)
    {
        if (string.IsNullOrWhiteSpace(detailedJson)) return null;

        try
        {
            using var doc = JsonDocument.Parse(detailedJson);
            if (doc.RootElement.TryGetProperty("NBest", out var nBest) &&
                nBest.ValueKind == JsonValueKind.Array &&
                nBest.GetArrayLength() > 0)
            {
                var first = nBest[0];
                if (first.TryGetProperty("Confidence", out var conf) && conf.ValueKind == JsonValueKind.Number)
                    return conf.GetDouble();
            }
        }
        catch (JsonException)
        {
            // Malformed or unexpected JSON shape — treated as "confidence not available," never guessed.
        }

        return null;
    }
}
