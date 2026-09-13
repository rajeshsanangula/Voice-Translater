namespace VTTranslate.Backend.Api.Security;

/// <summary>
/// Phase 6.8 — per-provider master credential configuration.
/// <see cref="SubscriptionKey"/> IS a secret (the long-lived Azure Cognitive Services
/// subscription key) and must always be committed empty — real values come from
/// environment variables / a secret manager in any real deployment, never a committed
/// value (same discipline as <c>DatabaseOptions.ConnectionString</c>/
/// <c>BillingOptions.WebhookSigningSecret</c>). <see cref="Region"/> is not a secret.
/// </summary>
public sealed class AzureProviderOptions
{
    public string? SubscriptionKey { get; set; }
    public string? Region { get; set; }
}

/// <summary>Non-secret, non-provider-specific provider-access configuration.</summary>
public sealed class ProviderAccessOptions
{
    public const string SectionName = "ProviderAccess";

    /// <summary>Requested short-lived credential lifetime. The actual issued lifetime may be shorter — see AzureProviderCredentialIssuer's own doc comment for why it can never be lengthened beyond what the provider itself grants. Defaults to 600s (10 minutes) if unset or non-positive — never "unlimited".</summary>
    public int CredentialLifetimeSeconds { get; set; } = 600;
}
