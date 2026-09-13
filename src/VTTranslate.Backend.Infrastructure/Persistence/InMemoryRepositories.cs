using System.Collections.Concurrent;
using VTTranslate.Backend.Domain.Abstractions;
using VTTranslate.Backend.Domain.Entities;
using VTTranslate.Backend.Domain.Enums;

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

    /// <summary>No-op — see the interface's own doc comment. Single-process in-memory
    /// tests have no real concurrent-transaction race to protect against.</summary>
    public Task LockAccountForDeviceRegistrationAsync(Guid accountId, CancellationToken ct) =>
        Task.CompletedTask;

    /// <summary>No-op — see the interface's own doc comment (Phase 6.9).</summary>
    public Task LockAccountForUsageAccountingAsync(Guid accountId, CancellationToken ct) =>
        Task.CompletedTask;
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
    // "Live" here is a DENYLIST of terminal statuses (the inverse of the PostgreSQL
    // partial unique index's explicit ALLOWLIST — see SubscriptionConfiguration). This is
    // a deliberate test-double approximation: it lets an unrecognized/future status value
    // still be found and passed to EntitlementService's own exhaustive switch (so its
    // `default: deny` arm is actually exercisable in unit tests), rather than being
    // silently filtered out here first. Production behavior is governed by the real,
    // stricter EF/Postgres allowlist, not by this in-memory approximation.
    private static readonly SubscriptionStatus[] TerminalStatuses =
    [
        SubscriptionStatus.Cancelled, SubscriptionStatus.Expired, SubscriptionStatus.Refunded,
    ];

    private readonly ConcurrentDictionary<Guid, Subscription> _byId = new();

    public Task<Subscription?> FindByAccountAsync(Guid accountId, CancellationToken ct) =>
        Task.FromResult(_byId.Values.FirstOrDefault(s => s.AccountId == accountId && !TerminalStatuses.Contains(s.Status)));

    public Task<IReadOnlyList<Subscription>> FindHistoryByAccountAsync(Guid accountId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<Subscription>>(_byId.Values.Where(s => s.AccountId == accountId).ToList());

    public Task<Subscription?> FindByIdAsync(Guid subscriptionId, CancellationToken ct) =>
        Task.FromResult(_byId.GetValueOrDefault(subscriptionId));

    public Task<Subscription?> FindByBillingProviderSubscriptionIdAsync(string billingProviderSubscriptionId, CancellationToken ct) =>
        Task.FromResult(_byId.Values.FirstOrDefault(s => s.BillingProviderSubscriptionId == billingProviderSubscriptionId));

    public Task SaveAsync(Subscription subscription, CancellationToken ct)
    {
        // Mirrors the partial unique index enforced at the database layer (Phase 6.6) —
        // at most one LIVE subscription per account, even though many historical rows
        // may exist. Guards the in-memory/test path the same way PostgreSQL guards
        // production.
        if (!TerminalStatuses.Contains(subscription.Status) &&
            _byId.Values.Any(s => s.AccountId == subscription.AccountId && s.Id != subscription.Id && !TerminalStatuses.Contains(s.Status)))
        {
            throw new InvalidOperationException($"Account '{subscription.AccountId}' already has a live subscription — cannot save a second one.");
        }

        _byId[subscription.Id] = subscription;
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

/// <summary>Dev/test-only — no real transaction; the delegate simply runs (the in-memory repositories have no partial-failure mode to protect against in a single-process test).</summary>
public sealed class InMemoryUnitOfWork : IUnitOfWork
{
    public Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken ct) =>
        operation(ct);
}

public sealed class InMemoryProviderAccessRepository : IProviderAccessRepository
{
    private readonly ConcurrentBag<ProviderAccessGrant> _grants = new();

    public Task SaveAsync(ProviderAccessGrant grant, CancellationToken ct)
    {
        _grants.Add(grant);
        return Task.CompletedTask;
    }

    /// <summary>Test-only accessor — not part of the <see cref="IProviderAccessRepository"/> contract.</summary>
    public IReadOnlyList<ProviderAccessGrant> Grants => _grants.ToList();
}

public sealed class InMemoryTranslationSessionRepository : ITranslationSessionRepository
{
    private readonly ConcurrentDictionary<Guid, TranslationSession> _byId = new();

    public Task<TranslationSession?> FindByIdAsync(Guid sessionId, CancellationToken ct) =>
        Task.FromResult(_byId.GetValueOrDefault(sessionId));

    public Task<TranslationSession?> FindActiveByClientSessionIdAsync(Guid accountId, string clientSessionId, CancellationToken ct) =>
        Task.FromResult(_byId.Values.FirstOrDefault(s =>
            s.AccountId == accountId && s.ClientSessionId == clientSessionId && s.State == TranslationSessionState.Active));

    public Task SaveAsync(TranslationSession session, CancellationToken ct)
    {
        // Mirrors the partial unique index enforced at the database layer (Phase 6.9) —
        // at most one ACTIVE session per (AccountId, ClientSessionId), even though many
        // terminal historical rows may share the same clientSessionId.
        if (session.State == TranslationSessionState.Active && session.ClientSessionId is not null &&
            _byId.Values.Any(s => s.Id != session.Id && s.AccountId == session.AccountId &&
                                   s.ClientSessionId == session.ClientSessionId && s.State == TranslationSessionState.Active))
        {
            throw new ActiveSessionAlreadyExistsException(session.AccountId, session.ClientSessionId);
        }

        _byId[session.Id] = session;
        return Task.CompletedTask;
    }
}

public sealed class InMemoryBillingEventRepository : IBillingEventRepository
{
    private readonly ConcurrentDictionary<Guid, BillingEvent> _byId = new();

    public Task<BillingEvent?> FindByProviderEventIdAsync(string provider, string providerEventId, CancellationToken ct) =>
        Task.FromResult(_byId.Values.FirstOrDefault(e => e.Provider == provider && e.ProviderEventId == providerEventId));

    public Task SaveAsync(BillingEvent billingEvent, CancellationToken ct)
    {
        // Mirrors the UNIQUE(Provider, ProviderEventId) constraint enforced at the
        // database layer — the entire idempotency mechanism (Phase 6.6).
        var existing = _byId.Values.FirstOrDefault(e => e.Provider == billingEvent.Provider && e.ProviderEventId == billingEvent.ProviderEventId);
        if (existing is not null && existing.Id != billingEvent.Id)
            throw new Domain.DuplicateBillingEventException(billingEvent.Provider, billingEvent.ProviderEventId);

        _byId[billingEvent.Id] = billingEvent;
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
