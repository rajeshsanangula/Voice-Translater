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
    private readonly InMemoryAccountRepository _accounts = new();
    private readonly InMemoryUnitOfWork _unitOfWork = new();
    private readonly InMemoryAuditEventRepository _audit = new();
    private readonly FakeClock _clock = new();
    private readonly DeviceRegistrationService _service;

    public DeviceRegistrationServiceTests()
    {
        _service = new DeviceRegistrationService(_devices, _subscriptions, _plans, _accounts, _unitOfWork, _audit, _clock);
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

    // ---- Phase 6.7: 0/negative MaxActiveDevices (docs/phase-6.7-device-licensing-policy.md §3.3) ----

    [Fact]
    public async Task MaxActiveDevices_Zero_DeniesEveryRegistration()
    {
        var accountId = await SeedAccountWithPlanAsync(maxActiveDevices: 0);

        var ex = await Assert.ThrowsAsync<DeviceLimitExceededException>(() =>
            _service.RegisterDeviceAsync(accountId, DevicePlatform.Windows, "First", CancellationToken.None));
        Assert.Equal(0, ex.Limit);
    }

    [Fact]
    public async Task MaxActiveDevices_Negative_ClampsToConservativeSingleDeviceDefault_NotTrustedAsIs()
    {
        var accountId = await SeedAccountWithPlanAsync(maxActiveDevices: -5);

        // Clamped to 1, not -5 — the first registration succeeds (1 allowed), the second is denied.
        var first = await _service.RegisterDeviceAsync(accountId, DevicePlatform.Windows, "First", CancellationToken.None);
        Assert.Equal(DeviceStatus.Authorized, first.Status);

        var ex = await Assert.ThrowsAsync<DeviceLimitExceededException>(() =>
            _service.RegisterDeviceAsync(accountId, DevicePlatform.Windows, "Second", CancellationToken.None));
        Assert.Equal(1, ex.Limit);
    }

    // ---- Phase 6.7: Pending is reserved/unused (docs/phase-6.7-device-licensing-policy.md §2) ----

    [Fact]
    public async Task RegisterDevice_NeverProducesPendingStatus()
    {
        var accountId = await SeedAccountWithPlanAsync(maxActiveDevices: 2);

        var device = await _service.RegisterDeviceAsync(accountId, DevicePlatform.Windows, "PC", CancellationToken.None);

        Assert.NotEqual(DeviceStatus.Pending, device.Status);
        Assert.Equal(DeviceStatus.Authorized, device.Status);
    }

    // ---- Phase 6.7: pooled (Model A) — one limit across all platforms ----

    [Fact]
    public async Task PooledLimit_CountsAllPlatformsTogether_NotPerPlatform()
    {
        var accountId = await SeedAccountWithPlanAsync(maxActiveDevices: 3);

        await _service.RegisterDeviceAsync(accountId, DevicePlatform.Windows, "PC", CancellationToken.None);
        await _service.RegisterDeviceAsync(accountId, DevicePlatform.Android, "Phone", CancellationToken.None);
        await _service.RegisterDeviceAsync(accountId, DevicePlatform.iOS, "Tablet", CancellationToken.None);

        // The pool is now exhausted (3/3) regardless of platform mix — a 4th of ANY platform must be denied.
        await Assert.ThrowsAsync<DeviceLimitExceededException>(() =>
            _service.RegisterDeviceAsync(accountId, DevicePlatform.Windows, "Second PC", CancellationToken.None));
    }

    // ---- Phase 6.7: auditability ----

    [Fact]
    public async Task RegisterAndRevoke_EachProduceTheirOwnAuditEvent()
    {
        var accountId = await SeedAccountWithPlanAsync(maxActiveDevices: 2);

        var device = await _service.RegisterDeviceAsync(accountId, DevicePlatform.Windows, "PC", CancellationToken.None);
        await _service.RevokeDeviceAsync(accountId, device.Id, CancellationToken.None);

        Assert.Contains(_audit.Events, e => e.EventType == "DeviceRegistered" && e.AccountId == accountId);
        Assert.Contains(_audit.Events, e => e.EventType == "DeviceRevoked" && e.AccountId == accountId);
    }
}
