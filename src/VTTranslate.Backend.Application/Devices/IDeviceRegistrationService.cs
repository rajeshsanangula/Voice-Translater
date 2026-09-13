using VTTranslate.Backend.Domain.Entities;
using VTTranslate.Backend.Domain.Enums;

namespace VTTranslate.Backend.Application.Devices;

/// <summary>
/// Application boundary for device licensing (Phase 6.2B §6). Device identity is an
/// application-generated <see cref="Guid"/> assigned at registration — never derived
/// from hardware characteristics. No platform-specific (Windows/Android/iOS) code lives
/// here; this operates purely on the <see cref="Device"/> domain entity.
/// </summary>
public interface IDeviceRegistrationService
{
    /// <summary>
    /// Registers a new device for an account. Enforces the account's plan
    /// <see cref="EntitlementKeys.MaxActiveDevices"/> limit — throws
    /// <see cref="Domain.DeviceLimitExceededException"/> if already at the limit. The
    /// new device is created directly in <see cref="DeviceStatus.Authorized"/> once
    /// under the limit; <see cref="DeviceStatus.Pending"/> is reserved for a possible
    /// future manual-approval flow that is NOT YET DECIDED (see
    /// docs/phase-6.3-backend-foundation.md).
    /// </summary>
    Task<Device> RegisterDeviceAsync(Guid accountId, DevicePlatform platform, string? displayName, CancellationToken ct);

    Task<IReadOnlyList<Device>> ListDevicesAsync(Guid accountId, CancellationToken ct);

    /// <summary>Transitions a Pending device to Authorized. Reserved for the not-yet-designed manual-approval flow — see class doc comment. Throws <see cref="Domain.DeviceNotOwnedException"/> if the device does not belong to <paramref name="accountId"/>.</summary>
    Task AuthorizeDeviceAsync(Guid accountId, Guid deviceId, CancellationToken ct);

    /// <summary>Revokes a device. Idempotent — revoking an already-revoked device does not throw. Throws <see cref="Domain.DeviceNotOwnedException"/> if the device does not belong to <paramref name="accountId"/>.</summary>
    Task RevokeDeviceAsync(Guid accountId, Guid deviceId, CancellationToken ct);

    /// <summary>The single question the real-time translation entitlement gate ultimately depends on for the device side of its check (Phase 6.1 §5) — true iff the device exists, belongs to the account, and is currently Authorized.</summary>
    Task<bool> IsDeviceAuthorizedAsync(Guid accountId, Guid deviceId, CancellationToken ct);
}
