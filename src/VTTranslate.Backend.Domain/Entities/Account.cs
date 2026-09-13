namespace VTTranslate.Backend.Domain.Entities;

/// <summary>
/// The AUTRAXIS account — deliberately independent of any identity provider's own
/// subject identifier (see docs/phase-6.2b-resolved-architecture-decisions.md §3).
/// <see cref="ExternalIdentityProvider"/>/<see cref="ExternalSubjectId"/> record the
/// mapping to whichever <see cref="Abstractions.IIdentityProvider"/> implementation
/// authenticated this account, but every OTHER entity in this domain references
/// <see cref="Id"/> only, so a future identity-provider change or an additional
/// federated provider never requires renumbering Subscription/Device/UsageRecord rows.
/// </summary>
public sealed class Account
{
    public required Guid Id { get; init; }
    public required string Email { get; set; }
    public bool EmailVerified { get; set; }

    /// <summary>Which <see cref="Abstractions.IIdentityProvider"/> implementation this account authenticates through (e.g. "EntraExternalId"). A string, not an enum, because Phase 6.2B's identity abstraction is explicitly designed to admit additional providers later without a domain change.</summary>
    public required string ExternalIdentityProvider { get; init; }

    /// <summary>The identity provider's own subject identifier for this account. Never exposed outside the identity boundary/account-linking logic — every other component uses <see cref="Id"/>.</summary>
    public required string ExternalSubjectId { get; init; }

    public required Enums.Role Role { get; set; } = Enums.Role.Customer;

    /// <summary>
    /// Backend-authoritative usability state (Phase 6.4) — see <see cref="Enums.AccountStatus"/>.
    /// Defaults to <see cref="Enums.AccountStatus.Active"/>; only ever changed by trusted
    /// backend/admin logic, never inferred from an Entra claim or a client request.
    /// </summary>
    public Enums.AccountStatus Status { get; set; } = Enums.AccountStatus.Active;

    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? DeletionRequestedAt { get; set; }

    /// <summary>True once deletion has been requested and not reversed. Soft-delete only — see Phase 6.1 §3; hard purge is a separate, not-yet-designed process. Distinct from <see cref="Status"/>: a soft-deleted account and a suspended account are different administrative states with different causes.</summary>
    public bool IsSoftDeleted => DeletionRequestedAt.HasValue;

    /// <summary>True only when the account may access protected AUTRAXIS functionality — Active AND not soft-deleted. This is the single fail-closed gate the application-layer account-resolution service checks before allowing any protected operation to proceed.</summary>
    public bool IsUsable => Status == Enums.AccountStatus.Active && !IsSoftDeleted;
}
