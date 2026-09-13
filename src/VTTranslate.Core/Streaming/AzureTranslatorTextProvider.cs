using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace VTTranslate.Core.Streaming;

/// <summary>
/// EXPERIMENTAL, SHADOW-ONLY — Step 5.5/5.6a. Real client for the genuine, documented,
/// supported Azure AI Translator "Text Translation" REST API (v3.0,
/// api.cognitive.microsofttranslator.com by default) — a DIFFERENT Azure service from the
/// Speech Translation this project already uses (which bundles ASR+MT into one call via
/// `TranslationRecognizer`). This is NOT an invented API. It requires its OWN dedicated
/// credentials (a Translator, or multi-service Cognitive Services, resource) — a
/// single-service Speech resource's key does not automatically grant access to it. Per
/// Step 5.6a, this class NEVER reads `AZURE_SPEECH_KEY`/`AZURE_SPEECH_REGION` itself and
/// never falls back to them; its caller must supply dedicated Translator credentials
/// (see <see cref="TranslatorCredentialLoader"/>) or construction/use fails clearly. This
/// class makes the real call and reports whatever the service actually says (including a
/// 401) rather than assuming success — see
/// docs/design-notes/incremental-translation-provider-feasibility.md §2, §16.
///
/// The key/region/endpoint are read once at construction and used only in the
/// Authorization headers / request URL of the real HTTP request — never logged, never
/// included in any exception message, never returned in <see cref="TranslationProviderResult"/>.
/// </summary>
public sealed class AzureTranslatorTextProvider : IIncrementalTranslationProvider
{
    public const string DefaultEndpoint = "https://api.cognitive.microsofttranslator.com";

    private readonly HttpClient _httpClient;
    private readonly string _subscriptionKey;
    private readonly string _region;
    private readonly string _endpoint;

    /// <param name="httpClient">
    /// Injected for testability — a fake handler can simulate any status code without a
    /// real network call. Production/live use passes a real <see cref="HttpClient"/>.
    /// </param>
    /// <param name="subscriptionKey">
    /// A dedicated Azure Translator (or multi-service Cognitive Services) resource key.
    /// Must NOT be the Speech-only <c>AZURE_SPEECH_KEY</c> — see class doc comment.
    /// </param>
    /// <param name="region">The Translator resource's region, e.g. "eastus".</param>
    /// <param name="endpoint">
    /// Optional override of the Translator API base endpoint (e.g. for a regional or
    /// custom-domain Translator resource). Defaults to <see cref="DefaultEndpoint"/>, the
    /// public global endpoint, which is what the real Translator API actually requires
    /// when no custom endpoint has been provisioned — most Translator resources use it
    /// unmodified, so this is optional, not mandatory, per the "use the endpoint only if
    /// required" instruction.
    /// </param>
    public AzureTranslatorTextProvider(HttpClient httpClient, string subscriptionKey, string region, string? endpoint = null)
    {
        _httpClient = httpClient;
        _subscriptionKey = subscriptionKey;
        _region = region;
        _endpoint = string.IsNullOrWhiteSpace(endpoint) ? DefaultEndpoint : endpoint.TrimEnd('/');
    }

    public static AzureTranslatorTextProvider FromCredentialConfig(HttpClient httpClient, TranslatorCredentialConfig config) =>
        new(httpClient, config.Key, config.Region, config.Endpoint);

    public async Task<TranslationProviderResult> TranslateAsync(TranslationProviderRequest request, CancellationToken ct)
    {
        var textToSend = string.IsNullOrEmpty(request.BoundedContext)
            ? request.TextToTranslate
            : $"{request.BoundedContext} {request.TextToTranslate}"; // simplest available way to give context — see doc §6 limitation

        var url = $"{_endpoint}/translate?api-version=3.0&from={Uri.EscapeDataString(request.SourceLanguage)}&to={Uri.EscapeDataString(ToTwoLetter(request.TargetLanguage))}";
        var body = JsonSerializer.Serialize(new[] { new { Text = textToSend } });

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        httpRequest.Headers.Add("Ocp-Apim-Subscription-Key", _subscriptionKey);
        httpRequest.Headers.Add("Ocp-Apim-Subscription-Region", _region);
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        try
        {
            using var response = await _httpClient.SendAsync(httpRequest, ct);
            var statusCode = (int)response.StatusCode;

            if (!response.IsSuccessStatusCode)
            {
                return new TranslationProviderResult(
                    Success: false, CandidateTranslatedText: null,
                    FailureReason: $"HTTP {statusCode} {response.ReasonPhrase}", HttpStatusCode: statusCode);
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            var translatedText = ParseFirstTranslation(json);
            return translatedText != null
                ? new TranslationProviderResult(true, translatedText, null, statusCode)
                : new TranslationProviderResult(false, null, "Response parsed but contained no translation.", statusCode);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new TranslationProviderResult(false, null, $"{ex.GetType().Name}: request failed", null);
        }
    }

    private static string? ParseFirstTranslation(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0) return null;
        var first = root[0];
        if (!first.TryGetProperty("translations", out var translations) || translations.GetArrayLength() == 0) return null;
        return translations[0].TryGetProperty("text", out var text) ? text.GetString() : null;
    }

    private static string ToTwoLetter(string bcp47) => bcp47.Split('-')[0];
}
