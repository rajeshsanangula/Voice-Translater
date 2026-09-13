using VTTranslate.Backend.Domain.Entities;
using VTTranslate.Backend.Domain.Enums;

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

    /// <summary>
    /// Phase 6.7 — acquires an exclusive, transaction-scoped lock on this account's row,
    /// used ONLY to serialize concurrent device-registration attempts for the same
    /// account (see docs/phase-6.7-device-licensing-policy.md §6). Must be called inside
    /// an <see cref="IUnitOfWork"/> transaction; the lock is released automatically when
    /// that transaction commits or rolls back. Does not read or return any account data
    /// — callers that need the account itself still call <see cref="FindByIdAsync"/>
    /// separately. The in-memory implementation is a no-op (single-process test/dev
    /// only, no real concurrent-write race exists there to protect against).
    /// </summary>
    Task LockAccountForDeviceRegistrationAsync(Guid accountId, CancellationToken ct);

    /// <summary>
    /// Phase 6.9 — the same exclusive-row-lock mechanism as
    /// <see cref="LockAccountForDeviceRegistrationAsync"/>, under its own name because it
    /// protects a different invariant (concurrent translation-session admission never
    /// exceeding the authoritative usage limit — see
    /// docs/phase-6.9-usage-metering-and-session-accounting.md §21) at a different call
    /// site. Deliberately NOT unified with the Phase 6.7 method so that frozen Phase 6.7
    /// code and tests remain completely untouched by this phase.
    /// </summary>
    Task LockAccountForUsageAccountingAsync(Guid accountId, CancellationToken ct);
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
/// Phase 6.8 — append-only persistence for issued provider-access grants (audit/history
/// only; never used for any authorization decision — the authoritative decision is made
/// fresh on every request by <see cref="Entities.ProviderAccessGrant"/>'s own callers,
/// re-running the full authorization chain, never by reading a past grant).
/// </summary>
public interface IProviderAccessRepository
{
    Task SaveAsync(ProviderAccessGrant grant, CancellationToken ct);
}

/// <summary>
/// Phase 6.8 — the Infrastructure-implemented boundary that actually mints a short-lived,
/// provider-specific delegated credential. One implementation per <see cref="Provider"/>;
/// the Application layer depends only on this interface, never on any provider SDK or
/// HTTP detail. Master/long-lived provider credentials are read and used ENTIRELY inside
/// implementations of this interface (Infrastructure) — never exposed to Application,
/// Domain, or any API response.
/// </summary>
public interface IProviderCredentialIssuer
{
    Provider Provider { get; }
    bool SupportsCapability(ProviderCapability capability);

    /// <summary>
    /// Mints a short-lived delegated credential for the given capability. Returns null
    /// (fail closed) if the provider call fails or the master credential is not
    /// configured — never fabricates a credential, never falls back to a long-lived one.
    /// </summary>
    Task<IssuedProviderCredential?> IssueAsync(ProviderCapability capability, TimeSpan lifetime, CancellationToken ct);
}

/// <summary>
/// Phase 6.9 — persistence port for <see cref="TranslationSession"/>. Session lookups by
/// ID are always subsequently filtered by the caller's own AccountId (account isolation
/// is enforced by the Application layer, not by this port alone — mirrors every other
/// repository in this codebase).
/// </summary>
public interface ITranslationSessionRepository
{
    Task<TranslationSession?> FindByIdAsync(Guid sessionId, CancellationToken ct);

    /// <summary>Finds the currently-ACTIVE session (if any) for this (account, clientSessionId) pair — used for idempotent start/reconnect matching. Never returns a terminal session (there may be many historical terminal rows sharing the same clientSessionId once a prior session ended/expired — see the partial unique index, EF configuration).</summary>
    Task<TranslationSession?> FindActiveByClientSessionIdAsync(Guid accountId, string clientSessionId, CancellationToken ct);

    Task SaveAsync(TranslationSession session, CancellationToken ct);
}

/// <summary>Phase 6.9 — thrown when a concurrent start attempt races past the application-level idempotency check and collides with the database's own partial unique index (AccountId, ClientSessionId) WHERE State = Active. Translated from the database's own constraint violation at the Infrastructure boundary, exactly like the existing DuplicateBillingEventException/ConcurrentUpdateException pattern.</summary>
public sealed class ActiveSessionAlreadyExistsException(Guid accountId, string clientSessionId) : Exception($"Account '{accountId}' already has an active session for clientSessionId '{clientSessionId}'.")
{
    public Guid AccountId { get; } = accountId;
    public string ClientSessionId { get; } = clientSessionId;
}

/// <summary>The ONLY shape a provider-access caller ever sees — a genuinely short-lived, provider-scoped credential, never the backend's own master key.</summary>
public sealed record IssuedProviderCredential(string AccessToken, string Region, DateTimeOffset ExpiresAt);

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
