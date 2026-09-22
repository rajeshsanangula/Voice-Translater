using VTTranslate.Backend.Application.Devices;
using VTTranslate.Backend.Domain.Entities;
using VTTranslate.Backend.Domain.Enums;
using VTTranslate.Backend.Infrastructure.Persistence.EfCore;
using VTTranslate.Backend.Infrastructure.Time;

namespace VTTranslate.Backend.Tests;

/// <summary>
/// Phase 25E — real-PostgreSQL concurrency tests for device replacement, exercising the same account row lock
/// (<c>SELECT ... FOR UPDATE</c>) real registration uses. Docker-gated exactly like <see cref="EfPostgresPersistenceTests"/>:
/// self-skip (NOT counted as passed) without a working Docker daemon.
/// </summary>
public sealed class DeviceReplacementPostgresTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static async Task<(Account Account, Guid PlanId)> SeedAsync(AutraxisDbContext db, string subject, int maxActiveDevices)
    {
        var account = DeviceReplacementTestAuth.NewAccount(subject);
        await new EfAccountRepository(db).SaveAsync(account, CancellationToken.None);
        var planId = Guid.NewGuid();
        db.Plans.Add(new Plan { Id = planId, Name = $"Test {subject}", IsPubliclyPurchasable = false });
        db.Entitlements.Add(new Entitlement { Id = Guid.NewGuid(), PlanId = planId, Key = EntitlementKeys.MaxActiveDevices, Value = maxActiveDevices.ToString() });
        db.Subscriptions.Add(new Subscription
        {
            Id = Guid.NewGuid(), AccountId = account.Id, PlanId = planId, Status = SubscriptionStatus.Active,
            CurrentPeriodStart = DateTimeOffset.UtcNow.AddDays(-1), CurrentPeriodEnd = DateTimeOffset.UtcNow.AddDays(30),
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        return (account, planId);
    }

    private static DeviceRegistrationService ServiceFor(AutraxisDbContext db) => new(
        new EfDeviceRepository(db), new EfSubscriptionRepository(db), new EfPlanRepository(db),
        new EfAccountRepository(db), new EfUnitOfWork(db), new EfAuditEventRepository(db), new SystemClock());

    [SkipIfNoDockerFact]
    public async Task A_TwoSimultaneousReplacements_SameAccount_YieldExactlyOneLiveDevice()
    {
        Account account;
        await using (var seedDb = fixture.CreateContext())
            (account, _) = await SeedAsync(seedDb, $"pg-replace-a-{Guid.NewGuid():N}", maxActiveDevices: 1);

        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(async i =>
        {
            await using var db = fixture.CreateContext(); // own connection per caller
            return await ServiceFor(db).ReplaceDeviceAsync(account.Id, DevicePlatform.Windows, $"Replacement {i}", CancellationToken.None);
        }));

        await using var checkDb = fixture.CreateContext();
        var all = await new EfDeviceRepository(checkDb).ListByAccountAsync(account.Id, CancellationToken.None);
        var live = all.Where(d => d.Status != DeviceStatus.Revoked).ToList();
        Assert.Single(live); // exactly one live device — never zero, never two
        Assert.Contains(results, r => r.Id == live[0].Id);
    }

    [SkipIfNoDockerFact]
    public async Task B_ReplacementRacingNormalRegistration_NeverExceedsMaxActiveDevices()
    {
        Account account;
        await using (var seedDb = fixture.CreateContext())
            (account, _) = await SeedAsync(seedDb, $"pg-replace-b-{Guid.NewGuid():N}", maxActiveDevices: 1);
        await using (var db0 = fixture.CreateContext())
            await ServiceFor(db0).RegisterDeviceAsync(account.Id, DevicePlatform.Windows, "Original", CancellationToken.None);

        var tasks = Enumerable.Range(0, 6).Select(async i =>
        {
            await using var db = fixture.CreateContext();
            var svc = ServiceFor(db);
            try
            {
                return i % 2 == 0
                    ? (object)await svc.ReplaceDeviceAsync(account.Id, DevicePlatform.Windows, $"Replace {i}", CancellationToken.None)
                    : (object)await svc.RegisterDeviceAsync(account.Id, DevicePlatform.Windows, $"Register {i}", CancellationToken.None);
            }
            catch (VTTranslate.Backend.Domain.DeviceLimitExceededException)
            {
                return null; // an expected outcome for a plain register call losing the race — never a race defect
            }
        });
        await Task.WhenAll(tasks);

        await using var checkDb = fixture.CreateContext();
        var live = (await new EfDeviceRepository(checkDb).ListByAccountAsync(account.Id, CancellationToken.None))
            .Count(d => d.Status != DeviceStatus.Revoked);
        Assert.True(live <= 1, $"expected at most MaxActiveDevices(1) live device(s), found {live}");
        Assert.True(live >= 1, "a successful replacement/registration must leave at least one live device");
    }

    [SkipIfNoDockerFact]
    public async Task E_OldDeviceEndsRevoked_NewDeviceEndsAuthorized_OnRealPostgres()
    {
        Account account;
        await using (var seedDb = fixture.CreateContext())
            (account, _) = await SeedAsync(seedDb, $"pg-replace-e-{Guid.NewGuid():N}", maxActiveDevices: 1);

        Guid oldId;
        await using (var db1 = fixture.CreateContext())
            oldId = (await ServiceFor(db1).RegisterDeviceAsync(account.Id, DevicePlatform.Windows, "Old", CancellationToken.None)).Id;

        Guid newId;
        await using (var db2 = fixture.CreateContext())
            newId = (await ServiceFor(db2).ReplaceDeviceAsync(account.Id, DevicePlatform.Windows, "New", CancellationToken.None)).Id;

        await using var checkDb = fixture.CreateContext();
        var all = await new EfDeviceRepository(checkDb).ListByAccountAsync(account.Id, CancellationToken.None);
        Assert.Equal(DeviceStatus.Revoked, all.Single(d => d.Id == oldId).Status);
        Assert.NotNull(all.Single(d => d.Id == oldId).RevokedAt);
        Assert.Equal(DeviceStatus.Authorized, all.Single(d => d.Id == newId).Status);
    }
}
