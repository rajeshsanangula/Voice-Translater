using VTTranslate.Backend.Domain;
using VTTranslate.Backend.Domain.Abstractions;
using VTTranslate.Backend.Domain.Entities;
using VTTranslate.Backend.Domain.Enums;

namespace VTTranslate.Backend.Application.Devices;

public sealed class DeviceRegistrationService(
    IDeviceRepository devices,
    ISubscriptionRepository subscriptions,
    IPlanRepository plans,
    IAccountRepository accounts,
    IUnitOfWork unitOfWork,
    IAuditEventRepository audit,
    IClock clock) : IDeviceRegistrationService
{
    public Task<Device> RegisterDeviceAsync(Guid accountId, DevicePlatform platform, string? displayName, CancellationToken ct) =>
        // Phase 6.7: the count-then-insert sequence below is a check-then-act race under
        // concurrent registration attempts for the same account (see
        // docs/phase-6.7-device-licensing-policy.md §6) — closed by acquiring an
        // exclusive row lock on the Account inside a real transaction BEFORE reading the
        // current device count, so two concurrent callers for the same account are
        // serialized, never both observing "under the limit" simultaneously.
        unitOfWork.ExecuteInTransactionAsync(async innerCt =>
        {
            await accounts.LockAccountForDeviceRegistrationAsync(accountId, innerCt);

            var existing = await devices.ListByAccountAsync(accountId, innerCt);
            var activeCount = existing.Count(d => d.Status != DeviceStatus.Revoked);

            var maxDevices = await GetMaxActiveDevicesAsync(accountId, innerCt);
            if (activeCount >= maxDevices)
                throw new DeviceLimitExceededException(maxDevices);

            var device = new Device
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                Platform = platform,
                DisplayName = displayName,
                Status = DeviceStatus.Authorized,
                RegisteredAt = clock.UtcNow,
                LastSeenAt = clock.UtcNow,
            };

            await devices.SaveAsync(device, innerCt);
            await AuditAsync(accountId, device.Id, "DeviceRegistered", $"platform={platform}", innerCt);
            return device;
        }, ct);

    public Task<Device> ReplaceDeviceAsync(Guid accountId, DevicePlatform platform, string? displayName, CancellationToken ct) =>
        // Phase 25E: same account-row-lock + transaction shape as RegisterDeviceAsync above (see its own comment) —
        // no window in which two devices are simultaneously live, and no window in which zero are (a failed
        // transaction rolls back both the revokes and the new registration together).
        unitOfWork.ExecuteInTransactionAsync(async innerCt =>
        {
            await accounts.LockAccountForDeviceRegistrationAsync(accountId, innerCt);

            var existing = await devices.ListByAccountAsync(accountId, innerCt);
            var revokedCount = 0;
            foreach (var old in existing.Where(d => d.Status != DeviceStatus.Revoked))
            {
                old.Status = DeviceStatus.Revoked;
                old.RevokedAt = clock.UtcNow;
                await devices.SaveAsync(old, innerCt);
                await AuditAsync(accountId, old.Id, "DeviceRevoked", "reason=replaced", innerCt);
                revokedCount++;
            }

            // Re-check the limit AFTER revoking (not assumed to be 0): a plan explicitly configured with
            // MaxActiveDevices=0 (Phase 6.7 §3.3, a deliberate "no devices permitted" value) must still refuse the
            // new device even though every prior device was just revoked — replacement is never a way around the limit.
            var maxDevices = await GetMaxActiveDevicesAsync(accountId, innerCt);
            if (0 >= maxDevices)
                throw new DeviceLimitExceededException(maxDevices);

            var device = new Device
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                Platform = platform,
                DisplayName = displayName,
                Status = DeviceStatus.Authorized,
                RegisteredAt = clock.UtcNow,
                LastSeenAt = clock.UtcNow,
            };

            await devices.SaveAsync(device, innerCt);
            await AuditAsync(accountId, device.Id, "DeviceRegistered", $"platform={platform};replacedCount={revokedCount}", innerCt);
            return device;
        }, ct);

    public Task<IReadOnlyList<Device>> ListDevicesAsync(Guid accountId, CancellationToken ct) =>
        devices.ListByAccountAsync(accountId, ct);

    public async Task AuthorizeDeviceAsync(Guid accountId, Guid deviceId, CancellationToken ct)
    {
        var device = await GetOwnedDeviceAsync(accountId, deviceId, ct);
        device.Status = DeviceStatus.Authorized;
        await devices.SaveAsync(device, ct);
    }

    public async Task RevokeDeviceAsync(Guid accountId, Guid deviceId, CancellationToken ct)
    {
        var device = await GetOwnedDeviceAsync(accountId, deviceId, ct);
        if (device.Status == DeviceStatus.Revoked) return; // idempotent

        device.Status = DeviceStatus.Revoked;
        device.RevokedAt = clock.UtcNow;
        await devices.SaveAsync(device, ct);
        await AuditAsync(accountId, device.Id, "DeviceRevoked", null, ct);
    }

    public async Task<bool> IsDeviceAuthorizedAsync(Guid accountId, Guid deviceId, CancellationToken ct)
    {
        var device = await devices.FindByIdAsync(deviceId, ct);
        return device is not null && device.AccountId == accountId && device.Status == DeviceStatus.Authorized;
    }

    private async Task<Device> GetOwnedDeviceAsync(Guid accountId, Guid deviceId, CancellationToken ct)
    {
        var device = await devices.FindByIdAsync(deviceId, ct);
        if (device is null || device.AccountId != accountId)
            throw new DeviceNotOwnedException();
        return device;
    }

    private async Task<int> GetMaxActiveDevicesAsync(Guid accountId, CancellationToken ct)
    {
        var subscription = await subscriptions.FindByAccountAsync(accountId, ct);
        if (subscription is null) return 1; // no subscription row yet — conservative single-device default, never unlimited

        var entitlements = await plans.GetEntitlementsAsync(subscription.PlanId, ct);
        var entry = entitlements.FirstOrDefault(e => e.Key == EntitlementKeys.MaxActiveDevices);
        if (entry is null || !int.TryParse(entry.Value, out var max))
            return 1; // missing/malformed entitlement — fail conservatively closed, never open

        // Phase 6.7 (docs/phase-6.7-device-licensing-policy.md §3.3): 0 is a deliberate,
        // valid configuration meaning "no devices permitted" — returned as-is, never
        // conflated with "missing"/"malformed". A negative value has no legitimate
        // meaning and is NOT trusted as-is (previously it parsed successfully and was
        // used directly, which happened to deny everything only by comparison
        // arithmetic coincidence, not by design) — it is explicitly clamped to the same
        // conservative single-device default used for missing/malformed values.
        if (max < 0) return 1;

        return max;
    }

    private async Task AuditAsync(Guid accountId, Guid deviceId, string eventType, string? metadata, CancellationToken ct) =>
        await audit.AddAsync(new AuditEvent
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            EventType = eventType,
            Metadata = metadata is null ? $"deviceId={deviceId}" : $"deviceId={deviceId};{metadata}",
            OccurredAt = clock.UtcNow,
        }, ct);
}
