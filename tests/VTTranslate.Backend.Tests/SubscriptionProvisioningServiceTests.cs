using VTTranslate.Backend.Application.Billing;
using VTTranslate.Backend.Application.Subscriptions;
using VTTranslate.Backend.Domain.Entities;
using VTTranslate.Backend.Domain.Enums;
using VTTranslate.Backend.Infrastructure.Persistence;

namespace VTTranslate.Backend.Tests;

/// <summary>
/// Phase 25 — unit tests for the production Trial-provisioning boundary
/// (<see cref="SubscriptionProvisioningService"/>). Entitlement only: no payment is involved or claimed.
/// </summary>
public sealed class SubscriptionProvisioningServiceTests
{
    private sealed class Fixture
    {
        public readonly InMemoryAccountRepository Accounts = new();
        public readonly InMemorySubscriptionRepository Subscriptions = new();
        public readonly InMemoryPlanRepository Plans = new();
        public readonly InMemoryAuditEventRepository Audit = new();
        public readonly FakeClock Clock = new();
        public readonly Guid TrialPlanId = Guid.NewGuid();

        public SubscriptionProvisioningService Service => new(
            Accounts, Subscriptions, Plans, Audit, new SubscriptionLifecycleService(Subscriptions, Plans, Audit, Clock), new InMemoryUnitOfWork(), Clock, TrialPlanId);

        public void SeedTrialPlan(int durationDays = 14, string usageSeconds = "36000", string maxDevices = "1")
        {
            Plans.Seed(new Plan { Id = TrialPlanId, Name = "Trial", IsPubliclyPurchasable = false },
            [
                new Entitlement { Id = Guid.NewGuid(), PlanId = TrialPlanId, Key = EntitlementKeys.TrialDurationDays, Value = durationDays.ToString() },
                new Entitlement { Id = Guid.NewGuid(), PlanId = TrialPlanId, Key = EntitlementKeys.TrialUsageLimitSeconds, Value = usageSeconds },
                new Entitlement { Id = Guid.NewGuid(), PlanId = TrialPlanId, Key = EntitlementKeys.MaxActiveDevices, Value = maxDevices },
            ]);
        }

        public async Task<Account> AddAccountAsync(AccountStatus status = AccountStatus.Active)
        {
            var account = new Account
            {
                Id = Guid.NewGuid(),
                Email = $"{Guid.NewGuid():N}@example.com",
                EmailVerified = true,
                ExternalIdentityProvider = "EntraExternalId",
                ExternalSubjectId = Guid.NewGuid().ToString("N"),
                Role = Role.Customer,
                Status = status,
                CreatedAt = Clock.UtcNow,
            };
            await Accounts.SaveAsync(account, CancellationToken.None);
            return account;
        }
    }

    [Fact]
    public async Task ValidProvisioning_CreatesBoundedTrialOnConfiguredPlan_AndAudits()
    {
        var f = new Fixture();
        f.SeedTrialPlan(durationDays: 14);
        var account = await f.AddAccountAsync();

        var result = await f.Service.StartTrialAsync(account.Id, CancellationToken.None);

        Assert.Equal(TrialProvisioningOutcome.Started, result.Outcome);
        var sub = result.Subscription!;
        Assert.Equal(account.Id, sub.AccountId);
        Assert.Equal(f.TrialPlanId, sub.PlanId);
        Assert.Equal(SubscriptionStatus.Trial, sub.Status);
        Assert.Equal(f.Clock.UtcNow, sub.CurrentPeriodStart);
        Assert.Equal(f.Clock.UtcNow.AddDays(14), sub.CurrentPeriodEnd);
        Assert.Null(sub.BillingProviderSubscriptionId); // no payment involved or claimed

        var stored = await f.Subscriptions.FindByAccountAsync(account.Id, CancellationToken.None);
        Assert.Equal(sub.Id, stored!.Id);
        Assert.Contains(f.Audit.Events, e => e.EventType == "SubscriptionTrialStarted" && e.AccountId == account.Id);
    }

    [Fact]
    public async Task DuplicateProvisioning_IsIdempotent_ReturnsSameSubscription_NoSecondRow()
    {
        var f = new Fixture();
        f.SeedTrialPlan();
        var account = await f.AddAccountAsync();

        var first = await f.Service.StartTrialAsync(account.Id, CancellationToken.None);
        f.Clock.UtcNow = f.Clock.UtcNow.AddDays(1);
        var second = await f.Service.StartTrialAsync(account.Id, CancellationToken.None);

        Assert.Equal(TrialProvisioningOutcome.Started, first.Outcome);
        Assert.Equal(TrialProvisioningOutcome.AlreadySubscribed, second.Outcome);
        Assert.Equal(first.Subscription!.Id, second.Subscription!.Id);
        Assert.Equal(first.Subscription.CurrentPeriodEnd, second.Subscription.CurrentPeriodEnd); // period never extended by a repeat call
        Assert.Single(await f.Subscriptions.FindHistoryByAccountAsync(account.Id, CancellationToken.None));
    }

    // NOTE: there is deliberately no in-memory "concurrent provisioning yields exactly one subscription" test here.
    // Concurrency/transaction serialization is an integration property of the PostgreSQL repository (the real
    // account-row lock plus the partial unique index) and is verified by the Testcontainers tests in
    // TrialProvisioningPostgresTests. InMemoryAccountRepository.LockAccountForUsageAccountingAsync is a documented
    // no-op — the in-memory repository intentionally does not emulate database row locks — so asserting "exactly one
    // survivor" against real parallel Task.Run callers here would be testing a guarantee this double cannot provide,
    // not a property of SubscriptionProvisioningService itself.

    [Fact]
    public async Task Provisioning_OnlyAffectsTheNamedAccount_NeverAnother()
    {
        var f = new Fixture();
        f.SeedTrialPlan();
        var accountA = await f.AddAccountAsync();
        var accountB = await f.AddAccountAsync();

        await f.Service.StartTrialAsync(accountA.Id, CancellationToken.None);

        Assert.NotNull(await f.Subscriptions.FindByAccountAsync(accountA.Id, CancellationToken.None));
        Assert.Null(await f.Subscriptions.FindByAccountAsync(accountB.Id, CancellationToken.None));
    }

    [Fact]
    public async Task NonexistentPlan_FailsClosed_GrantsNothing()
    {
        var f = new Fixture(); // trial plan intentionally NOT seeded
        var account = await f.AddAccountAsync();

        var result = await f.Service.StartTrialAsync(account.Id, CancellationToken.None);

        Assert.Equal(TrialProvisioningOutcome.TrialUnavailable, result.Outcome);
        Assert.Null(result.Subscription);
        Assert.Empty(await f.Subscriptions.FindHistoryByAccountAsync(account.Id, CancellationToken.None));
    }

    [Theory]
    [InlineData("0", "3600", "2")]      // zero duration
    [InlineData("-3", "3600", "2")]     // negative duration
    [InlineData("14", "0", "2")]        // zero usage limit (would be "no limit" downstream)
    [InlineData("14", "abc", "2")]      // unparseable usage limit
    [InlineData("14", "3600", "0")]     // no device allowance
    public async Task IncompleteOrUnboundedPlan_FailsClosed_NeverGrantsUnboundedTrial(string days, string usage, string devices)
    {
        var f = new Fixture();
        f.Plans.Seed(new Plan { Id = f.TrialPlanId, Name = "Bad", IsPubliclyPurchasable = false },
        [
            new Entitlement { Id = Guid.NewGuid(), PlanId = f.TrialPlanId, Key = EntitlementKeys.TrialDurationDays, Value = days },
            new Entitlement { Id = Guid.NewGuid(), PlanId = f.TrialPlanId, Key = EntitlementKeys.TrialUsageLimitSeconds, Value = usage },
            new Entitlement { Id = Guid.NewGuid(), PlanId = f.TrialPlanId, Key = EntitlementKeys.MaxActiveDevices, Value = devices },
        ]);
        var account = await f.AddAccountAsync();

        var result = await f.Service.StartTrialAsync(account.Id, CancellationToken.None);

        Assert.Equal(TrialProvisioningOutcome.TrialUnavailable, result.Outcome);
        Assert.Empty(await f.Subscriptions.FindHistoryByAccountAsync(account.Id, CancellationToken.None));
    }

    [Fact]
    public async Task PlanMissingAnEntitlementKey_FailsClosed()
    {
        var f = new Fixture();
        f.Plans.Seed(new Plan { Id = f.TrialPlanId, Name = "NoLimits", IsPubliclyPurchasable = false },
            [new Entitlement { Id = Guid.NewGuid(), PlanId = f.TrialPlanId, Key = EntitlementKeys.TrialDurationDays, Value = "14" }]);
        var account = await f.AddAccountAsync();

        var result = await f.Service.StartTrialAsync(account.Id, CancellationToken.None);

        Assert.Equal(TrialProvisioningOutcome.TrialUnavailable, result.Outcome);
    }

    [Fact]
    public async Task PreviousTerminalSubscription_BlocksASecondFreeTrial()
    {
        var f = new Fixture();
        f.SeedTrialPlan();
        var account = await f.AddAccountAsync();
        await f.Subscriptions.SaveAsync(new Subscription
        {
            Id = Guid.NewGuid(), AccountId = account.Id, PlanId = f.TrialPlanId, Status = SubscriptionStatus.Expired,
            CurrentPeriodStart = f.Clock.UtcNow.AddDays(-30), CurrentPeriodEnd = f.Clock.UtcNow.AddDays(-16),
            CreatedAt = f.Clock.UtcNow.AddDays(-30), UpdatedAt = f.Clock.UtcNow.AddDays(-16),
        }, CancellationToken.None);

        var result = await f.Service.StartTrialAsync(account.Id, CancellationToken.None);

        Assert.Equal(TrialProvisioningOutcome.TrialAlreadyUsed, result.Outcome);
        Assert.Null(await f.Subscriptions.FindByAccountAsync(account.Id, CancellationToken.None));
    }

    [Theory]
    [InlineData(AccountStatus.Suspended)]
    [InlineData(AccountStatus.Pending)]
    public async Task NonActiveAccount_IsNotEligible(AccountStatus status)
    {
        var f = new Fixture();
        f.SeedTrialPlan();
        var account = await f.AddAccountAsync(status);

        var result = await f.Service.StartTrialAsync(account.Id, CancellationToken.None);

        Assert.Equal(TrialProvisioningOutcome.AccountNotEligible, result.Outcome);
        Assert.Empty(await f.Subscriptions.FindHistoryByAccountAsync(account.Id, CancellationToken.None));
    }

    [Fact]
    public async Task UnknownAccount_IsNotEligible()
    {
        var f = new Fixture();
        f.SeedTrialPlan();

        var result = await f.Service.StartTrialAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(TrialProvisioningOutcome.AccountNotEligible, result.Outcome);
    }
}
