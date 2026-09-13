using VTTranslate.Backend.Domain;
using VTTranslate.Backend.Domain.Abstractions;
using VTTranslate.Backend.Domain.Entities;
using VTTranslate.Backend.Domain.Enums;

namespace VTTranslate.Backend.Application.Devices;

public sealed class DeviceRegistrationService(
    IDeviceRepository devices,
    ISubscriptionRepository subscriptions,
    IPlanRepository plans,
    IClock clock) : IDeviceRegistrationService
{
    public async Task<Device> RegisterDeviceAsync(Guid accountId, DevicePlatform platform, string? displayName, CancellationToken ct)
    {
        var existing = await devices.ListByAccountAsync(accountId, ct);
        var activeCount = existing.Count(d => d.Status != DeviceStatus.Revoked);

        var maxDevices = await GetMaxActiveDevicesAsync(accountId, ct);
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

        await devices.SaveAsync(device, ct);
        return device;
    }

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

        return max;
    }
}
