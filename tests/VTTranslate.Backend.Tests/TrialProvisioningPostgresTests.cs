using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using VTTranslate.Backend.Application.Billing;
using VTTranslate.Backend.Application.Subscriptions;
using VTTranslate.Backend.Domain.Entities;
using VTTranslate.Backend.Domain.Enums;
using VTTranslate.Backend.Infrastructure.Persistence.EfCore;
using VTTranslate.Backend.Infrastructure.Time;

namespace VTTranslate.Backend.Tests;

/// <summary>
/// Phase 25 — real-PostgreSQL tests for Trial provisioning (persistence across DbContext / host lifetimes,
/// real row-lock concurrency, and the Production-environment HTTP surface). Docker-gated exactly like
/// <see cref="EfPostgresPersistenceTests"/>: they self-skip (are NOT counted as passed) without a Docker daemon.
/// </summary>
public sealed class TrialProvisioningPostgresTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static async Task<(Account account, Guid planId)> SeedAsync(AutraxisDbContext db, string subject)
    {
        var account = TrialTestSupport.NewAccount(subject);
        await new EfAccountRepository(db).SaveAsync(account, CancellationToken.None);

        var planId = Guid.NewGuid();
        db.Plans.Add(new Plan { Id = planId, Name = $"Trial {subject}", IsPubliclyPurchasable = false });
        db.Entitlements.AddRange(
            new Entitlement { Id = Guid.NewGuid(), PlanId = planId, Key = EntitlementKeys.TrialDurationDays, Value = "14" },
            new Entitlement { Id = Guid.NewGuid(), PlanId = planId, Key = EntitlementKeys.TrialUsageLimitSeconds, Value = "36000" },
            new Entitlement { Id = Guid.NewGuid(), PlanId = planId, Key = EntitlementKeys.MaxActiveDevices, Value = "1" });
        await db.SaveChangesAsync();
        return (account, planId);
    }

    private static SubscriptionProvisioningService ServiceFor(AutraxisDbContext db, Guid planId) => new(
        new EfAccountRepository(db), new EfSubscriptionRepository(db), new EfPlanRepository(db),
        new EfAuditEventRepository(db),
        new SubscriptionLifecycleService(new EfSubscriptionRepository(db), new EfPlanRepository(db), new EfAuditEventRepository(db), new SystemClock()),
        new EfUnitOfWork(db), new SystemClock(), planId);

    [SkipIfNoDockerFact]
    public async Task Provisioning_Persists_AcrossDbContextLifetimes_AndIsIdempotent()
    {
        Account account; Guid planId; Guid subscriptionId;
        await using (var db = fixture.CreateContext())
        {
            (account, planId) = await SeedAsync(db, $"pg-trial-{Guid.NewGuid():N}");
            var first = await ServiceFor(db, planId).StartTrialAsync(account.Id, CancellationToken.None);
            Assert.Equal(TrialProvisioningOutcome.Started, first.Outcome);
            subscriptionId = first.Subscription!.Id;
        }

        await using (var db2 = fixture.CreateContext()) // "restart": a brand-new context/connection
        {
            var stored = await new EfSubscriptionRepository(db2).FindByAccountAsync(account.Id, CancellationToken.None);
            Assert.Equal(subscriptionId, stored!.Id);
            Assert.Equal(SubscriptionStatus.Trial, stored.Status);
            Assert.Equal(planId, stored.PlanId);

            var again = await ServiceFor(db2, planId).StartTrialAsync(account.Id, CancellationToken.None);
            Assert.Equal(TrialProvisioningOutcome.AlreadySubscribed, again.Outcome);
            Assert.Equal(subscriptionId, again.Subscription!.Id);
            Assert.Single(await new EfSubscriptionRepository(db2).FindHistoryByAccountAsync(account.Id, CancellationToken.None));
        }
    }

    [SkipIfNoDockerFact]
    public async Task ConcurrentProvisioning_OnRealPostgres_YieldsExactlyOneLiveSubscription()
    {
        Account account; Guid planId;
        await using (var seedDb = fixture.CreateContext())
            (account, planId) = await SeedAsync(seedDb, $"pg-trial-race-{Guid.NewGuid():N}");

        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(async _ =>
        {
            await using var db = fixture.CreateContext(); // own connection per caller
            return await ServiceFor(db, planId).StartTrialAsync(account.Id, CancellationToken.None);
        }));

        Assert.Equal(1, results.Count(r => r.Outcome == TrialProvisioningOutcome.Started));
        Assert.All(results.Where(r => r.Outcome != TrialProvisioningOutcome.Started),
            r => Assert.Equal(TrialProvisioningOutcome.AlreadySubscribed, r.Outcome));

        await using var checkDb = fixture.CreateContext();
        Assert.Single(await new EfSubscriptionRepository(checkDb).FindHistoryByAccountAsync(account.Id, CancellationToken.None));
    }

    [SkipIfNoDockerFact]
    public async Task TrialUsage_IsSummedAcrossMonthBuckets_OnRealPostgres_AndSurvivesNewContexts()
    {
        // usage_records.AccountId/DeviceId are real foreign keys (FK_usage_records_accounts_AccountId,
        // FK_usage_records_devices_DeviceId) — an arbitrary Guid.NewGuid() with no corresponding row is rejected by
        // PostgreSQL (23503), unlike the in-memory double this test has no equivalent of. Seed a real Account (via
        // the same TrialTestSupport/EfAccountRepository pattern the other tests in this class use) and a real
        // Device row before writing any UsageRecord against them.
        Guid accountId, deviceId;
        var since = new DateTimeOffset(2031, 1, 25, 9, 0, 0, TimeSpan.Zero);

        await using (var seedDb = fixture.CreateContext())
        {
            var account = TrialTestSupport.NewAccount($"pg-trial-usage-{Guid.NewGuid():N}");
            await new EfAccountRepository(seedDb).SaveAsync(account, CancellationToken.None);
            accountId = account.Id;

            var device = new Device
            {
                Id = Guid.NewGuid(), AccountId = accountId, Platform = DevicePlatform.Windows, DisplayName = "Test",
                Status = DeviceStatus.Authorized, RegisteredAt = DateTimeOffset.UtcNow, LastSeenAt = DateTimeOffset.UtcNow,
            };
            await new EfDeviceRepository(seedDb).SaveAsync(device, CancellationToken.None);
            deviceId = device.Id;
        }

        await using (var db = fixture.CreateContext())
        {
            var repo = new EfUsageRecordRepository(db);
            async Task Add(DateTimeOffset at, double seconds, UsageRecordSource source) =>
                await repo.AddAsync(new UsageRecord
                {
                    Id = Guid.NewGuid(), AccountId = accountId, DeviceId = deviceId, Direction = "en-US:de-DE", SecondsUsed = seconds,
                    Source = source, PeriodBucket = at.ToString("yyyy-MM"), RecordedAt = at,
                }, CancellationToken.None);

            await Add(since.AddHours(-1), 700, UsageRecordSource.ServerDerived);                               // before the trial
            await Add(since.AddDays(2), 20000, UsageRecordSource.ServerDerived);                              // January bucket
            await Add(since.AddDays(9), 15999, UsageRecordSource.ServerDerived);                              // February bucket
            await Add(since.AddDays(9), 88888, UsageRecordSource.ClientReportedHint);                          // never counted
        }

        await using var db2 = fixture.CreateContext(); // new context = new connection
        var clock = new FakeClock { UtcNow = since.AddDays(10) };
        var total = await new Application.Usage.UsageService(new EfUsageRecordRepository(db2), clock)
            .GetAuthoritativeUsageSecondsSinceAsync(accountId, since, CancellationToken.None);

        Assert.Equal(35999, total);
    }

    [SkipIfNoDockerFact]
    public async Task ExpiredTrial_IsPersisted_AndCannotBeRestarted_AcrossContexts()
    {
        Account account; Guid planId;
        await using (var db = fixture.CreateContext())
        {
            (account, planId) = await SeedAsync(db, $"pg-expired-{Guid.NewGuid():N}");
            Assert.Equal(TrialProvisioningOutcome.Started, (await ServiceFor(db, planId).StartTrialAsync(account.Id, CancellationToken.None)).Outcome);

            var subscriptions = new EfSubscriptionRepository(db);
            var sub = (await subscriptions.FindByAccountAsync(account.Id, CancellationToken.None))!;
            sub.CurrentPeriodEnd = DateTimeOffset.UtcNow.AddMinutes(-5);                  // the trial period has passed
            await subscriptions.SaveAsync(sub, CancellationToken.None);
        }

        await using (var db2 = fixture.CreateContext())
        {
            var restart = await ServiceFor(db2, planId).StartTrialAsync(account.Id, CancellationToken.None);
            Assert.Equal(TrialProvisioningOutcome.TrialAlreadyUsed, restart.Outcome);     // reconciled to Expired, never restarted
        }

        await using (var db3 = fixture.CreateContext())
        {
            var subscriptions = new EfSubscriptionRepository(db3);
            Assert.Null(await subscriptions.FindByAccountAsync(account.Id, CancellationToken.None)); // no longer live
            var history = await subscriptions.FindHistoryByAccountAsync(account.Id, CancellationToken.None);
            Assert.Single(history);
            Assert.Equal(SubscriptionStatus.Expired, history[0].Status);

            var again = await ServiceFor(db3, planId).StartTrialAsync(account.Id, CancellationToken.None);
            Assert.Equal(TrialProvisioningOutcome.TrialAlreadyUsed, again.Outcome);       // and still cannot, from history
        }
    }

    /// <summary>A real Production-environment host: PostgreSQL-backed, Trial enabled by the operator.</summary>
    private sealed class ProductionFactory(string connectionString, Guid planId) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("Database:ConnectionString", connectionString);
            builder.UseSetting("Subscriptions:Trial:Enabled", "true");
            builder.UseSetting("Subscriptions:Trial:PlanId", planId.ToString());
            builder.ConfigureTestServices(TrialTestSupport.UseOfflineJwt);
        }
    }

    [SkipIfNoDockerFact]
    public async Task Production_ProvisionsTrial_DevSeedEndpointDoesNotExist_AndSurvivesHostRestart()
    {
        var subject = $"pg-prod-{Guid.NewGuid():N}";
        Account account; Guid planId;
        await using (var db = fixture.CreateContext())
            (account, planId) = await SeedAsync(db, subject);

        HttpClient Authed(WebApplicationFactory<Program> f)
        {
            var c = f.CreateClient();
            c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TrialTestSupport.BuildToken(subject));
            return c;
        }

        await using (var host1 = new ProductionFactory(fixture.ConnectionString, planId))
        {
            using var client = Authed(host1);

            // The Development-only seed endpoint must not exist in Production.
            var dev = await client.PostAsync("/dev/seed-subscription", content: null);
            Assert.Equal(HttpStatusCode.NotFound, dev.StatusCode);

            var created = await client.PostAsync("/subscription/trial", content: null);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            Assert.Equal("Trial", (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
        }

        await using (var host2 = new ProductionFactory(fixture.ConnectionString, planId)) // host restart
        {
            using var client = Authed(host2);
            var current = await client.GetAsync("/subscription");
            Assert.Equal(HttpStatusCode.OK, current.StatusCode);
            Assert.Equal("Trial", (await current.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());

            var again = await client.PostAsync("/subscription/trial", content: null);
            Assert.Equal(HttpStatusCode.OK, again.StatusCode); // idempotent across restart
        }
    }
}
