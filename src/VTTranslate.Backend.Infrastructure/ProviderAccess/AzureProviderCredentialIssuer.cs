using VTTranslate.Backend.Domain.Abstractions;
using VTTranslate.Backend.Domain.Enums;

namespace VTTranslate.Backend.Infrastructure.ProviderAccess;

/// <summary>
/// Phase 6.8 — real Azure Cognitive Services STS token broker. Azure Speech and Azure
/// Translator both support the same standard delegated-token mechanism
/// (<c>POST https://{region}.api.cognitive.microsoft.com/sts/v1.0/issueToken</c>,
/// authenticated with the long-lived subscription key, returning a bearer JWT valid for
/// exactly 10 minutes) — this is the "secure short-lived scoped credential" the Phase 6.8
/// design requires (docs/phase-6.8-provider-access-gateway.md §8), not something invented
/// for this project. The long-lived subscription key is read from configuration ONLY
/// inside this class and is NEVER returned, logged, or exposed past this boundary — only
/// the STS-issued short-lived token is ever returned to a caller.
///
/// One instance of this class is registered per <see cref="Provider"/> (AzureSpeech,
/// AzureTranslator), each with its own subscription key/region/supported-capability set
/// — see Program.cs's conditional registration (only registered when that provider's
/// master key is actually configured; unconfigured providers are simply not registered,
/// so <see cref="Application.ProviderAccess.ProviderAccessGateway"/>'s issuer lookup
/// naturally reports "unsupported provider" rather than needing a separate
/// not-implemented stub).
/// </summary>
public sealed class AzureProviderCredentialIssuer(
    HttpClient httpClient,
    Provider provider,
    string subscriptionKey,
    string region,
    IReadOnlySet<ProviderCapability> supportedCapabilities) : IProviderCredentialIssuer
{
    public Provider Provider => provider;

    public bool SupportsCapability(ProviderCapability capability) => supportedCapabilities.Contains(capability);

    public async Task<IssuedProviderCredential?> IssueAsync(ProviderCapability capability, TimeSpan lifetime, CancellationToken ct)
    {
        if (!SupportsCapability(capability)) return null;

        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://{region}.api.cognitive.microsoft.com/sts/v1.0/issueToken");
        request.Headers.Add("Ocp-Apim-Subscription-Key", subscriptionKey);
        request.Content = new StringContent(string.Empty);

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException)
        {
            // Never include exception detail that could echo request/response content —
            // metadata-safe failure only (Phase 6.8 §11's logging discipline).
            return null;
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode) return null;

            var token = await response.Content.ReadAsStringAsync(ct);
            if (string.IsNullOrWhiteSpace(token)) return null;

            // Azure's own STS-issued tokens are always valid for exactly 10 minutes,
            // regardless of what this backend requests — the actual expiry returned here
            // reflects that real, provider-imposed lifetime, not this backend's
            // configured "requested" lifetime. Using Min() means this backend's
            // configuration can only ever shorten the effective lifetime, never lengthen
            // it beyond what Azure actually granted — never claim a longer-lived
            // credential than the provider actually issued.
            var azureFixedLifetime = TimeSpan.FromMinutes(10);
            var effectiveLifetime = lifetime < azureFixedLifetime ? lifetime : azureFixedLifetime;

            return new IssuedProviderCredential(token, region, DateTimeOffset.UtcNow.Add(effectiveLifetime));
        }
    }
}
