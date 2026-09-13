using VTTranslate.Backend.Application.Devices;
using VTTranslate.Backend.Domain;
using VTTranslate.Backend.Domain.Entities;
using VTTranslate.Backend.Domain.Enums;
using VTTranslate.Backend.Infrastructure.Persistence;

namespace VTTranslate.Backend.Tests;

public class DeviceRegistrationServiceTests
{
    private readonly InMemoryDeviceRepository _devices = new();
    private readonly InMemorySubscriptionRepository _subscriptions = new();
    private readonly InMemoryPlanRepository _plans = new();
    private readonly FakeClock _clock = new();
    private readonly DeviceRegistrationService _service;

    public DeviceRegistrationServiceTests()
    {
        _service = new DeviceRegistrationService(_devices, _subscriptions, _plans, _clock);
    }

    private async Task<Guid> SeedAccountWithPlanAsync(int maxActiveDevices)
    {
        var accountId = Guid.NewGuid();
        var planId = Guid.NewGuid();
        _plans.Seed(
            new Plan { Id = planId, Name = "Test", IsPubliclyPurchasable = false },
            [new Entitlement { Id = Guid.NewGuid(), PlanId = planId, Key = EntitlementKeys.MaxActiveDevices, Value = maxActiveDevices.ToString() }]);
        await _subscriptions.SaveAsync(new Subscription
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            PlanId = planId,
            Status = SubscriptionStatus.Active,
            CurrentPeriodStart = _clock.UtcNow,
            CurrentPeriodEnd = _clock.UtcNow.AddDays(30),
        }, CancellationToken.None);
        return accountId;
    }

    [Fact]
    public async Task RegisterDevice_UnderLimit_Succeeds_AndIsAuthorizedImmediately()
    {
        var accountId = await SeedAccountWithPlanAsync(maxActiveDevices: 2);

        var device = await _service.RegisterDeviceAsync(accountId, DevicePlatform.Windows, "My PC", CancellationToken.None);

        Assert.Equal(DeviceStatus.Authorized, device.Status);
        Assert.Equal(accountId, device.AccountId);
        Assert.True(await _service.IsDeviceAuthorizedAsync(accountId, device.Id, CancellationToken.None));
    }

    [Fact]
    public async Task RegisterDevice_AtLimit_ThrowsDeviceLimitExceeded()
    {
        var accountId = await SeedAccountWithPlanAsync(maxActiveDevices: 1);
        await _service.RegisterDeviceAsync(accountId, DevicePlatform.Windows, "First", CancellationToken.None);

        var ex = await Assert.ThrowsAsync<DeviceLimitExceededException>(() =>
            _service.RegisterDeviceAsync(accountId, DevicePlatform.Windows, "Second", CancellationToken.None));
        Assert.Equal(1, ex.Limit);
    }

    [Fact]
    public async Task RevokedDevice_DoesNotCountTowardTheLimit()
    {
        var accountId = await SeedAccountWithPlanAsync(maxActiveDevices: 1);
        var first = await _service.RegisterDeviceAsync(accountId, DevicePlatform.Windows, "First", CancellationToken.None);
        await _service.RevokeDeviceAsync(accountId, first.Id, CancellationToken.None);

        // Registering a second device now succeeds, since the revoked one no longer counts.
        var second = await _service.RegisterDeviceAsync(accountId, DevicePlatform.Windows, "Second", CancellationToken.None);
        Assert.Equal(DeviceStatus.Authorized, second.Status);
    }

    [Fact]
    public async Task RevokeDevice_MakesItUnauthorized()
    {
        var accountId = await SeedAccountWithPlanAsync(maxActiveDevices: 2);
        var device = await _service.RegisterDeviceAsync(accountId, DevicePlatform.Windows, "PC", CancellationToken.None);

        await _service.RevokeDeviceAsync(accountId, device.Id, CancellationToken.None);

        Assert.False(await _service.IsDeviceAuthorizedAsync(accountId, device.Id, CancellationToken.None));
    }

    [Fact]
    public async Task RevokeDevice_IsIdempotent()
    {
        var accountId = await SeedAccountWithPlanAsync(maxActiveDevices: 2);
        var device = await _service.RegisterDeviceAsync(accountId, DevicePlatform.Windows, "PC", CancellationToken.None);

        await _service.RevokeDeviceAsync(accountId, device.Id, CancellationToken.None);
        var exception = await Record.ExceptionAsync(() => _service.RevokeDeviceAsync(accountId, device.Id, CancellationToken.None));

        Assert.Null(exception);
    }

    [Fact]
    public async Task RevokeDevice_BelongingToAnotherAccount_ThrowsDeviceNotOwned()
    {
        var accountA = await SeedAccountWithPlanAsync(maxActiveDevices: 2);
        var accountB = await SeedAccountWithPlanAsync(maxActiveDevices: 2);
        var device = await _service.RegisterDeviceAsync(accountA, DevicePlatform.Windows, "PC", CancellationToken.None);

        await Assert.ThrowsAsync<DeviceNotOwnedException>(() =>
            _service.RevokeDeviceAsync(accountB, device.Id, CancellationToken.None));
    }

    [Fact]
    public async Task IsDeviceAuthorized_UnknownDevice_ReturnsFalse_NeverThrows()
    {
        var accountId = await SeedAccountWithPlanAsync(maxActiveDevices: 2);
        var result = await _service.IsDeviceAuthorizedAsync(accountId, Guid.NewGuid(), CancellationToken.None);
        Assert.False(result);
    }

    [Fact]
    public async Task RegisterDevice_NoSubscriptionRow_FailsClosedToSingleDeviceDefault()
    {
        // No SeedAccountWithPlanAsync call — this account has no subscription at all.
        var accountId = Guid.NewGuid();

        var first = await _service.RegisterDeviceAsync(accountId, DevicePlatform.Windows, "First", CancellationToken.None);
        Assert.Equal(DeviceStatus.Authorized, first.Status);

        // The conservative single-device default must still be enforced, not treated as unlimited.
        await Assert.ThrowsAsync<DeviceLimitExceededException>(() =>
            _service.RegisterDeviceAsync(accountId, DevicePlatform.Windows, "Second", CancellationToken.None));
    }
}
