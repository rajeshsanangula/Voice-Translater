using VTTranslate.Backend.Domain.Entities;

namespace VTTranslate.Backend.Domain.Abstractions;

/// <summary>
/// Persistence ports. Deliberately vendor-neutral (no Entity Framework, no SQL, no
/// specific database type appears anywhere in these signatures) — see
/// docs/phase-6.3-backend-foundation.md "Persistence strategy" for why no database
/// vendor is committed to in this phase. The only implementations in this phase are
/// in-memory (<c>VTTranslate.Backend.Infrastructure.Persistence</c>), used for
/// automated tests and local development only — never a production data store.
/// </summary>
public interface IAccountRepository
{
    Task<Account?> FindByIdAsync(Guid accountId, CancellationToken ct);
    Task<Account?> FindByExternalIdentityAsync(string provider, string externalSubjectId, CancellationToken ct);
    Task SaveAsync(Account account, CancellationToken ct);
}

public interface IDeviceRepository
{
    Task<Device?> FindByIdAsync(Guid deviceId, CancellationToken ct);
    Task<IReadOnlyList<Device>> ListByAccountAsync(Guid accountId, CancellationToken ct);
    Task SaveAsync(Device device, CancellationToken ct);
}

/// <summary>
/// Phase 6.6: <see cref="FindByAccountAsync"/>'s meaning changed from "the account's one
/// subscription row" (Phase 6.3-6.5, when a plain unique index on AccountId enforced
/// exactly one row ever) to "the account's CURRENTLY-EFFECTIVE subscription" (a
/// subscription whose Status is one of Trial/Active/PastDue/GracePeriod) — an account can
/// now have many historical rows (see docs/phase-6.6-billing-subscription.md, "Subscription
/// Cardinality"), but a partial unique database index still guarantees at most one LIVE
/// row per account, so this method's "at most one result" contract is unchanged even
/// though the underlying table is no longer 1:1.
/// </summary>
public interface ISubscriptionRepository
{
    Task<Subscription?> FindByAccountAsync(Guid accountId, CancellationToken ct);

    /// <summary>Phase 6.6: full historical list of every subscription row (any status) ever created for this account — for support/audit use. Never used by EntitlementService or any authorization decision.</summary>
    Task<IReadOnlyList<Subscription>> FindHistoryByAccountAsync(Guid accountId, CancellationToken ct);

    Task<Subscription?> FindByIdAsync(Guid subscriptionId, CancellationToken ct);

    /// <summary>Phase 6.6: correlates an inbound billing webhook event to the AUTRAXIS subscription it concerns, by the opaque provider-issued handle. Returns null if no subscription is linked to that handle (an "unknown correlation" — never auto-creates one).</summary>
    Task<Subscription?> FindByBillingProviderSubscriptionIdAsync(string billingProviderSubscriptionId, CancellationToken ct);

    Task SaveAsync(Subscription subscription, CancellationToken ct);
}

/// <summary>
/// Phase 6.6 — the idempotency ledger port for received billing webhooks. See
/// <see cref="Entities.BillingEvent"/>'s own doc comment.
/// </summary>
public interface IBillingEventRepository
{
    Task<BillingEvent?> FindByProviderEventIdAsync(string provider, string providerEventId, CancellationToken ct);
    Task SaveAsync(BillingEvent billingEvent, CancellationToken ct);
}

public interface IPlanRepository
{
    Task<Plan?> FindByIdAsync(Guid planId, CancellationToken ct);
    Task<IReadOnlyList<Entitlement>> GetEntitlementsAsync(Guid planId, CancellationToken ct);
}

public interface IUsageRecordRepository
{
    Task AddAsync(UsageRecord record, CancellationToken ct);
    Task<IReadOnlyList<UsageRecord>> ListByAccountAndPeriodAsync(Guid accountId, string periodBucket, CancellationToken ct);
}

public interface IAuditEventRepository
{
    Task AddAsync(AuditEvent auditEvent, CancellationToken ct);
}

/// <summary>
/// Phase 6.5 addition — Profile persistence was implied by the domain model since Phase
/// 6.3 but had no repository port yet. One profile per account (1:1), so lookup is by
/// account id, mirroring <see cref="ISubscriptionRepository"/>'s shape.
/// </summary>
public interface IProfileRepository
{
    Task<Profile?> FindByAccountIdAsync(Guid accountId, CancellationToken ct);
    Task SaveAsync(Profile profile, CancellationToken ct);
}

/// <summary>
/// Phase 6.6 — a minimal transaction boundary abstraction, used ONLY by
/// IBillingWebhookProcessor to guarantee that a BillingEvent status update and its
/// corresponding Subscription write commit together, atomically (see
/// docs/phase-6.6-billing-subscription.md §7 for the exact lifecycle this protects). Not
/// a general-purpose unit-of-work — every other repository call in this codebase remains
/// a single, self-contained SaveChangesAsync (Phase 6.5's existing, deliberate scope
/// decision), because no other operation in this codebase spans multiple repository
/// writes that must succeed-or-fail together.
/// </summary>
public interface IUnitOfWork
{
    Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken ct);
}

/// <summary>
/// Phase 6.5 addition — authentication-session persistence (sign-out-this-device /
/// sign-out-everywhere, Phase 6.1 §10). Distinct from device authorization and from any
/// real-time translation session (see <see cref="Entities.Session"/>'s own doc comment).
/// </summary>
public interface ISessionRepository
{
    Task<Session?> FindByIdAsync(Guid sessionId, CancellationToken ct);
    Task<IReadOnlyList<Session>> ListByAccountAsync(Guid accountId, CancellationToken ct);
    Task SaveAsync(Session session, CancellationToken ct);
}
