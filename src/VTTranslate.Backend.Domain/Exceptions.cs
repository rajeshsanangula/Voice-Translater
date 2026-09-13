namespace VTTranslate.Backend.Domain;

/// <summary>Thrown when a device registration would exceed the account's plan-entitled <see cref="Entities.EntitlementKeys.MaxActiveDevices"/> limit.</summary>
public sealed class DeviceLimitExceededException(int limit) : Exception($"Device limit of {limit} reached for this account's plan.")
{
    public int Limit { get; } = limit;
}

/// <summary>Thrown when an operation targets a device that does not belong to the acting account (and the caller is not an Admin/SuperAdmin — see AuthorizationService).</summary>
public sealed class DeviceNotOwnedException() : Exception("This device does not belong to the specified account.");

/// <summary>Thrown by AuthorizationService when a principal's role does not meet the minimum required for an operation. The backend is authoritative — this is never bypassed by client-supplied state (Phase 6.2B §8).</summary>
public sealed class InsufficientRoleException(Enums.Role required, Enums.Role actual)
    : Exception($"Operation requires at least role '{required}'; principal has '{actual}'.")
{
    public Enums.Role Required { get; } = required;
    public Enums.Role Actual { get; } = actual;
}

/// <summary>Phase 6.6: thrown by a repository's SaveAsync when a concurrent write conflict is detected (e.g. EF Core's DbUpdateConcurrencyException, translated at the Infrastructure boundary so Application never references an EF Core type directly). ReconcileTimeBasedTransitionsAsync catches this specifically to re-read and re-evaluate rather than blindly retrying a write.</summary>
public sealed class ConcurrentUpdateException(Guid entityId) : Exception($"Entity '{entityId}' was concurrently modified by another operation.")
{
    public Guid EntityId { get; } = entityId;
}

/// <summary>Phase 6.6: thrown by IBillingEventRepository.SaveAsync when a (Provider, ProviderEventId) row already exists — the entire idempotency mechanism for redelivered webhooks. Translated from the database's own unique-constraint violation at the Infrastructure boundary, exactly like <see cref="ConcurrentUpdateException"/>.</summary>
public sealed class DuplicateBillingEventException(string provider, string providerEventId) : Exception($"BillingEvent ({provider}, {providerEventId}) already exists.")
{
    public string Provider { get; } = provider;
    public string ProviderEventId { get; } = providerEventId;
}

/// <summary>Thrown when an operation is attempted against an account that is not <see cref="Entities.Account.IsUsable"/> (suspended or soft-deleted) — Phase 6.4's fail-closed account-status enforcement.</summary>
public sealed class AccountNotUsableException(Guid accountId, Enums.AccountStatus status)
    : Exception($"Account '{accountId}' is not usable (status: {status}).")
{
    public Guid AccountId { get; } = accountId;
    public Enums.AccountStatus Status { get; } = status;
}
