namespace VTTranslate.Backend.Domain.Entities;

/// <summary>
/// Metadata-only security/audit event — see Phase 6.1 §8/§10. <see cref="Metadata"/> must
/// NEVER contain secrets, tokens, passwords, or recognized/translated speech content,
/// consistent with this repository's existing diagnostic-logging convention (e.g.
/// <c>VTTranslate.Core.Diagnostics.FileDiagnosticLogger</c>) extended to the backend.
/// </summary>
public sealed class AuditEvent
{
    public required Guid Id { get; init; }

    /// <summary>Null for system-level events not tied to one account.</summary>
    public Guid? AccountId { get; init; }

    /// <summary>e.g. "Login", "DeviceRevoked", "SubscriptionChanged", "ProviderTokenIssued" — a free-form string rather than a closed enum, since the exhaustive list of audit-worthy events will grow across later phases without this entity needing to change.</summary>
    public required string EventType { get; init; }

    /// <summary>Metadata only (lengths, booleans, IDs, timestamps) — no secrets, no full tokens, no speech content. Caller's responsibility to uphold; not enforced by this type.</summary>
    public string? Metadata { get; init; }

    public DateTimeOffset OccurredAt { get; init; }
}
