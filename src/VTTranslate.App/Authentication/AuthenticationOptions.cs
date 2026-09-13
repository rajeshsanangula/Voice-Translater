namespace VTTranslate.App.Authentication;

/// <summary>
/// Phase 7.1 — non-secret customer-client configuration
/// (docs/phase-7.1-customer-authentication-client-and-entra-integration.md §26).
/// <see cref="Authority"/>/<see cref="ClientId"/>/<see cref="ApiScope"/>/
/// <see cref="ApiBaseUrl"/> are all public, non-secret values (a public client has no
/// client secret to protect at all — §40 principle 13). Loaded from environment
/// variables here, mirroring the existing <c>VTTranslate.Core.Config.AppSettings</c>
/// convention; committed defaults are placeholders only, never real tenant/production
/// values, and a missing value fails closed (see <see cref="IsConfigured"/>) rather
/// than falling back to some other identity source.
/// </summary>
public sealed class AuthenticationOptions
{
    public string Authority { get; }
    public string ClientId { get; }
    public string ApiScope { get; }
    public string ApiBaseUrl { get; }

    public AuthenticationOptions(string authority, string clientId, string apiScope, string apiBaseUrl)
    {
        Authority = authority;
        ClientId = clientId;
        ApiScope = apiScope;
        ApiBaseUrl = apiBaseUrl;
    }

    /// <summary>True only when every value required to authenticate and call the AUTRAXIS API is present. False (never a fabricated default) if any is missing — the application must show a configuration error rather than silently operate unauthenticated or against a wrong endpoint.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Authority) &&
        !string.IsNullOrWhiteSpace(ClientId) &&
        !string.IsNullOrWhiteSpace(ApiScope) &&
        !string.IsNullOrWhiteSpace(ApiBaseUrl);

    /// <summary>
    /// Loads configuration from environment variables — no real tenant ID, client ID,
    /// or production domain is ever hardcoded here (per explicit instruction). Each
    /// environment (Development/Staging/Production) supplies its own values via these
    /// variables at build/publish/deployment time, exactly mirroring the backend's own
    /// <c>Identity:Authority</c>/<c>Identity:Audience</c> environment-supplied pattern.
    /// </summary>
    public static AuthenticationOptions FromEnvironment() => new(
        authority: Environment.GetEnvironmentVariable("AUTRAXIS_CUSTOMER_AUTHORITY") ?? string.Empty,
        clientId: Environment.GetEnvironmentVariable("AUTRAXIS_CUSTOMER_CLIENT_ID") ?? string.Empty,
        apiScope: Environment.GetEnvironmentVariable("AUTRAXIS_CUSTOMER_API_SCOPE") ?? string.Empty,
        apiBaseUrl: Environment.GetEnvironmentVariable("AUTRAXIS_API_BASE_URL") ?? string.Empty);
}
