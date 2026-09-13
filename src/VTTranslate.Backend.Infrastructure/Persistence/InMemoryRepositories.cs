using System.Collections.Concurrent;
using VTTranslate.Backend.Domain.Abstractions;
using VTTranslate.Backend.Domain.Entities;

namespace VTTranslate.Backend.Infrastructure.Persistence;

// NOT A PRODUCTION PERSISTENCE LAYER. In-memory, process-lifetime-only implementations
// of the Domain repository ports, used for (a) automated tests and (b) local
// development before a real database vendor is chosen — see
// docs/phase-6.3-backend-foundation.md "Persistence strategy" for why no vendor is
// committed to in this phase. All state is lost on process restart. Never register
// these for anything resembling a production deployment.

public sealed class InMemoryAccountRepository : IAccountRepository
{
    private readonly ConcurrentDictionary<Guid, Account> _byId = new();

    public Task<Account?> FindByIdAsync(Guid accountId, CancellationToken ct) =>
        Task.FromResult(_byId.GetValueOrDefault(accountId));

    public Task<Account?> FindByExternalIdentityAsync(string provider, string externalSubjectId, CancellationToken ct) =>
        Task.FromResult(_byId.Values.FirstOrDefault(a =>
            a.ExternalIdentityProvider == provider && a.ExternalSubjectId == externalSubjectId));

    public Task SaveAsync(Account account, CancellationToken ct)
    {
        _byId[account.Id] = account;
        return Task.CompletedTask;
    }
}

public sealed class InMemoryDeviceRepository : IDeviceRepository
{
    private readonly ConcurrentDictionary<Guid, Device> _byId = new();

    public Task<Device?> FindByIdAsync(Guid deviceId, CancellationToken ct) =>
        Task.FromResult(_byId.GetValueOrDefault(deviceId));

    public Task<IReadOnlyList<Device>> ListByAccountAsync(Guid accountId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<Device>>(_byId.Values.Where(d => d.AccountId == accountId).ToList());

    public Task SaveAsync(Device device, CancellationToken ct)
    {
        _byId[device.Id] = device;
        return Task.CompletedTask;
    }
}

public sealed class InMemorySubscriptionRepository : ISubscriptionRepository
{
    private readonly ConcurrentDictionary<Guid, Subscription> _byAccountId = new();

    public Task<Subscription?> FindByAccountAsync(Guid accountId, CancellationToken ct) =>
        Task.FromResult(_byAccountId.GetValueOrDefault(accountId));

    public Task SaveAsync(Subscription subscription, CancellationToken ct)
    {
        _byAccountId[subscription.AccountId] = subscription;
        return Task.CompletedTask;
    }
}

public sealed class InMemoryPlanRepository : IPlanRepository
{
    private readonly ConcurrentDictionary<Guid, Plan> _plans = new();
    private readonly ConcurrentDictionary<Guid, List<Entitlement>> _entitlementsByPlan = new();

    /// <summary>Test/dev seeding helper — not part of the <see cref="IPlanRepository"/> contract.</summary>
    public void Seed(Plan plan, IEnumerable<Entitlement> entitlements)
    {
        _plans[plan.Id] = plan;
        _entitlementsByPlan[plan.Id] = entitlements.ToList();
    }

    public Task<Plan?> FindByIdAsync(Guid planId, CancellationToken ct) =>
        Task.FromResult(_plans.GetValueOrDefault(planId));

    public Task<IReadOnlyList<Entitlement>> GetEntitlementsAsync(Guid planId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<Entitlement>>(_entitlementsByPlan.GetValueOrDefault(planId) ?? []);
}

public sealed class InMemoryUsageRecordRepository : IUsageRecordRepository
{
    private readonly ConcurrentBag<UsageRecord> _records = new();

    public Task AddAsync(UsageRecord record, CancellationToken ct)
    {
        _records.Add(record);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<UsageRecord>> ListByAccountAndPeriodAsync(Guid accountId, string periodBucket, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<UsageRecord>>(
            _records.Where(r => r.AccountId == accountId && r.PeriodBucket == periodBucket).ToList());
}

public sealed class InMemoryAuditEventRepository : IAuditEventRepository
{
    private readonly ConcurrentBag<AuditEvent> _events = new();

    public Task AddAsync(AuditEvent auditEvent, CancellationToken ct)
    {
        _events.Add(auditEvent);
        return Task.CompletedTask;
    }

    /// <summary>Test-only accessor — not part of the <see cref="IAuditEventRepository"/> contract.</summary>
    public IReadOnlyList<AuditEvent> Events => _events.ToList();
}

public sealed class InMemoryProfileRepository : IProfileRepository
{
    private readonly ConcurrentDictionary<Guid, Profile> _byAccountId = new();

    public Task<Profile?> FindByAccountIdAsync(Guid accountId, CancellationToken ct) =>
        Task.FromResult(_byAccountId.GetValueOrDefault(accountId));

    public Task SaveAsync(Profile profile, CancellationToken ct)
    {
        _byAccountId[profile.AccountId] = profile;
        return Task.CompletedTask;
    }
}

public sealed class InMemorySessionRepository : ISessionRepository
{
    private readonly ConcurrentDictionary<Guid, Session> _byId = new();

    public Task<Session?> FindByIdAsync(Guid sessionId, CancellationToken ct) =>
        Task.FromResult(_byId.GetValueOrDefault(sessionId));

    public Task<IReadOnlyList<Session>> ListByAccountAsync(Guid accountId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<Session>>(_byId.Values.Where(s => s.AccountId == accountId).ToList());

    public Task SaveAsync(Session session, CancellationToken ct)
    {
        _byId[session.Id] = session;
        return Task.CompletedTask;
    }
}
