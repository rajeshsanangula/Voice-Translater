using Microsoft.EntityFrameworkCore;
using Npgsql;
using VTTranslate.Backend.Domain.Abstractions;
using VTTranslate.Backend.Domain.Entities;

namespace VTTranslate.Backend.Infrastructure.Persistence.EfCore;

// PostgreSQL-backed repository implementations (Phase 6.5) — the real production
// counterparts to VTTranslate.Backend.Infrastructure.Persistence's in-memory
// implementations, which remain for unit tests/local dev only (see that file's own
// header comment). Application/Domain code depends only on the IXxxRepository
// interfaces; nothing outside this file/namespace references AutraxisDbContext.
//
// SaveAsync on every repository here is an upsert (Add-if-new, otherwise EF's own
// change-tracking on an already-attached/re-attached entity) and calls SaveChangesAsync
// itself — one repository call is one atomic unit of work, matching the granularity the
// Application layer already assumes (Phase 6.3's in-memory repositories behave the same
// way). No repository call spans multiple aggregate roots in this phase, so no broader
// explicit transaction boundary is introduced — see
// docs/phase-6.5-database-persistence.md §10 for why that is a deliberate, not an
// accidental, scope decision.

public sealed class EfAccountRepository(AutraxisDbContext db) : IAccountRepository
{
    public Task<Account?> FindByIdAsync(Guid accountId, CancellationToken ct) =>
        db.Accounts.FirstOrDefaultAsync(a => a.Id == accountId, ct);

    public Task<Account?> FindByExternalIdentityAsync(string provider, string externalSubjectId, CancellationToken ct) =>
        db.Accounts.FirstOrDefaultAsync(a => a.ExternalIdentityProvider == provider && a.ExternalSubjectId == externalSubjectId, ct);

    public async Task SaveAsync(Account account, CancellationToken ct)
    {
        var existing = await db.Accounts.FirstOrDefaultAsync(a => a.Id == account.Id, ct);
        if (existing is null) db.Accounts.Add(account);
        else db.Entry(existing).CurrentValues.SetValues(account);
        await db.SaveChangesAsync(ct);
    }

    // Phase 6.7 — real PostgreSQL row lock (SELECT ... FOR UPDATE), closing the
    // check-then-act device-registration race (docs/phase-6.7-device-licensing-policy.md
    // §6). Executed via ExecuteSqlInterpolatedAsync rather than a materializing query —
    // only the lock's side effect is needed, not the row's data. Must be called inside an
    // IUnitOfWork transaction; the lock is held until that transaction commits/rolls back.
    public Task LockAccountForDeviceRegistrationAsync(Guid accountId, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM accounts WHERE \"Id\" = {accountId} FOR UPDATE", ct);
}

public sealed class EfProfileRepository(AutraxisDbContext db) : IProfileRepository
{
    public Task<Profile?> FindByAccountIdAsync(Guid accountId, CancellationToken ct) =>
        db.Profiles.FirstOrDefaultAsync(p => p.AccountId == accountId, ct);

    public async Task SaveAsync(Profile profile, CancellationToken ct)
    {
        var existing = await db.Profiles.FirstOrDefaultAsync(p => p.AccountId == profile.AccountId, ct);
        if (existing is null) db.Profiles.Add(profile);
        else db.Entry(existing).CurrentValues.SetValues(profile);
        await db.SaveChangesAsync(ct);
    }
}

public sealed class EfDeviceRepository(AutraxisDbContext db) : IDeviceRepository
{
    public Task<Device?> FindByIdAsync(Guid deviceId, CancellationToken ct) =>
        db.Devices.FirstOrDefaultAsync(d => d.Id == deviceId, ct);

    public async Task<IReadOnlyList<Device>> ListByAccountAsync(Guid accountId, CancellationToken ct) =>
        await db.Devices.Where(d => d.AccountId == accountId).ToListAsync(ct);

    public async Task SaveAsync(Device device, CancellationToken ct)
    {
        var existing = await db.Devices.FirstOrDefaultAsync(d => d.Id == device.Id, ct);
        if (existing is null) db.Devices.Add(device);
        else db.Entry(existing).CurrentValues.SetValues(device);
        await db.SaveChangesAsync(ct);
    }
}

public sealed class EfSubscriptionRepository(AutraxisDbContext db) : ISubscriptionRepository
{
    private static readonly VTTranslate.Backend.Domain.Enums.SubscriptionStatus[] LiveStatuses =
    [
        VTTranslate.Backend.Domain.Enums.SubscriptionStatus.Trial,
        VTTranslate.Backend.Domain.Enums.SubscriptionStatus.Active,
        VTTranslate.Backend.Domain.Enums.SubscriptionStatus.PastDue,
        VTTranslate.Backend.Domain.Enums.SubscriptionStatus.GracePeriod,
    ];

    // Phase 6.6: "the account's subscription" now means "the account's currently
    // LIVE subscription" — the partial unique index guarantees at most one such row
    // exists, so FirstOrDefault's "at most one match" assumption still holds.
    public Task<Subscription?> FindByAccountAsync(Guid accountId, CancellationToken ct) =>
        db.Subscriptions.FirstOrDefaultAsync(s => s.AccountId == accountId && LiveStatuses.Contains(s.Status), ct);

    public async Task<IReadOnlyList<Subscription>> FindHistoryByAccountAsync(Guid accountId, CancellationToken ct) =>
        await db.Subscriptions.Where(s => s.AccountId == accountId).ToListAsync(ct);

    public Task<Subscription?> FindByIdAsync(Guid subscriptionId, CancellationToken ct) =>
        db.Subscriptions.FirstOrDefaultAsync(s => s.Id == subscriptionId, ct);

    public Task<Subscription?> FindByBillingProviderSubscriptionIdAsync(string billingProviderSubscriptionId, CancellationToken ct) =>
        db.Subscriptions.FirstOrDefaultAsync(s => s.BillingProviderSubscriptionId == billingProviderSubscriptionId, ct);

    public async Task SaveAsync(Subscription subscription, CancellationToken ct)
    {
        var existing = await db.Subscriptions.FirstOrDefaultAsync(s => s.Id == subscription.Id, ct);
        if (existing is null) db.Subscriptions.Add(subscription);
        else db.Entry(existing).CurrentValues.SetValues(subscription);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Translate the EF Core-specific exception to a Domain-level one at this
            // boundary (Phase 6.6) — Application must never reference an EF Core type.
            throw new Domain.ConcurrentUpdateException(subscription.Id);
        }
    }
}

public sealed class EfBillingEventRepository(AutraxisDbContext db) : IBillingEventRepository
{
    // PostgreSQL SQLSTATE for a unique-constraint violation — used to distinguish "this
    // (Provider, ProviderEventId) already exists" (the idempotency mechanism) from any
    // other, unrelated database error, which must not be silently reinterpreted as a
    // duplicate.
    private const string UniqueViolationSqlState = "23505";

    public Task<BillingEvent?> FindByProviderEventIdAsync(string provider, string providerEventId, CancellationToken ct) =>
        db.BillingEvents.FirstOrDefaultAsync(e => e.Provider == provider && e.ProviderEventId == providerEventId, ct);

    public async Task SaveAsync(BillingEvent billingEvent, CancellationToken ct)
    {
        var existing = await db.BillingEvents.FirstOrDefaultAsync(e => e.Id == billingEvent.Id, ct);
        if (existing is null) db.BillingEvents.Add(billingEvent);
        else db.Entry(existing).CurrentValues.SetValues(billingEvent);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: UniqueViolationSqlState })
        {
            throw new Domain.DuplicateBillingEventException(billingEvent.Provider, billingEvent.ProviderEventId);
        }
    }
}

public sealed class EfPlanRepository(AutraxisDbContext db) : IPlanRepository
{
    public Task<Plan?> FindByIdAsync(Guid planId, CancellationToken ct) =>
        db.Plans.FirstOrDefaultAsync(p => p.Id == planId, ct);

    public async Task<IReadOnlyList<Entitlement>> GetEntitlementsAsync(Guid planId, CancellationToken ct) =>
        await db.Entitlements.Where(e => e.PlanId == planId).ToListAsync(ct);
}

public sealed class EfUsageRecordRepository(AutraxisDbContext db) : IUsageRecordRepository
{
    public async Task AddAsync(UsageRecord record, CancellationToken ct)
    {
        db.UsageRecords.Add(record);
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<UsageRecord>> ListByAccountAndPeriodAsync(Guid accountId, string periodBucket, CancellationToken ct) =>
        await db.UsageRecords.Where(r => r.AccountId == accountId && r.PeriodBucket == periodBucket).ToListAsync(ct);
}

public sealed class EfAuditEventRepository(AutraxisDbContext db) : IAuditEventRepository
{
    public async Task AddAsync(AuditEvent auditEvent, CancellationToken ct)
    {
        db.AuditEvents.Add(auditEvent);
        await db.SaveChangesAsync(ct);
    }
}

public sealed class EfSessionRepository(AutraxisDbContext db) : ISessionRepository
{
    public Task<Session?> FindByIdAsync(Guid sessionId, CancellationToken ct) =>
        db.Sessions.FirstOrDefaultAsync(s => s.Id == sessionId, ct);

    public async Task<IReadOnlyList<Session>> ListByAccountAsync(Guid accountId, CancellationToken ct) =>
        await db.Sessions.Where(s => s.AccountId == accountId).ToListAsync(ct);

    public async Task SaveAsync(Session session, CancellationToken ct)
    {
        var existing = await db.Sessions.FirstOrDefaultAsync(s => s.Id == session.Id, ct);
        if (existing is null) db.Sessions.Add(session);
        else db.Entry(existing).CurrentValues.SetValues(session);
        await db.SaveChangesAsync(ct);
    }
}
