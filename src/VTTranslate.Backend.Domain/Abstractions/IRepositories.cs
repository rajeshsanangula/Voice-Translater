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

public interface ISubscriptionRepository
{
    Task<Subscription?> FindByAccountAsync(Guid accountId, CancellationToken ct);
    Task SaveAsync(Subscription subscription, CancellationToken ct);
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
