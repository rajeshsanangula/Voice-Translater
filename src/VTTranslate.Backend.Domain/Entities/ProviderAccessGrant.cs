using VTTranslate.Backend.Domain.Enums;

namespace VTTranslate.Backend.Domain.Entities;

/// <summary>
/// Phase 6.8 — an audit/persistence record of one successful provider-access issuance.
/// Deliberately does NOT store the issued credential/token value itself — only metadata
/// about the grant (who, what, when, until when). The short-lived credential is returned
/// to the caller once, over the API response, and never persisted or logged anywhere
/// (docs/phase-6.8-provider-access-gateway.md).
/// </summary>
public sealed class ProviderAccessGrant
{
    public required Guid Id { get; init; }
    public required Guid AccountId { get; init; }
    public required Guid DeviceId { get; init; }
    public required Provider Provider { get; init; }
    public required ProviderCapability Capability { get; init; }
    public required DateTimeOffset IssuedAt { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }

    /// <summary>Correlates this grant to the audit trail and to any downstream usage-recording that references the same issuance — never a token/secret value.</summary>
    public required Guid CorrelationId { get; init; }
}
