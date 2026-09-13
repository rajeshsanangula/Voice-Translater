using VTTranslate.Backend.Application.Devices;
using VTTranslate.Backend.Application.Entitlements;
using VTTranslate.Backend.Application.Usage;
using VTTranslate.Backend.Domain.Entities;
using VTTranslate.Backend.Domain.Enums;
using VTTranslate.Backend.Infrastructure.Persistence;

namespace VTTranslate.Backend.Tests;

public class EntitlementServiceTests
{
    private readonly InMemoryDeviceRepository _devices = new();
    private readonly InMemorySubscriptionRepository _subscriptions = new();
    private readonly InMemoryPlanRepository _plans = new();
    private readonly InMemoryUsageRecordRepository _usageRecords = new();
    private readonly FakeClock _clock = new();
    private readonly DeviceRegistrationService _deviceService;
    private readonly UsageService _usageService;
    private readonly EntitlementService _service;

    public EntitlementServiceTests()
    {
        _deviceService = new DeviceRegistrationService(_devices, _subscriptions, _plans, _clock);
        _usageService = new UsageService(_usageRecords, _clock);
        _service = new EntitlementService(_subscriptions, _plans, _deviceService, _usageService, _clock);
    }

    private async Task<(Guid AccountId, Guid DeviceId, Guid PlanId)> SeedAsync(
        SubscriptionStatus status, DateTimeOffset periodEnd, IEnumerable<Entitlement> extraEntitlements)
    {
        var accountId = Guid.NewGuid();
        var planId = Guid.NewGuid();
        var entitlements = new List<Entitlement>
        {
            new() { Id = Guid.NewGuid(), PlanId = planId, Key = EntitlementKeys.MaxActiveDevices, Value = "5" },
        };
        entitlements.AddRange(extraEntitlements);
        _plans.Seed(new Plan { Id = planId, Name = "Test", IsPubliclyPurchasable = false }, entitlements);

        await _subscriptions.SaveAsync(new Subscription
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            PlanId = planId,
            Status = status,
            CurrentPeriodStart = _clock.UtcNow.AddDays(-1),
            CurrentPeriodEnd = periodEnd,
        }, CancellationToken.None);

        var device = await _deviceService.RegisterDeviceAsync(accountId, DevicePlatform.Windows, "PC", CancellationToken.None);
        return (accountId, device.Id, planId);
    }

    [Fact]
    public async Task ActiveSubscription_AuthorizedDevice_WithinPeriod_NoUsageLimit_Allows()
    {
        var (accountId, deviceId, _) = await SeedAsync(SubscriptionStatus.Active, _clock.UtcNow.AddDays(10), []);

        var decision = await _service.CanStartTranslationSessionAsync(accountId, deviceId, CancellationToken.None);

        Assert.True(decision.Allowed);
    }

    [Fact]
    public async Task UnauthorizedDevice_Denies()
    {
        var (accountId, _, _) = await SeedAsync(SubscriptionStatus.Active, _clock.UtcNow.AddDays(10), []);
        var unknownDeviceId = Guid.NewGuid();

        var decision = await _service.CanStartTranslationSessionAsync(accountId, unknownDeviceId, CancellationToken.None);

        Assert.False(decision.Allowed);
        Assert.Contains("not authorized", decision.Reason);
    }

    [Fact]
    public async Task RevokedDevice_Denies()
    {
        var (accountId, deviceId, _) = await SeedAsync(SubscriptionStatus.Active, _clock.UtcNow.AddDays(10), []);
        await _deviceService.RevokeDeviceAsync(accountId, deviceId, CancellationToken.None);

        var decision = await _service.CanStartTranslationSessionAsync(accountId, deviceId, CancellationToken.None);

        Assert.False(decision.Allowed);
    }

    [Fact]
    public async Task NoSubscription_Denies()
    {
        var accountId = Guid.NewGuid();
        var device = await _deviceService.RegisterDeviceAsync(accountId, DevicePlatform.Windows, "PC", CancellationToken.None);

        var decision = await _service.CanStartTranslationSessionAsync(accountId, device.Id, CancellationToken.None);

        Assert.False(decision.Allowed);
        Assert.Contains("no subscription", decision.Reason);
    }

    [Fact]
    public async Task ExpiredStatus_AlwaysDenies_EvenIfPeriodEndIsInTheFuture()
    {
        // A subscription explicitly marked Expired must deny regardless of any date field —
        // Status is authoritative, not a derived value from CurrentPeriodEnd alone.
        var (accountId, deviceId, _) = await SeedAsync(SubscriptionStatus.Expired, _clock.UtcNow.AddDays(30), []);

        var decision = await _service.CanStartTranslationSessionAsync(accountId, deviceId, CancellationToken.None);

        Assert.False(decision.Allowed);
        Assert.Contains("expired", decision.Reason);
    }

    [Fact]
    public async Task CancelledStatus_AlwaysDenies()
    {
        var (accountId, deviceId, _) = await SeedAsync(SubscriptionStatus.Cancelled, _clock.UtcNow.AddDays(30), []);

        var decision = await _service.CanStartTranslationSessionAsync(accountId, deviceId, CancellationToken.None);

        Assert.False(decision.Allowed);
        Assert.Contains("cancelled", decision.Reason);
    }

    [Fact]
    public async Task ActiveStatus_PastPeriodEnd_Denies()
    {
        var (accountId, deviceId, _) = await SeedAsync(SubscriptionStatus.Active, _clock.UtcNow.AddDays(-1), []);

        var decision = await _service.CanStartTranslationSessionAsync(accountId, deviceId, CancellationToken.None);

        Assert.False(decision.Allowed);
    }

    [Fact]
    public async Task GracePeriod_WithinBound_Allows()
    {
        var (accountId, deviceId, _) = await SeedAsync(
            SubscriptionStatus.GracePeriod, _clock.UtcNow.AddDays(-1),
            [new Entitlement { Id = Guid.NewGuid(), PlanId = Guid.Empty, Key = EntitlementKeys.GracePeriodDays, Value = "3" }]);

        var decision = await _service.CanStartTranslationSessionAsync(accountId, deviceId, CancellationToken.None);

        Assert.True(decision.Allowed);
    }

    [Fact]
    public async Task GracePeriod_PastBound_Denies_NeverBecomesIndefiniteBypass()
    {
        var (accountId, deviceId, _) = await SeedAsync(
            SubscriptionStatus.GracePeriod, _clock.UtcNow.AddDays(-10),
            [new Entitlement { Id = Guid.NewGuid(), PlanId = Guid.Empty, Key = EntitlementKeys.GracePeriodDays, Value = "3" }]);

        var decision = await _service.CanStartTranslationSessionAsync(accountId, deviceId, CancellationToken.None);

        Assert.False(decision.Allowed);
        Assert.Contains("grace period", decision.Reason);
    }

    [Fact]
    public async Task GracePeriod_MissingGraceEntitlement_FailsClosed_NotOpen()
    {
        // No GracePeriodDays entitlement configured at all — must deny, not silently allow.
        var (accountId, deviceId, _) = await SeedAsync(SubscriptionStatus.GracePeriod, _clock.UtcNow.AddDays(-1), []);

        var decision = await _service.CanStartTranslationSessionAsync(accountId, deviceId, CancellationToken.None);

        Assert.False(decision.Allowed);
    }

    [Fact]
    public async Task UsageAtOrAboveLimit_Denies()
    {
        var (accountId, deviceId, _) = await SeedAsync(
            SubscriptionStatus.Active, _clock.UtcNow.AddDays(10),
            [new Entitlement { Id = Guid.NewGuid(), PlanId = Guid.Empty, Key = EntitlementKeys.UsageLimitSecondsPerPeriod, Value = "100" }]);

        await _usageService.RecordServerDerivedUsageAsync(accountId, deviceId, "en-US:de-DE", 100, "AzureSpeech", CancellationToken.None);

        var decision = await _service.CanStartTranslationSessionAsync(accountId, deviceId, CancellationToken.None);

        Assert.False(decision.Allowed);
        Assert.Contains("usage limit", decision.Reason);
    }

    [Fact]
    public async Task UsageBelowLimit_Allows()
    {
        var (accountId, deviceId, _) = await SeedAsync(
            SubscriptionStatus.Active, _clock.UtcNow.AddDays(10),
            [new Entitlement { Id = Guid.NewGuid(), PlanId = Guid.Empty, Key = EntitlementKeys.UsageLimitSecondsPerPeriod, Value = "100" }]);

        await _usageService.RecordServerDerivedUsageAsync(accountId, deviceId, "en-US:de-DE", 50, "AzureSpeech", CancellationToken.None);

        var decision = await _service.CanStartTranslationSessionAsync(accountId, deviceId, CancellationToken.None);

        Assert.True(decision.Allowed);
    }

    [Fact]
    public async Task ClientReportedUsageHint_NeverCountsTowardTheLimit()
    {
        // This is the concrete regression test for Phase 6.2B §7/§6: client-reported
        // usage must never be treated as billing/enforcement authority.
        var (accountId, deviceId, _) = await SeedAsync(
            SubscriptionStatus.Active, _clock.UtcNow.AddDays(10),
            [new Entitlement { Id = Guid.NewGuid(), PlanId = Guid.Empty, Key = EntitlementKeys.UsageLimitSecondsPerPeriod, Value = "100" }]);

        // A client "hint" reporting usage far beyond the limit must have NO effect on the gate.
        await _usageService.RecordClientReportedHintAsync(accountId, deviceId, "en-US:de-DE", 10_000, CancellationToken.None);

        var decision = await _service.CanStartTranslationSessionAsync(accountId, deviceId, CancellationToken.None);

        Assert.True(decision.Allowed);
    }

    [Fact]
    public async Task TrialStatus_UsesTrialUsageLimitKey_NotThePaidLimitKey()
    {
        var (accountId, deviceId, _) = await SeedAsync(
            SubscriptionStatus.Trial, _clock.UtcNow.AddDays(10),
            [
                new Entitlement { Id = Guid.NewGuid(), PlanId = Guid.Empty, Key = EntitlementKeys.TrialUsageLimitSeconds, Value = "60" },
                new Entitlement { Id = Guid.NewGuid(), PlanId = Guid.Empty, Key = EntitlementKeys.UsageLimitSecondsPerPeriod, Value = "999999" },
            ]);

        await _usageService.RecordServerDerivedUsageAsync(accountId, deviceId, "en-US:de-DE", 60, "AzureSpeech", CancellationToken.None);

        var decision = await _service.CanStartTranslationSessionAsync(accountId, deviceId, CancellationToken.None);

        // Must be denied by the TRIAL limit (60), not allowed by the much larger paid-plan limit.
        Assert.False(decision.Allowed);
    }
}
