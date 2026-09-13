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
    private readonly InMemoryAccountRepository _accounts = new();
    private readonly InMemoryUnitOfWork _unitOfWork = new();
    private readonly InMemoryAuditEventRepository _audit = new();
    private readonly FakeClock _clock = new();
    private readonly DeviceRegistrationService _deviceService;
    private readonly UsageService _usageService;
    private readonly EntitlementService _service;

    public EntitlementServiceTests()
    {
        _deviceService = new DeviceRegistrationService(_devices, _subscriptions, _plans, _accounts, _unitOfWork, _audit, _clock);
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
        // Phase 6.6: ISubscriptionRepository.FindByAccountAsync now returns only the
        // account's CURRENTLY-EFFECTIVE (live) subscription — Expired is a terminal,
        // historical status, so a subscription in this state is correctly no longer
        // returned as "the account's subscription" at all. The deny outcome (the actual
        // security property this test protects) is unchanged; only the specific denial
        // reason changes, from a status-specific message to "no subscription found".
        var (accountId, deviceId, _) = await SeedAsync(SubscriptionStatus.Expired, _clock.UtcNow.AddDays(30), []);

        var decision = await _service.CanStartTranslationSessionAsync(accountId, deviceId, CancellationToken.None);

        Assert.False(decision.Allowed);
        Assert.Contains("no subscription found", decision.Reason);
    }

    [Fact]
    public async Task CancelledStatus_AlwaysDenies()
    {
        // See ExpiredStatus_AlwaysDenies_EvenIfPeriodEndIsInTheFuture's comment — Phase
        // 6.6's live-subscription-only lookup means a Cancelled row is likewise no
        // longer surfaced as "the account's subscription"; the deny outcome is unchanged.
        var (accountId, deviceId, _) = await SeedAsync(SubscriptionStatus.Cancelled, _clock.UtcNow.AddDays(30), []);

        var decision = await _service.CanStartTranslationSessionAsync(accountId, deviceId, CancellationToken.None);

        Assert.False(decision.Allowed);
        Assert.Contains("no subscription found", decision.Reason);
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

    // ---- Phase 6.6: PastDue (approved product decision: Option A, bounded access) ----

    [Fact]
    public async Task PastDue_WithinBound_Allows()
    {
        var (accountId, deviceId, _) = await SeedAsync(
            SubscriptionStatus.PastDue, _clock.UtcNow.AddDays(-1),
            [new Entitlement { Id = Guid.NewGuid(), PlanId = Guid.Empty, Key = EntitlementKeys.PastDueGraceDays, Value = "3" }]);

        var decision = await _service.CanStartTranslationSessionAsync(accountId, deviceId, CancellationToken.None);

        Assert.True(decision.Allowed);
    }

    [Fact]
    public async Task PastDue_PastBound_Denies_NeverBecomesUnlimitedAccess()
    {
        var (accountId, deviceId, _) = await SeedAsync(
            SubscriptionStatus.PastDue, _clock.UtcNow.AddDays(-10),
            [new Entitlement { Id = Guid.NewGuid(), PlanId = Guid.Empty, Key = EntitlementKeys.PastDueGraceDays, Value = "3" }]);

        var decision = await _service.CanStartTranslationSessionAsync(accountId, deviceId, CancellationToken.None);

        Assert.False(decision.Allowed);
        Assert.Contains("past-due", decision.Reason);
    }

    [Fact]
    public async Task PastDue_MissingEntitlement_FailsClosed_NotOpen()
    {
        var (accountId, deviceId, _) = await SeedAsync(SubscriptionStatus.PastDue, _clock.UtcNow.AddDays(-1), []);

        var decision = await _service.CanStartTranslationSessionAsync(accountId, deviceId, CancellationToken.None);

        Assert.False(decision.Allowed);
    }

    [Fact]
    public async Task Refunded_AlwaysDenies()
    {
        // Refunded is terminal — like Cancelled/Expired, it is no longer surfaced as
        // "the account's subscription" by FindByAccountAsync (Phase 6.6 cardinality
        // change), so the deny happens via "no subscription found", not a status-specific
        // switch branch — the same, already-established pattern as the two tests above.
        var (accountId, deviceId, _) = await SeedAsync(SubscriptionStatus.Refunded, _clock.UtcNow.AddDays(30), []);

        var decision = await _service.CanStartTranslationSessionAsync(accountId, deviceId, CancellationToken.None);

        Assert.False(decision.Allowed);
    }

    [Fact]
    public async Task UnrecognizedStatus_FailsClosed_NeverImplicitlyAllows()
    {
        // The unconditional correctness rule (Phase 6.6): an unhandled/unrecognized
        // SubscriptionStatus value must deny, never silently fall through to Allow. Since
        // the enum is closed and every real member is handled, this is proven by
        // constructing a Subscription with an out-of-range numeric status value (a
        // malformed/corrupted-data scenario the switch's `default:` arm exists for).
        var accountId = Guid.NewGuid();
        var planId = Guid.NewGuid();
        _plans.Seed(new Plan { Id = planId, Name = "Test", IsPubliclyPurchasable = false }, []);
        await _subscriptions.SaveAsync(new Subscription
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            PlanId = planId,
            Status = (SubscriptionStatus)9999,
            CurrentPeriodStart = _clock.UtcNow.AddDays(-1),
            CurrentPeriodEnd = _clock.UtcNow.AddDays(30),
        }, CancellationToken.None);
        var device = await _deviceService.RegisterDeviceAsync(accountId, DevicePlatform.Windows, "PC", CancellationToken.None);

        var decision = await _service.CanStartTranslationSessionAsync(accountId, device.Id, CancellationToken.None);

        Assert.False(decision.Allowed);
        Assert.Contains("unrecognized", decision.Reason);
    }
}
