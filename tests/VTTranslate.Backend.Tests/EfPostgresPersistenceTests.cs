using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using VTTranslate.Backend.Application.Devices;
using VTTranslate.Backend.Application.Entitlements;
using VTTranslate.Backend.Application.Sessions;
using VTTranslate.Backend.Domain;
using VTTranslate.Backend.Domain.Entities;
using VTTranslate.Backend.Domain.Enums;
using VTTranslate.Backend.Infrastructure.Persistence.EfCore;
using VTTranslate.Backend.Infrastructure.Time;

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

        var tableNames = new[] { "accounts", "profiles", "plans", "entitlements", "subscriptions", "devices", "sessions", "usage_records", "audit_events", "billing_events", "provider_access_grants", "translation_sessions" };
        foreach (var table in tableNames)
        {
            // EF Core's SqlQuery<T> for a scalar T wraps the raw SQL as
            // `SELECT t.Value FROM (<sql>) AS t` and requires the raw SQL's own result
            // column to be named "Value" — PostgreSQL names an unaliased EXISTS(...)
            // column "exists", not "Value", so the wrapper's `t.Value` reference doesn't
            // exist unless the column is explicitly aliased here.
            var exists = await db.Database.SqlQuery<bool>(
                $"SELECT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = {table}) AS \"Value\"").FirstAsync();
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

    // ---- Phase 6.6: partial unique index (at most one LIVE subscription per account) ----

    [SkipIfNoDockerFact]
    public async Task Subscription_TwoLiveSubscriptionsForSameAccount_RejectedByPartialUniqueIndex()
    {
        var account = NewAccount($"acct-live-dup-{Guid.NewGuid()}");
        var plan = new Plan { Id = Guid.NewGuid(), Name = "Live Dup Plan", IsPubliclyPurchasable = true };
        await using var db = fixture.CreateContext();
        db.Accounts.Add(account);
        db.Plans.Add(plan);
        db.Subscriptions.Add(new Subscription { Id = Guid.NewGuid(), AccountId = account.Id, PlanId = plan.Id, Status = SubscriptionStatus.Active, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();

        // A second LIVE (Trial) subscription for the SAME account must be rejected.
        db.Subscriptions.Add(new Subscription { Id = Guid.NewGuid(), AccountId = account.Id, PlanId = plan.Id, Status = SubscriptionStatus.Trial, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [SkipIfNoDockerFact]
    public async Task Subscription_LiveAndCancelled_AllowedTogether_ReSubscriptionAfterCancellation()
    {
        // The whole point of the Phase 6.6 cardinality change (§1 of the revised
        // design): a cancelled (terminal, historical) row must NOT block a new live
        // subscription for the same account.
        var account = NewAccount($"acct-resubscribe-{Guid.NewGuid()}");
        var plan = new Plan { Id = Guid.NewGuid(), Name = "Resubscribe Plan", IsPubliclyPurchasable = true };
        await using var db = fixture.CreateContext();
        db.Accounts.Add(account);
        db.Plans.Add(plan);
        db.Subscriptions.Add(new Subscription { Id = Guid.NewGuid(), AccountId = account.Id, PlanId = plan.Id, Status = SubscriptionStatus.Cancelled, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();

        db.Subscriptions.Add(new Subscription { Id = Guid.NewGuid(), AccountId = account.Id, PlanId = plan.Id, Status = SubscriptionStatus.Active, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync(); // must NOT throw

        await using var readDb = fixture.CreateContext();
        var history = await readDb.Subscriptions.Where(s => s.AccountId == account.Id).ToListAsync();
        Assert.Equal(2, history.Count);
        Assert.Single(history, s => s.Status == SubscriptionStatus.Active);
        Assert.Single(history, s => s.Status == SubscriptionStatus.Cancelled);
    }

    // ---- Phase 6.6: BillingEvent idempotency/FK constraints ----

    [SkipIfNoDockerFact]
    public async Task BillingEvent_DuplicateProviderEventId_RejectedByUniqueConstraint()
    {
        var providerEventId = $"evt-{Guid.NewGuid()}";
        await using var db = fixture.CreateContext();
        db.BillingEvents.Add(new BillingEvent { Id = Guid.NewGuid(), Provider = "Paddle", ProviderEventId = providerEventId, EventType = "PaymentSucceeded", ReceivedAt = DateTimeOffset.UtcNow, ProcessingStatus = BillingEventProcessingStatus.Received, RawPayloadHash = "hash1" });
        await db.SaveChangesAsync();

        db.BillingEvents.Add(new BillingEvent { Id = Guid.NewGuid(), Provider = "Paddle", ProviderEventId = providerEventId, EventType = "PaymentSucceeded", ReceivedAt = DateTimeOffset.UtcNow, ProcessingStatus = BillingEventProcessingStatus.Received, RawPayloadHash = "hash2" });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [SkipIfNoDockerFact]
    public async Task BillingEvent_PersistAndQueryByAccountAndTime_CorrelatesToSubscription()
    {
        var account = NewAccount($"acct-billingevent-{Guid.NewGuid()}");
        var plan = new Plan { Id = Guid.NewGuid(), Name = "Billing Event Plan", IsPubliclyPurchasable = true };
        var subscription = new Subscription { Id = Guid.NewGuid(), AccountId = account.Id, PlanId = plan.Id, Status = SubscriptionStatus.Active, BillingProviderSubscriptionId = "sub-ef-1", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };

        await using (var db = fixture.CreateContext())
        {
            db.Accounts.Add(account);
            db.Plans.Add(plan);
            db.Subscriptions.Add(subscription);
            db.BillingEvents.Add(new BillingEvent { Id = Guid.NewGuid(), Provider = "Paddle", ProviderEventId = $"evt-{Guid.NewGuid()}", EventType = "PaymentFailed", AccountId = account.Id, SubscriptionId = subscription.Id, ReceivedAt = DateTimeOffset.UtcNow, ProcessingStatus = BillingEventProcessingStatus.Processed, RawPayloadHash = "hash" });
            await db.SaveChangesAsync();
        }

        await using var readDb = fixture.CreateContext();
        var events = await readDb.BillingEvents.Where(e => e.AccountId == account.Id).ToListAsync();
        Assert.Single(events);
        Assert.Equal(subscription.Id, events[0].SubscriptionId);
    }

    [SkipIfNoDockerFact]
    public async Task BillingEvent_ForeignKeyViolation_RejectedForUnknownSubscription()
    {
        await using var db = fixture.CreateContext();
        db.BillingEvents.Add(new BillingEvent { Id = Guid.NewGuid(), Provider = "Paddle", ProviderEventId = $"evt-{Guid.NewGuid()}", EventType = "PaymentFailed", SubscriptionId = Guid.NewGuid(), ReceivedAt = DateTimeOffset.UtcNow, ProcessingStatus = BillingEventProcessingStatus.Rejected, RawPayloadHash = "hash" });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    // ---- Phase 6.6: transaction-boundary rollback (real PostgreSQL, not simulated) ----

    [SkipIfNoDockerFact]
    public async Task UnitOfWork_ExceptionInsideTransaction_RollsBackBothSubscriptionAndBillingEventWrites()
    {
        var account = NewAccount($"acct-txn-{Guid.NewGuid()}");
        var plan = new Plan { Id = Guid.NewGuid(), Name = "Txn Plan", IsPubliclyPurchasable = true };
        var subscription = new Subscription { Id = Guid.NewGuid(), AccountId = account.Id, PlanId = plan.Id, Status = SubscriptionStatus.Active, BillingProviderSubscriptionId = "sub-txn-1", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
        var billingEvent = new BillingEvent { Id = Guid.NewGuid(), Provider = "Paddle", ProviderEventId = $"evt-txn-{Guid.NewGuid()}", EventType = "PaymentFailed", ReceivedAt = DateTimeOffset.UtcNow, ProcessingStatus = BillingEventProcessingStatus.Received, RawPayloadHash = "hash" };

        await using (var seedDb = fixture.CreateContext())
        {
            seedDb.Accounts.Add(account);
            seedDb.Plans.Add(plan);
            seedDb.Subscriptions.Add(subscription);
            seedDb.BillingEvents.Add(billingEvent);
            await seedDb.SaveChangesAsync();
        }

        await using (var txnDb = fixture.CreateContext())
        {
            var unitOfWork = new EfUnitOfWork(txnDb);
            await Assert.ThrowsAsync<InvalidOperationException>(() => unitOfWork.ExecuteInTransactionAsync<object?>(async ct =>
            {
                // Mutate BOTH rows inside the transaction, then throw before it can commit
                // — simulates exactly the "application attempt" transaction described in
                // docs/phase-6.6-billing-subscription.md §7 step 3.
                var sub = await txnDb.Subscriptions.FirstAsync(s => s.Id == subscription.Id, ct);
                sub.Status = SubscriptionStatus.PastDue;

                var evt = await txnDb.BillingEvents.FirstAsync(e => e.Id == billingEvent.Id, ct);
                evt.ProcessingStatus = BillingEventProcessingStatus.Processed;

                await txnDb.SaveChangesAsync(ct);
                throw new InvalidOperationException("simulated transient failure after the write, before commit");
            }, CancellationToken.None));
        }

        // A FRESH context/connection — proves the rollback is real (committed to
        // PostgreSQL, not just an uncommitted change-tracker artifact in the same context).
        await using var readDb = fixture.CreateContext();
        var reloadedSubscription = await readDb.Subscriptions.FirstAsync(s => s.Id == subscription.Id);
        var reloadedEvent = await readDb.BillingEvents.FirstAsync(e => e.Id == billingEvent.Id);

        Assert.Equal(SubscriptionStatus.Active, reloadedSubscription.Status); // unchanged — rolled back
        Assert.Equal(BillingEventProcessingStatus.Received, reloadedEvent.ProcessingStatus); // unchanged — rolled back, NOT Failed
    }

    // ---- Phase 6.8: provider-access grant persistence/transaction rollback ----

    [SkipIfNoDockerFact]
    public async Task ProviderAccessGrant_PersistAndQueryByAccount()
    {
        var account = NewAccount($"acct-grant-{Guid.NewGuid()}");
        var plan = new Plan { Id = Guid.NewGuid(), Name = "Grant Plan", IsPubliclyPurchasable = true };
        var device = new Device { Id = Guid.NewGuid(), AccountId = account.Id, Platform = DevicePlatform.Windows, Status = DeviceStatus.Authorized, RegisteredAt = DateTimeOffset.UtcNow, LastSeenAt = DateTimeOffset.UtcNow };

        await using (var db = fixture.CreateContext())
        {
            db.Accounts.Add(account);
            db.Plans.Add(plan);
            db.Devices.Add(device);
            db.ProviderAccessGrants.Add(new ProviderAccessGrant
            {
                Id = Guid.NewGuid(), AccountId = account.Id, DeviceId = device.Id,
                Provider = Provider.AzureSpeech, Capability = ProviderCapability.SpeechRecognition,
                IssuedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10), CorrelationId = Guid.NewGuid(),
            });
            await db.SaveChangesAsync();
        }

        await using var readDb = fixture.CreateContext();
        var grants = await readDb.ProviderAccessGrants.Where(g => g.AccountId == account.Id).ToListAsync();
        Assert.Single(grants);
        Assert.Equal(device.Id, grants[0].DeviceId);
    }

    [SkipIfNoDockerFact]
    public async Task ProviderAccessGrant_AccountIsolation_QueryByAccountNeverReturnsAnotherAccountsGrant()
    {
        var accountA = NewAccount($"acct-grant-iso-a-{Guid.NewGuid()}");
        var accountB = NewAccount($"acct-grant-iso-b-{Guid.NewGuid()}");
        var deviceA = new Device { Id = Guid.NewGuid(), AccountId = accountA.Id, Platform = DevicePlatform.Windows, Status = DeviceStatus.Authorized, RegisteredAt = DateTimeOffset.UtcNow, LastSeenAt = DateTimeOffset.UtcNow };
        var deviceB = new Device { Id = Guid.NewGuid(), AccountId = accountB.Id, Platform = DevicePlatform.Windows, Status = DeviceStatus.Authorized, RegisteredAt = DateTimeOffset.UtcNow, LastSeenAt = DateTimeOffset.UtcNow };

        await using (var db = fixture.CreateContext())
        {
            db.Accounts.AddRange(accountA, accountB);
            db.Devices.AddRange(deviceA, deviceB);
            db.ProviderAccessGrants.Add(new ProviderAccessGrant { Id = Guid.NewGuid(), AccountId = accountA.Id, DeviceId = deviceA.Id, Provider = Provider.AzureSpeech, Capability = ProviderCapability.SpeechRecognition, IssuedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10), CorrelationId = Guid.NewGuid() });
            db.ProviderAccessGrants.Add(new ProviderAccessGrant { Id = Guid.NewGuid(), AccountId = accountB.Id, DeviceId = deviceB.Id, Provider = Provider.AzureSpeech, Capability = ProviderCapability.SpeechRecognition, IssuedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10), CorrelationId = Guid.NewGuid() });
            await db.SaveChangesAsync();
        }

        await using var readDb = fixture.CreateContext();
        var accountAGrants = await readDb.ProviderAccessGrants.Where(g => g.AccountId == accountA.Id).ToListAsync();
        Assert.Single(accountAGrants);
        Assert.Equal(deviceA.Id, accountAGrants[0].DeviceId);
        Assert.DoesNotContain(accountAGrants, g => g.AccountId == accountB.Id);
    }

    [SkipIfNoDockerFact]
    public async Task ProviderAccessGrant_ForeignKeyViolation_RejectedForUnknownDevice()
    {
        var account = NewAccount($"acct-grant-fk-{Guid.NewGuid()}");
        await using var db = fixture.CreateContext();
        db.Accounts.Add(account);
        db.ProviderAccessGrants.Add(new ProviderAccessGrant
        {
            Id = Guid.NewGuid(), AccountId = account.Id, DeviceId = Guid.NewGuid(),
            Provider = Provider.AzureSpeech, Capability = ProviderCapability.SpeechRecognition,
            IssuedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10), CorrelationId = Guid.NewGuid(),
        });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [SkipIfNoDockerFact]
    public async Task ProviderAccessIssuance_ExceptionInsideTransaction_RollsBackBothGrantAndAuditWrites()
    {
        var account = NewAccount($"acct-grant-txn-{Guid.NewGuid()}");
        var device = new Device { Id = Guid.NewGuid(), AccountId = account.Id, Platform = DevicePlatform.Windows, Status = DeviceStatus.Authorized, RegisteredAt = DateTimeOffset.UtcNow, LastSeenAt = DateTimeOffset.UtcNow };

        await using (var seedDb = fixture.CreateContext())
        {
            seedDb.Accounts.Add(account);
            seedDb.Devices.Add(device);
            await seedDb.SaveChangesAsync();
        }

        var grantId = Guid.NewGuid();
        await using (var txnDb = fixture.CreateContext())
        {
            var unitOfWork = new EfUnitOfWork(txnDb);
            await Assert.ThrowsAsync<InvalidOperationException>(() => unitOfWork.ExecuteInTransactionAsync<object?>(async ct =>
            {
                txnDb.ProviderAccessGrants.Add(new ProviderAccessGrant
                {
                    Id = grantId, AccountId = account.Id, DeviceId = device.Id,
                    Provider = Provider.AzureSpeech, Capability = ProviderCapability.SpeechRecognition,
                    IssuedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10), CorrelationId = Guid.NewGuid(),
                });
                await txnDb.SaveChangesAsync(ct);

                throw new InvalidOperationException("simulated failure after the grant write, before commit (e.g. the audit write failing)");
            }, CancellationToken.None));
        }

        await using var readDb = fixture.CreateContext();
        var reloaded = await readDb.ProviderAccessGrants.FirstOrDefaultAsync(g => g.Id == grantId);
        Assert.Null(reloaded); // rolled back — never partially persisted
    }

    // ---- Phase 6.7: real PostgreSQL device-registration concurrency ----

    [SkipIfNoDockerFact]
    public async Task ConcurrentDeviceRegistration_NeverExceedsPooledLimit_RealPostgresLock()
    {
        const int maxActiveDevices = 3;
        const int concurrentAttempts = 10;

        var account = NewAccount($"acct-device-race-{Guid.NewGuid()}");
        var plan = new Plan { Id = Guid.NewGuid(), Name = "Device Race Plan", IsPubliclyPurchasable = true };
        var entitlement = new Entitlement { Id = Guid.NewGuid(), PlanId = plan.Id, Key = EntitlementKeys.MaxActiveDevices, Value = maxActiveDevices.ToString() };
        var subscription = new Subscription
        {
            Id = Guid.NewGuid(), AccountId = account.Id, PlanId = plan.Id, Status = SubscriptionStatus.Active,
            CurrentPeriodStart = DateTimeOffset.UtcNow.AddDays(-1), CurrentPeriodEnd = DateTimeOffset.UtcNow.AddDays(30),
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };

        await using (var seedDb = fixture.CreateContext())
        {
            seedDb.Accounts.Add(account);
            seedDb.Plans.Add(plan);
            seedDb.Entitlements.Add(entitlement);
            seedDb.Subscriptions.Add(subscription);
            await seedDb.SaveChangesAsync();
        }

        // Each concurrent "caller" gets its OWN DbContext/connection — a single shared
        // DbContext is not safe for parallel use and would not exercise the real
        // multi-connection race this test exists to prove is closed.
        var tasks = Enumerable.Range(0, concurrentAttempts).Select(async _ =>
        {
            await using var db = fixture.CreateContext();
            var service = new DeviceRegistrationService(
                new EfDeviceRepository(db), new EfSubscriptionRepository(db), new EfPlanRepository(db),
                new EfAccountRepository(db), new EfUnitOfWork(db), new EfAuditEventRepository(db), new SystemClock());
            try
            {
                await service.RegisterDeviceAsync(account.Id, DevicePlatform.Windows, "Concurrent", CancellationToken.None);
                return true;
            }
            catch (DeviceLimitExceededException)
            {
                return false;
            }
        });

        var results = await Task.WhenAll(tasks);

        Assert.Equal(maxActiveDevices, results.Count(r => r)); // exactly the limit succeeded, never more
        Assert.Equal(concurrentAttempts - maxActiveDevices, results.Count(r => !r));

        await using var readDb = fixture.CreateContext();
        var actualDeviceCount = await readDb.Devices.CountAsync(d => d.AccountId == account.Id && d.Status != DeviceStatus.Revoked);
        Assert.Equal(maxActiveDevices, actualDeviceCount); // the invariant, verified against real committed data
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

    // ---- Phase 6.9: TranslationSession persistence / partial unique index / rollback / concurrency ----

    [SkipIfNoDockerFact]
    public async Task TranslationSession_PersistAndQueryByAccount()
    {
        var account = NewAccount($"acct-session-persist-{Guid.NewGuid()}");
        var device = new Device { Id = Guid.NewGuid(), AccountId = account.Id, Platform = DevicePlatform.Windows, Status = DeviceStatus.Authorized, RegisteredAt = DateTimeOffset.UtcNow, LastSeenAt = DateTimeOffset.UtcNow };
        var session = new TranslationSession { Id = Guid.NewGuid(), AccountId = account.Id, DeviceId = device.Id, ClientSessionId = "corr-persist", State = TranslationSessionState.Active, StartedAt = DateTimeOffset.UtcNow, LastActivityAt = DateTimeOffset.UtcNow };

        await using (var db = fixture.CreateContext())
        {
            db.Accounts.Add(account);
            db.Devices.Add(device);
            db.TranslationSessions.Add(session);
            await db.SaveChangesAsync();
        }

        await using var readDb = fixture.CreateContext();
        var loaded = await readDb.TranslationSessions.Where(s => s.AccountId == account.Id).ToListAsync();
        Assert.Single(loaded);
        Assert.Equal(TranslationSessionState.Active, loaded[0].State);
    }

    [SkipIfNoDockerFact]
    public async Task TranslationSession_ForeignKeyViolation_RejectedForUnknownDevice()
    {
        var account = NewAccount($"acct-session-fk-{Guid.NewGuid()}");
        await using var db = fixture.CreateContext();
        db.Accounts.Add(account);
        db.TranslationSessions.Add(new TranslationSession { Id = Guid.NewGuid(), AccountId = account.Id, DeviceId = Guid.NewGuid(), State = TranslationSessionState.Active, StartedAt = DateTimeOffset.UtcNow, LastActivityAt = DateTimeOffset.UtcNow });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [SkipIfNoDockerFact]
    public async Task TranslationSession_TwoActiveSameClientSessionId_RejectedByPartialUniqueIndex()
    {
        var account = NewAccount($"acct-session-dup-{Guid.NewGuid()}");
        var device = new Device { Id = Guid.NewGuid(), AccountId = account.Id, Platform = DevicePlatform.Windows, Status = DeviceStatus.Authorized, RegisteredAt = DateTimeOffset.UtcNow, LastSeenAt = DateTimeOffset.UtcNow };
        await using var db = fixture.CreateContext();
        db.Accounts.Add(account);
        db.Devices.Add(device);
        db.TranslationSessions.Add(new TranslationSession { Id = Guid.NewGuid(), AccountId = account.Id, DeviceId = device.Id, ClientSessionId = "corr-dup", State = TranslationSessionState.Active, StartedAt = DateTimeOffset.UtcNow, LastActivityAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();

        db.TranslationSessions.Add(new TranslationSession { Id = Guid.NewGuid(), AccountId = account.Id, DeviceId = device.Id, ClientSessionId = "corr-dup", State = TranslationSessionState.Active, StartedAt = DateTimeOffset.UtcNow, LastActivityAt = DateTimeOffset.UtcNow });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [SkipIfNoDockerFact]
    public async Task TranslationSession_OneActiveOneTerminal_SameClientSessionId_AllowedTogether()
    {
        // The whole point of scoping the unique index to State = 'Active': a terminal
        // (Ended) row must NOT block a genuinely new session reusing the same
        // clientSessionId after reconnect.
        var account = NewAccount($"acct-session-reconnect-{Guid.NewGuid()}");
        var device = new Device { Id = Guid.NewGuid(), AccountId = account.Id, Platform = DevicePlatform.Windows, Status = DeviceStatus.Authorized, RegisteredAt = DateTimeOffset.UtcNow, LastSeenAt = DateTimeOffset.UtcNow };
        await using var db = fixture.CreateContext();
        db.Accounts.Add(account);
        db.Devices.Add(device);
        db.TranslationSessions.Add(new TranslationSession { Id = Guid.NewGuid(), AccountId = account.Id, DeviceId = device.Id, ClientSessionId = "corr-reconnect", State = TranslationSessionState.Ended, StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5), LastActivityAt = DateTimeOffset.UtcNow.AddMinutes(-5), TerminalAt = DateTimeOffset.UtcNow.AddMinutes(-4) });
        await db.SaveChangesAsync();

        db.TranslationSessions.Add(new TranslationSession { Id = Guid.NewGuid(), AccountId = account.Id, DeviceId = device.Id, ClientSessionId = "corr-reconnect", State = TranslationSessionState.Active, StartedAt = DateTimeOffset.UtcNow, LastActivityAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync(); // must NOT throw

        await using var readDb = fixture.CreateContext();
        var history = await readDb.TranslationSessions.Where(s => s.AccountId == account.Id).ToListAsync();
        Assert.Equal(2, history.Count);
        Assert.Single(history, s => s.State == TranslationSessionState.Active);
        Assert.Single(history, s => s.State == TranslationSessionState.Ended);
    }

    [SkipIfNoDockerFact]
    public async Task TranslationSessionEnd_ExceptionInsideTransaction_RollsBackBothSessionAndUsageWrites()
    {
        var account = NewAccount($"acct-session-txn-{Guid.NewGuid()}");
        var device = new Device { Id = Guid.NewGuid(), AccountId = account.Id, Platform = DevicePlatform.Windows, Status = DeviceStatus.Authorized, RegisteredAt = DateTimeOffset.UtcNow, LastSeenAt = DateTimeOffset.UtcNow };
        var session = new TranslationSession { Id = Guid.NewGuid(), AccountId = account.Id, DeviceId = device.Id, State = TranslationSessionState.Active, StartedAt = DateTimeOffset.UtcNow.AddSeconds(-30), LastActivityAt = DateTimeOffset.UtcNow.AddSeconds(-30) };

        await using (var seedDb = fixture.CreateContext())
        {
            seedDb.Accounts.Add(account);
            seedDb.Devices.Add(device);
            seedDb.TranslationSessions.Add(session);
            await seedDb.SaveChangesAsync();
        }

        await using (var txnDb = fixture.CreateContext())
        {
            var unitOfWork = new EfUnitOfWork(txnDb);
            await Assert.ThrowsAsync<InvalidOperationException>(() => unitOfWork.ExecuteInTransactionAsync<object?>(async ct =>
            {
                var toEnd = await txnDb.TranslationSessions.FirstAsync(s => s.Id == session.Id, ct);
                toEnd.State = TranslationSessionState.Ended;
                toEnd.TerminalAt = DateTimeOffset.UtcNow;

                txnDb.UsageRecords.Add(new UsageRecord { Id = Guid.NewGuid(), AccountId = account.Id, DeviceId = device.Id, Direction = "en-US:de-DE", SecondsUsed = 30, Source = UsageRecordSource.ServerDerived, PeriodBucket = DateTimeOffset.UtcNow.ToString("yyyy-MM"), RecordedAt = DateTimeOffset.UtcNow });

                await txnDb.SaveChangesAsync(ct);
                throw new InvalidOperationException("simulated failure after both writes, before commit");
            }, CancellationToken.None));
        }

        await using var readDb = fixture.CreateContext();
        var reloadedSession = await readDb.TranslationSessions.FirstAsync(s => s.Id == session.Id);
        var usageCount = await readDb.UsageRecords.CountAsync(u => u.AccountId == account.Id);

        Assert.Equal(TranslationSessionState.Active, reloadedSession.State); // unchanged — rolled back
        Assert.Equal(0, usageCount); // unchanged — rolled back, no orphaned usage record
    }

    [SkipIfNoDockerFact]
    public async Task ConcurrentSessionStart_NeverExceedsUsageLimit_RealPostgresLock()
    {
        const int usageLimitSeconds = 300; // remaining "allowance" is small and shared
        const int concurrentAttempts = 8;

        var account = NewAccount($"acct-session-race-{Guid.NewGuid()}");
        var plan = new Plan { Id = Guid.NewGuid(), Name = "Session Race Plan", IsPubliclyPurchasable = true };
        var maxDevicesEntitlement = new Entitlement { Id = Guid.NewGuid(), PlanId = plan.Id, Key = EntitlementKeys.MaxActiveDevices, Value = concurrentAttempts.ToString() };
        var usageLimitEntitlement = new Entitlement { Id = Guid.NewGuid(), PlanId = plan.Id, Key = EntitlementKeys.UsageLimitSecondsPerPeriod, Value = usageLimitSeconds.ToString() };
        var subscription = new Subscription
        {
            Id = Guid.NewGuid(), AccountId = account.Id, PlanId = plan.Id, Status = SubscriptionStatus.Active,
            CurrentPeriodStart = DateTimeOffset.UtcNow.AddDays(-1), CurrentPeriodEnd = DateTimeOffset.UtcNow.AddDays(30),
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        var deviceIds = Enumerable.Range(0, concurrentAttempts).Select(_ => Guid.NewGuid()).ToList();

        // Pre-existing ServerDerived usage leaves exactly ZERO remaining allowance —
        // every concurrent session-start attempt below must be denied by UsageDenied,
        // and none may slip through and record additional usage.
        var existingUsage = new UsageRecord { Id = Guid.NewGuid(), AccountId = account.Id, DeviceId = deviceIds[0], Direction = "en-US:de-DE", SecondsUsed = usageLimitSeconds, Source = UsageRecordSource.ServerDerived, PeriodBucket = DateTimeOffset.UtcNow.ToString("yyyy-MM"), RecordedAt = DateTimeOffset.UtcNow };

        await using (var seedDb = fixture.CreateContext())
        {
            seedDb.Accounts.Add(account);
            seedDb.Plans.Add(plan);
            seedDb.Entitlements.AddRange(maxDevicesEntitlement, usageLimitEntitlement);
            seedDb.Subscriptions.Add(subscription);
            seedDb.UsageRecords.Add(existingUsage);
            foreach (var deviceId in deviceIds)
                seedDb.Devices.Add(new Device { Id = deviceId, AccountId = account.Id, Platform = DevicePlatform.Windows, Status = DeviceStatus.Authorized, RegisteredAt = DateTimeOffset.UtcNow, LastSeenAt = DateTimeOffset.UtcNow });
            await seedDb.SaveChangesAsync();
        }

        // Each concurrent "caller" gets its OWN DbContext/connection/service graph — a
        // shared DbContext is not safe for parallel use and would not exercise the real
        // multi-connection race this test exists to prove is closed (§21/§36: an
        // in-memory test double must not falsely claim to prove this).
        var tasks = deviceIds.Select(async deviceId =>
        {
            await using var db = fixture.CreateContext();
            var clock = new SystemClock();
            var devices = new EfDeviceRepository(db);
            var subscriptions = new EfSubscriptionRepository(db);
            var plans = new EfPlanRepository(db);
            var accounts = new EfAccountRepository(db);
            var unitOfWork = new EfUnitOfWork(db);
            var audit = new EfAuditEventRepository(db);
            var deviceService = new DeviceRegistrationService(devices, subscriptions, plans, accounts, unitOfWork, audit, clock);
            var usageService = new VTTranslate.Backend.Application.Usage.UsageService(new EfUsageRecordRepository(db), clock);
            var entitlementService = new EntitlementService(subscriptions, plans, deviceService, usageService, clock);
            var sessionService = new TranslationSessionService(deviceService, entitlementService, new EfTranslationSessionRepository(db), accounts, usageService, audit, unitOfWork, clock, TimeSpan.FromSeconds(60));

            var result = await sessionService.StartSessionAsync(account.Id, deviceId, null, null, CancellationToken.None);
            return result.Outcome;
        });

        var results = await Task.WhenAll(tasks);

        // Usage allowance was already fully consumed before any attempt — none may
        // succeed; the race is over whether any attempt can slip past the exhausted
        // allowance check due to a stale read, not over how many succeed.
        Assert.All(results, outcome => Assert.Equal(SessionStartOutcome.UsageDenied, outcome));

        await using var readDb = fixture.CreateContext();
        var activeSessionCount = await readDb.TranslationSessions.CountAsync(s => s.AccountId == account.Id && s.State == TranslationSessionState.Active);
        Assert.Equal(0, activeSessionCount); // the invariant, verified against real committed data
    }
}
