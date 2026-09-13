using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using VTTranslate.Backend.Domain;
using VTTranslate.Backend.Domain.Entities;
using VTTranslate.Backend.Domain.Enums;
using VTTranslate.Backend.Infrastructure.Persistence.EfCore;

namespace VTTranslate.Backend.Tests;

/// <summary>
/// Phase 6.5 real-relational-database tests — run against an actual PostgreSQL server
/// (via Testcontainers), never SQLite/EF-InMemory, so real constraint enforcement, real
/// transactions, and real migration SQL are exercised (see
/// docs/phase-6.5-database-decision.md §9/§3 for why). One container is shared across
/// this class's tests (expensive to start; each test uses its own uniquely-scoped rows
/// so tests do not interfere with each other) via <see cref="IClassFixture{TFixture}"/>.
///
/// Every test is <see cref="SkipIfNoDockerFactAttribute"/> — see that type's doc comment.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _container;
    public string ConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        if (!DockerProbe.IsAvailable.Value) return; // nothing to do — tests will all self-skip

        _container = new PostgreSqlBuilder()
            .WithImage("postgres:16-alpine")
            .WithDatabase("autraxis_test")
            .WithUsername("autraxis")
            .WithPassword("autraxis_test_password")
            .Build();
        await _container.StartAsync();
        ConnectionString = _container.GetConnectionString();

        await using var db = CreateContext();
        await db.Database.MigrateAsync(); // real EF Core migration, not EnsureCreated
    }

    public async Task DisposeAsync()
    {
        if (_container is not null) await _container.DisposeAsync();
    }

    /// <summary>A fresh DbContext against the SAME underlying database — used to prove
    /// real persistence (data survives disposing one context and creating another),
    /// which in-memory/ConcurrentDictionary-backed repositories cannot demonstrate.</summary>
    public AutraxisDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AutraxisDbContext>().UseNpgsql(ConnectionString).Options;
        return new AutraxisDbContext(options);
    }
}

public sealed class EfPostgresPersistenceTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static Account NewAccount(string subject, AccountStatus status = AccountStatus.Active, Role role = Role.Customer) => new()
    {
        Id = Guid.NewGuid(),
        Email = $"{subject}@example.com",
        EmailVerified = true,
        ExternalIdentityProvider = "EntraExternalId",
        ExternalSubjectId = subject,
        Role = role,
        Status = status,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    // ---- MIGRATIONS ----

    [SkipIfNoDockerFact]
    public async Task Migration_AppliesSuccessfully_AllExpectedTablesExist()
    {
        await using var db = fixture.CreateContext();
        var pending = await db.Database.GetPendingMigrationsAsync();
        Assert.Empty(pending); // fixture already migrated — nothing left pending

        var tableNames = new[] { "accounts", "profiles", "plans", "entitlements", "subscriptions", "devices", "sessions", "usage_records", "audit_events" };
        foreach (var table in tableNames)
        {
            var exists = await db.Database.SqlQuery<bool>(
                $"SELECT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = {table})").FirstAsync();
            Assert.True(exists, $"expected migrated table '{table}' to exist");
        }
    }

    // ---- ACCOUNT ----

    [SkipIfNoDockerFact]
    public async Task Account_CreateAndRetrieve_RoundTrips()
    {
        var account = NewAccount($"acct-create-{Guid.NewGuid()}");
        await using (var db = fixture.CreateContext())
        {
            db.Accounts.Add(account);
            await db.SaveChangesAsync();
        }

        await using var readDb = fixture.CreateContext();
        var loaded = await readDb.Accounts.FindAsync(account.Id);
        Assert.NotNull(loaded);
        Assert.Equal(account.Email, loaded!.Email);
        Assert.Equal(account.ExternalSubjectId, loaded.ExternalSubjectId);
    }

    [SkipIfNoDockerFact]
    public async Task Account_Update_Persists()
    {
        var account = NewAccount($"acct-update-{Guid.NewGuid()}");
        await using (var db = fixture.CreateContext())
        {
            db.Accounts.Add(account);
            await db.SaveChangesAsync();
        }

        await using (var db = fixture.CreateContext())
        {
            var toUpdate = await db.Accounts.FirstAsync(a => a.Id == account.Id);
            toUpdate.Role = Role.Admin;
            await db.SaveChangesAsync();
        }

        await using var readDb = fixture.CreateContext();
        var reloaded = await readDb.Accounts.FirstAsync(a => a.Id == account.Id);
        Assert.Equal(Role.Admin, reloaded.Role);
    }

    [SkipIfNoDockerFact]
    public async Task Account_Suspend_PersistsAcrossReload()
    {
        var account = NewAccount($"acct-suspend-{Guid.NewGuid()}");
        await using (var db = fixture.CreateContext())
        {
            db.Accounts.Add(account);
            await db.SaveChangesAsync();
        }

        await using (var db = fixture.CreateContext())
        {
            var toSuspend = await db.Accounts.FirstAsync(a => a.Id == account.Id);
            toSuspend.Status = AccountStatus.Suspended;
            await db.SaveChangesAsync();
        }

        // Distinct DbContext/connection — proves this is real durable persistence, not
        // first-level-cache behavior within one context.
        await using var readDb = fixture.CreateContext();
        var reloaded = await readDb.Accounts.FirstAsync(a => a.Id == account.Id);
        Assert.Equal(AccountStatus.Suspended, reloaded.Status);
        Assert.False(reloaded.IsUsable);
    }

    [SkipIfNoDockerFact]
    public async Task Account_DuplicateExternalIdentity_RejectedByUniqueConstraint()
    {
        var subject = $"acct-dup-{Guid.NewGuid()}";
        await using var db = fixture.CreateContext();
        db.Accounts.Add(NewAccount(subject));
        await db.SaveChangesAsync();

        db.Accounts.Add(NewAccount(subject)); // same provider+subject, different Id
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [SkipIfNoDockerFact]
    public async Task Account_Role_PersistsCorrectly_AsBackendAuthoritativeValue()
    {
        var account = NewAccount($"acct-role-{Guid.NewGuid()}", role: Role.SuperAdmin);
        await using (var db = fixture.CreateContext())
        {
            db.Accounts.Add(account);
            await db.SaveChangesAsync();
        }

        await using var readDb = fixture.CreateContext();
        var reloaded = await readDb.Accounts.FirstAsync(a => a.Id == account.Id);
        Assert.Equal(Role.SuperAdmin, reloaded.Role);
    }

    // ---- PROFILE ----

    [SkipIfNoDockerFact]
    public async Task Profile_CreateAndRetrieve_AssociatedWithAccount()
    {
        var account = NewAccount($"acct-profile-{Guid.NewGuid()}");
        var profile = new Profile { AccountId = account.Id, DisplayName = "Test User", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };

        await using (var db = fixture.CreateContext())
        {
            db.Accounts.Add(account);
            db.Profiles.Add(profile);
            await db.SaveChangesAsync();
        }

        await using var readDb = fixture.CreateContext();
        var reloaded = await readDb.Profiles.FirstOrDefaultAsync(p => p.AccountId == account.Id);
        Assert.NotNull(reloaded);
        Assert.Equal("Test User", reloaded!.DisplayName);
    }

    [SkipIfNoDockerFact]
    public async Task Profile_ForeignKeyViolation_RejectedWithoutOwningAccount()
    {
        await using var db = fixture.CreateContext();
        db.Profiles.Add(new Profile { AccountId = Guid.NewGuid(), CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    // ---- DEVICE ----

    [SkipIfNoDockerFact]
    public async Task Device_RegisterAndRetrieve_BelongsToAccount()
    {
        var account = NewAccount($"acct-device-{Guid.NewGuid()}");
        var device = new Device { Id = Guid.NewGuid(), AccountId = account.Id, Platform = DevicePlatform.Windows, Status = DeviceStatus.Authorized, RegisteredAt = DateTimeOffset.UtcNow, LastSeenAt = DateTimeOffset.UtcNow };

        await using (var db = fixture.CreateContext())
        {
            db.Accounts.Add(account);
            db.Devices.Add(device);
            await db.SaveChangesAsync();
        }

        await using var readDb = fixture.CreateContext();
        var reloaded = await readDb.Devices.FindAsync(device.Id);
        Assert.NotNull(reloaded);
        Assert.Equal(account.Id, reloaded!.AccountId);
    }

    [SkipIfNoDockerFact]
    public async Task Device_Revoke_PersistsAcrossReload()
    {
        var account = NewAccount($"acct-device-revoke-{Guid.NewGuid()}");
        var device = new Device { Id = Guid.NewGuid(), AccountId = account.Id, Platform = DevicePlatform.Windows, Status = DeviceStatus.Authorized, RegisteredAt = DateTimeOffset.UtcNow, LastSeenAt = DateTimeOffset.UtcNow };

        await using (var db = fixture.CreateContext())
        {
            db.Accounts.Add(account);
            db.Devices.Add(device);
            await db.SaveChangesAsync();
        }

        await using (var db = fixture.CreateContext())
        {
            var toRevoke = await db.Devices.FirstAsync(d => d.Id == device.Id);
            toRevoke.Status = DeviceStatus.Revoked;
            toRevoke.RevokedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }

        await using var readDb = fixture.CreateContext();
        var reloaded = await readDb.Devices.FirstAsync(d => d.Id == device.Id);
        Assert.Equal(DeviceStatus.Revoked, reloaded.Status);
        Assert.NotNull(reloaded.RevokedAt);
    }

    [SkipIfNoDockerFact]
    public async Task Device_AccountIsolation_ListByAccountNeverReturnsAnotherAccountsDevice()
    {
        var accountA = NewAccount($"acct-iso-a-{Guid.NewGuid()}");
        var accountB = NewAccount($"acct-iso-b-{Guid.NewGuid()}");
        var deviceA = new Device { Id = Guid.NewGuid(), AccountId = accountA.Id, Platform = DevicePlatform.Windows, Status = DeviceStatus.Authorized, RegisteredAt = DateTimeOffset.UtcNow, LastSeenAt = DateTimeOffset.UtcNow };
        var deviceB = new Device { Id = Guid.NewGuid(), AccountId = accountB.Id, Platform = DevicePlatform.Windows, Status = DeviceStatus.Authorized, RegisteredAt = DateTimeOffset.UtcNow, LastSeenAt = DateTimeOffset.UtcNow };

        await using (var db = fixture.CreateContext())
        {
            db.Accounts.AddRange(accountA, accountB);
            db.Devices.AddRange(deviceA, deviceB);
            await db.SaveChangesAsync();
        }

        await using var readDb = fixture.CreateContext();
        var accountADevices = await readDb.Devices.Where(d => d.AccountId == accountA.Id).ToListAsync();
        Assert.Single(accountADevices);
        Assert.Equal(deviceA.Id, accountADevices[0].Id);
        Assert.DoesNotContain(accountADevices, d => d.Id == deviceB.Id);
    }

    [SkipIfNoDockerFact]
    public async Task Device_ForeignKeyViolation_RejectedWithoutOwningAccount()
    {
        await using var db = fixture.CreateContext();
        db.Devices.Add(new Device { Id = Guid.NewGuid(), AccountId = Guid.NewGuid(), Platform = DevicePlatform.Windows, Status = DeviceStatus.Authorized, RegisteredAt = DateTimeOffset.UtcNow, LastSeenAt = DateTimeOffset.UtcNow });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    // ---- PLAN / ENTITLEMENT ----

    [SkipIfNoDockerFact]
    public async Task Plan_PersistAndRetrieve()
    {
        var plan = new Plan { Id = Guid.NewGuid(), Name = "Test Plan", IsPubliclyPurchasable = true };
        await using (var db = fixture.CreateContext())
        {
            db.Plans.Add(plan);
            await db.SaveChangesAsync();
        }

        await using var readDb = fixture.CreateContext();
        var reloaded = await readDb.Plans.FindAsync(plan.Id);
        Assert.NotNull(reloaded);
        Assert.Equal("Test Plan", reloaded!.Name);
    }

    [SkipIfNoDockerFact]
    public async Task Entitlement_DuplicateKeyForSamePlan_RejectedByUniqueConstraint()
    {
        var plan = new Plan { Id = Guid.NewGuid(), Name = "Dup Entitlement Plan", IsPubliclyPurchasable = false };
        await using var db = fixture.CreateContext();
        db.Plans.Add(plan);
        db.Entitlements.Add(new Entitlement { Id = Guid.NewGuid(), PlanId = plan.Id, Key = EntitlementKeys.MaxActiveDevices, Value = "1" });
        await db.SaveChangesAsync();

        db.Entitlements.Add(new Entitlement { Id = Guid.NewGuid(), PlanId = plan.Id, Key = EntitlementKeys.MaxActiveDevices, Value = "2" });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    // ---- SUBSCRIPTION ----

    [SkipIfNoDockerFact]
    public async Task Subscription_PersistAndRetrieve_AccountRelationship()
    {
        var account = NewAccount($"acct-sub-{Guid.NewGuid()}");
        var plan = new Plan { Id = Guid.NewGuid(), Name = "Sub Plan", IsPubliclyPurchasable = true };
        var subscription = new Subscription
        {
            Id = Guid.NewGuid(),
            AccountId = account.Id,
            PlanId = plan.Id,
            Status = SubscriptionStatus.Active,
            CurrentPeriodStart = DateTimeOffset.UtcNow,
            CurrentPeriodEnd = DateTimeOffset.UtcNow.AddDays(30),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        await using (var db = fixture.CreateContext())
        {
            db.Accounts.Add(account);
            db.Plans.Add(plan);
            db.Subscriptions.Add(subscription);
            await db.SaveChangesAsync();
        }

        await using var readDb = fixture.CreateContext();
        var reloaded = await readDb.Subscriptions.FirstAsync(s => s.AccountId == account.Id);
        Assert.Equal(plan.Id, reloaded.PlanId);
        Assert.Equal(SubscriptionStatus.Active, reloaded.Status);
    }

    [SkipIfNoDockerFact]
    public async Task Subscription_DuplicateForSameAccount_RejectedByUniqueConstraint()
    {
        var account = NewAccount($"acct-sub-dup-{Guid.NewGuid()}");
        var plan = new Plan { Id = Guid.NewGuid(), Name = "Sub Dup Plan", IsPubliclyPurchasable = true };
        await using var db = fixture.CreateContext();
        db.Accounts.Add(account);
        db.Plans.Add(plan);
        db.Subscriptions.Add(new Subscription { Id = Guid.NewGuid(), AccountId = account.Id, PlanId = plan.Id, Status = SubscriptionStatus.Trial, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();

        db.Subscriptions.Add(new Subscription { Id = Guid.NewGuid(), AccountId = account.Id, PlanId = plan.Id, Status = SubscriptionStatus.Trial, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    // ---- USAGE ----

    [SkipIfNoDockerFact]
    public async Task UsageRecord_PersistAndQueryByAccountAndPeriod()
    {
        var account = NewAccount($"acct-usage-{Guid.NewGuid()}");
        var device = new Device { Id = Guid.NewGuid(), AccountId = account.Id, Platform = DevicePlatform.Windows, Status = DeviceStatus.Authorized, RegisteredAt = DateTimeOffset.UtcNow, LastSeenAt = DateTimeOffset.UtcNow };
        const string period = "2026-09";

        await using (var db = fixture.CreateContext())
        {
            db.Accounts.Add(account);
            db.Devices.Add(device);
            db.UsageRecords.Add(new UsageRecord { Id = Guid.NewGuid(), AccountId = account.Id, DeviceId = device.Id, Direction = "en-US:de-DE", SecondsUsed = 120, Source = UsageRecordSource.ServerDerived, PeriodBucket = period, RecordedAt = DateTimeOffset.UtcNow });
            db.UsageRecords.Add(new UsageRecord { Id = Guid.NewGuid(), AccountId = account.Id, DeviceId = device.Id, Direction = "en-US:de-DE", SecondsUsed = 999999, Source = UsageRecordSource.ClientReportedHint, PeriodBucket = period, RecordedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        await using var readDb = fixture.CreateContext();
        var records = await readDb.UsageRecords.Where(u => u.AccountId == account.Id && u.PeriodBucket == period).ToListAsync();
        Assert.Equal(2, records.Count);

        // Phase 6.2B server-authoritative rule: an application-layer sum for enforcement
        // MUST filter to ServerDerived — proven here at the persisted-data level (a
        // client-reported hint is stored, but distinguishable and never silently merged
        // into the authoritative figure by the schema itself).
        var authoritative = records.Where(r => r.Source == UsageRecordSource.ServerDerived).Sum(r => r.SecondsUsed);
        Assert.Equal(120, authoritative);
    }

    // ---- SESSION ----

    [SkipIfNoDockerFact]
    public async Task Session_PersistAndRetrieve()
    {
        var account = NewAccount($"acct-session-{Guid.NewGuid()}");
        var device = new Device { Id = Guid.NewGuid(), AccountId = account.Id, Platform = DevicePlatform.Windows, Status = DeviceStatus.Authorized, RegisteredAt = DateTimeOffset.UtcNow, LastSeenAt = DateTimeOffset.UtcNow };
        var session = new Session { Id = Guid.NewGuid(), AccountId = account.Id, DeviceId = device.Id, RefreshTokenFamilyId = Guid.NewGuid(), IssuedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(30) };

        await using (var db = fixture.CreateContext())
        {
            db.Accounts.Add(account);
            db.Devices.Add(device);
            db.Sessions.Add(session);
            await db.SaveChangesAsync();
        }

        await using var readDb = fixture.CreateContext();
        var reloaded = await readDb.Sessions.FindAsync(session.Id);
        Assert.NotNull(reloaded);
        Assert.Equal(account.Id, reloaded!.AccountId);
        Assert.True(reloaded.IsActive);
    }

    // ---- AUDIT ----

    [SkipIfNoDockerFact]
    public async Task AuditEvent_PersistAndQueryByAccount()
    {
        var account = NewAccount($"acct-audit-{Guid.NewGuid()}");
        await using (var db = fixture.CreateContext())
        {
            db.Accounts.Add(account);
            db.AuditEvents.Add(new AuditEvent { Id = Guid.NewGuid(), AccountId = account.Id, EventType = "Login", OccurredAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        await using var readDb = fixture.CreateContext();
        var events = await readDb.AuditEvents.Where(e => e.AccountId == account.Id).ToListAsync();
        Assert.Single(events);
        Assert.Equal("Login", events[0].EventType);
    }

    // ---- CROSS-ACCOUNT ISOLATION / SECURITY ----

    [SkipIfNoDockerFact]
    public async Task CrossAccountAccess_CustomerACannotRetrieveCustomerBsUsageRecordsByAccountFilter()
    {
        var accountA = NewAccount($"acct-cross-a-{Guid.NewGuid()}");
        var accountB = NewAccount($"acct-cross-b-{Guid.NewGuid()}");
        var deviceB = new Device { Id = Guid.NewGuid(), AccountId = accountB.Id, Platform = DevicePlatform.Windows, Status = DeviceStatus.Authorized, RegisteredAt = DateTimeOffset.UtcNow, LastSeenAt = DateTimeOffset.UtcNow };
        const string period = "2026-09";

        await using (var db = fixture.CreateContext())
        {
            db.Accounts.AddRange(accountA, accountB);
            db.Devices.Add(deviceB);
            db.UsageRecords.Add(new UsageRecord { Id = Guid.NewGuid(), AccountId = accountB.Id, DeviceId = deviceB.Id, Direction = "en-US:de-DE", SecondsUsed = 42, Source = UsageRecordSource.ServerDerived, PeriodBucket = period, RecordedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        // Simulates what a repository call scoped to "the authenticated caller's own
        // account" would do — querying with accountA's id must never surface accountB's
        // row, regardless of what id a client might otherwise supply.
        await using var readDb = fixture.CreateContext();
        var accountAUsage = await readDb.UsageRecords.Where(u => u.AccountId == accountA.Id && u.PeriodBucket == period).ToListAsync();
        Assert.Empty(accountAUsage);
    }

    [SkipIfNoDockerFact]
    public async Task Account_RemainsSuspended_AfterProcessRestartSimulation()
    {
        // "Restart" simulated by disposing every DbContext/connection in scope and
        // opening a brand-new one against the same underlying database — the closest
        // in-process approximation of a real application restart without tearing down
        // the test container itself.
        var account = NewAccount($"acct-restart-{Guid.NewGuid()}", status: AccountStatus.Suspended);

        await using (var db = fixture.CreateContext())
        {
            db.Accounts.Add(account);
            await db.SaveChangesAsync();
        } // context disposed — connection closed

        await using var freshDb = fixture.CreateContext(); // brand-new context/connection
        var reloaded = await freshDb.Accounts.FirstAsync(a => a.Id == account.Id);
        Assert.Equal(AccountStatus.Suspended, reloaded.Status);
        Assert.False(reloaded.IsUsable);
    }
}
