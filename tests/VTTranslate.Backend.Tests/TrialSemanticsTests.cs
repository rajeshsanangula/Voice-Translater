using VTTranslate.Backend.Application.Billing;
using VTTranslate.Backend.Application.Devices;
using VTTranslate.Backend.Application.Entitlements;
using VTTranslate.Backend.Application.Subscriptions;
using VTTranslate.Backend.Application.Usage;
using VTTranslate.Backend.Domain;
using VTTranslate.Backend.Domain.Entities;
using VTTranslate.Backend.Domain.Enums;
using VTTranslate.Backend.Infrastructure.Persistence;

namespace VTTranslate.Backend.Tests;

/// <summary>
/// Phase 25B — the approved trial semantics against the real services (in-memory repositories):
/// 14-day period, ONE 36,000-second allowance for the whole trial (never reset by a calendar month), one device,
/// expiry, no second trial, and unchanged monthly semantics for Active subscriptions. Entitlement only — no payment.
/// </summary>
public sealed class TrialSemanticsTests
{
    private sealed class Env
    {
        public readonly InMemoryAccountRepository Accounts = new();
        public readonly InMemorySubscriptionRepository Subscriptions = new();
        public readonly InMemoryPlanRepository Plans = new();
        public readonly InMemoryUsageRecordRepository UsageRecords = new();
        public readonly InMemoryDeviceRepository Devices = new();
        public readonly InMemoryAuditEventRepository Audit = new();
        public readonly FakeClock Clock = new() { UtcNow = new DateTimeOffset(2026, 1, 25, 9, 0, 0, TimeSpan.Zero) };
        public readonly Guid TrialPlanId = Guid.NewGuid();

        public readonly UsageService Usage;
        public readonly DeviceRegistrationService DeviceService;
        public readonly EntitlementService Entitlements;
        public readonly SubscriptionLifecycleService Lifecycle;
        public readonly SubscriptionProvisioningService Provisioning;
        public readonly TrialUsageReporter Reporter;

        public Env(string trialLimit = "36000", string maxDevices = "1")
        {
            Plans.Seed(new Plan { Id = TrialPlanId, Name = "Trial", IsPubliclyPurchasable = false },
            [
                new Entitlement { Id = Guid.NewGuid(), PlanId = TrialPlanId, Key = EntitlementKeys.TrialDurationDays, Value = "14" },
                new Entitlement { Id = Guid.NewGuid(), PlanId = TrialPlanId, Key = EntitlementKeys.TrialUsageLimitSeconds, Value = trialLimit },
                new Entitlement { Id = Guid.NewGuid(), PlanId = TrialPlanId, Key = EntitlementKeys.MaxActiveDevices, Value = maxDevices },
            ]);
            var unitOfWork = new InMemoryUnitOfWork();
            Usage = new UsageService(UsageRecords, Clock);
            DeviceService = new DeviceRegistrationService(Devices, Subscriptions, Plans, Accounts, unitOfWork, Audit, Clock);
            Entitlements = new EntitlementService(Subscriptions, Plans, DeviceService, Usage, Clock);
            Lifecycle = new SubscriptionLifecycleService(Subscriptions, Plans, Audit, Clock);
            Provisioning = new SubscriptionProvisioningService(Accounts, Subscriptions, Plans, Audit, Lifecycle, unitOfWork, Clock, TrialPlanId);
            Reporter = new TrialUsageReporter(Subscriptions, Plans, Usage, Clock);
        }

        public async Task<(Account Account, Guid DeviceId)> StartTrialCustomerAsync()
        {
            var account = TrialTestSupport.NewAccount(Guid.NewGuid().ToString("N"));
            await Accounts.SaveAsync(account, CancellationToken.None);
            var device = await DeviceService.RegisterDeviceAsync(account.Id, DevicePlatform.Windows, "PC", CancellationToken.None);
            var started = await Provisioning.StartTrialAsync(account.Id, CancellationToken.None);
            Assert.Equal(TrialProvisioningOutcome.Started, started.Outcome);
            return (account, device.Id);
        }

        public Task UseAsync(Guid accountId, Guid deviceId, double seconds) =>
            Usage.RecordServerDerivedUsageAsync(accountId, deviceId, "en-US:de-DE", seconds, null, CancellationToken.None);

        public Task<EntitlementDecision> CanStartAsync(Guid accountId, Guid deviceId) =>
            Entitlements.CanStartTranslationSessionAsync(accountId, deviceId, CancellationToken.None);
    }

    // ---- 1–3. approved values ----

    [Fact]
    public async Task ApprovedTrial_Is14DayPeriod_With36000SecondAllowance_AndOneDevice()
    {
        var env = new Env();
        var (account, deviceId) = await env.StartTrialCustomerAsync();

        var sub = (await env.Subscriptions.FindByAccountAsync(account.Id, CancellationToken.None))!;
        Assert.Equal(SubscriptionStatus.Trial, sub.Status);
        Assert.Equal(env.Clock.UtcNow, sub.CurrentPeriodStart);                 // persisted start = UTC now
        Assert.Equal(env.Clock.UtcNow.AddDays(14), sub.CurrentPeriodEnd);       // start + 14 days

        var report = (await env.Reporter.GetAsync(account.Id, CancellationToken.None))!;
        Assert.Equal(36000, report.LimitSeconds);                               // 10 hours
        Assert.Equal(36000, report.RemainingSeconds);
        Assert.True((await env.CanStartAsync(account.Id, deviceId)).Allowed);

        // One device: the account already holds one; a second is refused by the existing device-limit enforcement.
        await Assert.ThrowsAsync<DeviceLimitExceededException>(() =>
            env.DeviceService.RegisterDeviceAsync(account.Id, DevicePlatform.Windows, "Second PC", CancellationToken.None));
    }

    // ---- 9. exhaustion ----

    [Fact]
    public async Task Allowance_IsAtLeast36000Seconds_SessionDeniedAtBoundary_WithMachineReadableCode()
    {
        var env = new Env();
        var (account, deviceId) = await env.StartTrialCustomerAsync();

        await env.UseAsync(account.Id, deviceId, 35999);
        Assert.True((await env.CanStartAsync(account.Id, deviceId)).Allowed);   // 35,999 s used: one second left

        await env.UseAsync(account.Id, deviceId, 1);                            // 36,000 s used
        var denied = await env.CanStartAsync(account.Id, deviceId);
        Assert.False(denied.Allowed);
        Assert.Equal("trial_usage_exhausted", denied.Code);
        Assert.Contains("usage limit", denied.Reason, StringComparison.OrdinalIgnoreCase); // keeps the session service's UsageDenied mapping
    }

    [Fact]
    public async Task ConcurrentSessionStartsAtAndBelowTheBoundary_AreDeterministic_AndRunningSessionsAreNeverCut()
    {
        var env = new Env();
        var (account, deviceId) = await env.StartTrialCustomerAsync();

        // Exactly at the allowance: every concurrent start is denied.
        await env.UseAsync(account.Id, deviceId, 36000);
        var atLimit = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => env.CanStartAsync(account.Id, deviceId))));
        Assert.All(atLimit, d => { Assert.False(d.Allowed); Assert.Equal("trial_usage_exhausted", d.Code); });

        // One second below: concurrent starts all pass the check (usage is recorded when a session ENDS, so the check cannot
        // serialize them) — this is the documented overshoot: a session that starts under the limit may run past it.
        var env2 = new Env();
        var (account2, device2) = await env2.StartTrialCustomerAsync();
        await env2.UseAsync(account2.Id, device2, 35999);
        var below = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => env2.CanStartAsync(account2.Id, device2))));
        Assert.All(below, d => Assert.True(d.Allowed));
    }

    // ---- 10/11. lifetime allowance across month buckets ----

    [Fact]
    public async Task TrialUsage_DoesNotResetAtCalendarMonthBoundary()
    {
        var env = new Env();                                                   // trial starts Jan 25
        var (account, deviceId) = await env.StartTrialCustomerAsync();

        env.Clock.UtcNow = new DateTimeOffset(2026, 1, 30, 12, 0, 0, TimeSpan.Zero);
        await env.UseAsync(account.Id, deviceId, 36000);                       // whole allowance used in January

        env.Clock.UtcNow = new DateTimeOffset(2026, 2, 2, 8, 0, 0, TimeSpan.Zero); // February, still inside the 14-day trial
        var decision = await env.CanStartAsync(account.Id, deviceId);

        Assert.False(decision.Allowed);                                        // a per-month bucket would have allowed this
        Assert.Equal("trial_usage_exhausted", decision.Code);
        Assert.Equal(0, await env.Usage.GetAuthoritativeUsageSecondsAsync(account.Id, "2026-02", CancellationToken.None)); // February bucket alone is empty
    }

    [Fact]
    public async Task TrialUsage_IsSummedAcrossMonthBuckets_IgnoringEarlierRecordsAndClientHints()
    {
        var env = new Env();
        // Usage recorded BEFORE the trial period (earlier the same month) and client-reported hints must not count.
        env.Clock.UtcNow = new DateTimeOffset(2026, 1, 25, 8, 0, 0, TimeSpan.Zero);
        var account = TrialTestSupport.NewAccount("sum-buckets");
        await env.Accounts.SaveAsync(account, CancellationToken.None);
        var device = await env.DeviceService.RegisterDeviceAsync(account.Id, DevicePlatform.Windows, "PC", CancellationToken.None);
        await env.UseAsync(account.Id, device.Id, 5000);                       // 08:00 — before the trial starts
        env.Clock.UtcNow = new DateTimeOffset(2026, 1, 25, 9, 0, 0, TimeSpan.Zero);
        Assert.Equal(TrialProvisioningOutcome.Started, (await env.Provisioning.StartTrialAsync(account.Id, CancellationToken.None)).Outcome);

        await env.UseAsync(account.Id, device.Id, 20000);                      // January bucket, during the trial
        await env.Usage.RecordClientReportedHintAsync(account.Id, device.Id, "en-US:de-DE", 99999, CancellationToken.None);
        env.Clock.UtcNow = new DateTimeOffset(2026, 2, 3, 9, 0, 0, TimeSpan.Zero);
        await env.UseAsync(account.Id, device.Id, 15999);                      // February bucket, during the trial

        var report = (await env.Reporter.GetAsync(account.Id, CancellationToken.None))!;
        Assert.Equal(35999, report.UsedSeconds);                               // 20,000 + 15,999 only
        Assert.False(report.Exhausted);
        Assert.True((await env.CanStartAsync(account.Id, device.Id)).Allowed);

        await env.UseAsync(account.Id, device.Id, 1);
        Assert.False((await env.CanStartAsync(account.Id, device.Id)).Allowed);
    }

    // ---- 7/8. expiry ----

    [Fact]
    public async Task ExpiredTrial_IsDenied_BeforeAndAfterTheExpiryTransitionIsPersisted()
    {
        var env = new Env();
        var (account, deviceId) = await env.StartTrialCustomerAsync();

        env.Clock.UtcNow = env.Clock.UtcNow.AddDays(14).AddSeconds(1);         // trial period has passed
        var before = await env.CanStartAsync(account.Id, deviceId);            // not yet reconciled/persisted
        Assert.False(before.Allowed);
        Assert.Equal("trial_expired", before.Code);

        var sub = (await env.Subscriptions.FindByAccountAsync(account.Id, CancellationToken.None))!;
        var reconciled = await env.Lifecycle.ReconcileTimeBasedTransitionsAsync(sub, env.Clock.UtcNow, CancellationToken.None);
        Assert.Equal(SubscriptionStatus.Expired, reconciled.Status);           // deterministic persisted transition, no background job
        Assert.Contains(env.Audit.Events, e => e.EventType == "TrialExpiredByTimeReconciliation");
        Assert.Null(await env.Subscriptions.FindByAccountAsync(account.Id, CancellationToken.None)); // no longer live

        var after = await env.CanStartAsync(account.Id, deviceId);
        Assert.False(after.Allowed);
        Assert.Equal("trial_expired", after.Code);                             // still the upgrade reason once it is history
    }

    [Fact]
    public async Task ExpiredTrial_CannotBeRestarted_NorAnotherTrialCreated()
    {
        var env = new Env();
        var (account, _) = await env.StartTrialCustomerAsync();
        env.Clock.UtcNow = env.Clock.UtcNow.AddDays(30);

        var first = await env.Provisioning.StartTrialAsync(account.Id, CancellationToken.None); // live-but-ended: reconciled to Expired
        var second = await env.Provisioning.StartTrialAsync(account.Id, CancellationToken.None); // now history

        Assert.Equal(TrialProvisioningOutcome.TrialAlreadyUsed, first.Outcome);
        Assert.Equal(TrialProvisioningOutcome.TrialAlreadyUsed, second.Outcome);
        var history = await env.Subscriptions.FindHistoryByAccountAsync(account.Id, CancellationToken.None);
        Assert.Single(history);
        Assert.Equal(SubscriptionStatus.Expired, history[0].Status);
    }

    [Fact]
    public async Task ExpiredTrial_MyAccountReportsEnded_AndUsageIsNotReset()
    {
        var env = new Env();
        var (account, deviceId) = await env.StartTrialCustomerAsync();
        await env.UseAsync(account.Id, deviceId, 1200);
        env.Clock.UtcNow = env.Clock.UtcNow.AddDays(20);
        var sub = (await env.Subscriptions.FindByAccountAsync(account.Id, CancellationToken.None))!;
        await env.Lifecycle.ReconcileTimeBasedTransitionsAsync(sub, env.Clock.UtcNow, CancellationToken.None);

        var report = (await env.Reporter.GetAsync(account.Id, CancellationToken.None))!;
        Assert.True(report.Ended);
        Assert.Equal("Expired", report.Status);
        Assert.Equal(1200, report.UsedSeconds);
    }

    // ---- fail-closed / reporter ----

    [Fact]
    public async Task TrialPlanWithoutAPositiveUsageLimit_IsDenied_NeverUnlimited()
    {
        var env = new Env();
        var (account, deviceId) = await env.StartTrialCustomerAsync();
        env.Plans.Seed(new Plan { Id = env.TrialPlanId, Name = "Trial", IsPubliclyPurchasable = false },
        [
            new Entitlement { Id = Guid.NewGuid(), PlanId = env.TrialPlanId, Key = EntitlementKeys.TrialDurationDays, Value = "14" },
            new Entitlement { Id = Guid.NewGuid(), PlanId = env.TrialPlanId, Key = EntitlementKeys.MaxActiveDevices, Value = "1" },
        ]); // limit removed after the trial was created

        var decision = await env.CanStartAsync(account.Id, deviceId);

        Assert.False(decision.Allowed);
        Assert.Equal("trial_not_configured", decision.Code);
    }

    [Fact]
    public async Task Reporter_ReportsUsedRemainingAndExhausted_AndNothingForNonTrialAccounts()
    {
        var env = new Env();
        var (account, deviceId) = await env.StartTrialCustomerAsync();
        await env.UseAsync(account.Id, deviceId, 9000);

        var report = (await env.Reporter.GetAsync(account.Id, CancellationToken.None))!;
        Assert.Equal("Trial", report.Status);
        Assert.Equal(9000, report.UsedSeconds);
        Assert.Equal(36000, report.LimitSeconds);
        Assert.Equal(27000, report.RemainingSeconds);
        Assert.False(report.Ended);
        Assert.False(report.Exhausted);
        Assert.Equal(14, Math.Round((report.PeriodEnd - report.PeriodStart).TotalDays));

        await env.UseAsync(account.Id, deviceId, 27000);
        Assert.True((await env.Reporter.GetAsync(account.Id, CancellationToken.None))!.Exhausted);

        Assert.Null(await env.Reporter.GetAsync(Guid.NewGuid(), CancellationToken.None)); // no subscription
    }

    // ---- 12. Active subscriptions unchanged ----

    [Fact]
    public async Task ActiveSubscription_KeepsMonthlyBucketSemantics_AndIgnoresTheTrialLimit()
    {
        var env = new Env();
        var planId = Guid.NewGuid();
        env.Plans.Seed(new Plan { Id = planId, Name = "Paid (test)", IsPubliclyPurchasable = false },
        [
            new Entitlement { Id = Guid.NewGuid(), PlanId = planId, Key = EntitlementKeys.MaxActiveDevices, Value = "5" },
            new Entitlement { Id = Guid.NewGuid(), PlanId = planId, Key = EntitlementKeys.UsageLimitSecondsPerPeriod, Value = "100" },
            new Entitlement { Id = Guid.NewGuid(), PlanId = planId, Key = EntitlementKeys.TrialUsageLimitSeconds, Value = "5" }, // must be ignored for Active
        ]);
        var account = TrialTestSupport.NewAccount("active-monthly");
        await env.Accounts.SaveAsync(account, CancellationToken.None);
        await env.Subscriptions.SaveAsync(new Subscription
        {
            Id = Guid.NewGuid(), AccountId = account.Id, PlanId = planId, Status = SubscriptionStatus.Active,
            CurrentPeriodStart = env.Clock.UtcNow.AddDays(-1), CurrentPeriodEnd = env.Clock.UtcNow.AddDays(90),
            CreatedAt = env.Clock.UtcNow, UpdatedAt = env.Clock.UtcNow,
        }, CancellationToken.None);
        var device = await env.DeviceService.RegisterDeviceAsync(account.Id, DevicePlatform.Windows, "PC", CancellationToken.None);

        env.Clock.UtcNow = new DateTimeOffset(2026, 1, 30, 9, 0, 0, TimeSpan.Zero);
        await env.UseAsync(account.Id, device.Id, 50);
        Assert.True((await env.CanStartAsync(account.Id, device.Id)).Allowed);   // 50 ≥ the trial key (5) but Active ignores it
        await env.UseAsync(account.Id, device.Id, 50);
        var januaryDenied = await env.CanStartAsync(account.Id, device.Id);
        Assert.False(januaryDenied.Allowed);                                    // 100 used this month
        Assert.Null(januaryDenied.Code);                                        // Active denial unchanged: no trial code

        env.Clock.UtcNow = new DateTimeOffset(2026, 2, 1, 0, 0, 1, TimeSpan.Zero);
        Assert.True((await env.CanStartAsync(account.Id, device.Id)).Allowed);   // the calendar month DID reset for Active
    }

    [Fact]
    public async Task UsageSince_OnlyCountsServerDerived_AtOrAfterTheStart()
    {
        var env = new Env();
        var accountId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();
        env.Clock.UtcNow = new DateTimeOffset(2026, 3, 31, 23, 0, 0, TimeSpan.Zero);
        await env.UseAsync(accountId, deviceId, 100);                            // before `since`
        var since = new DateTimeOffset(2026, 3, 31, 23, 30, 0, TimeSpan.Zero);
        env.Clock.UtcNow = new DateTimeOffset(2026, 3, 31, 23, 45, 0, TimeSpan.Zero);
        await env.UseAsync(accountId, deviceId, 200);                            // March, after since
        env.Clock.UtcNow = new DateTimeOffset(2026, 4, 1, 0, 15, 0, TimeSpan.Zero);
        await env.UseAsync(accountId, deviceId, 300);                            // April
        await env.Usage.RecordClientReportedHintAsync(accountId, deviceId, "x", 5000, CancellationToken.None);

        Assert.Equal(500, await env.Usage.GetAuthoritativeUsageSecondsSinceAsync(accountId, since, CancellationToken.None));
    }
}
