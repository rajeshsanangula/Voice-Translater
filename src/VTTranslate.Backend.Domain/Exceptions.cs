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

/// <summary>Thrown when an operation is attempted against an account that is not <see cref="Entities.Account.IsUsable"/> (suspended or soft-deleted) — Phase 6.4's fail-closed account-status enforcement.</summary>
public sealed class AccountNotUsableException(Guid accountId, Enums.AccountStatus status)
    : Exception($"Account '{accountId}' is not usable (status: {status}).")
{
    public Guid AccountId { get; } = accountId;
    public Enums.AccountStatus Status { get; } = status;
}
