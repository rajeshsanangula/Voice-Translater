using VTTranslate.Backend.Domain.Entities;

namespace VTTranslate.Backend.Application.Identity;

/// <summary>
/// Phase 7.0 — explicit account-lifecycle transition operations (see
/// docs/phase-7.0-production-identity-and-account-lifecycle.md §8/§9). Deliberately
/// four named, single-purpose operations rather than one generic "set status" method —
/// the architecture explicitly forbids a dangerous arbitrary-status-setter surface.
/// Every operation is server-authoritative: <see cref="Guid"/> account identifiers are
/// the only input, never a client-supplied target status, and every legal/illegal
/// transition is validated here, not left to callers.
///
/// No API endpoint calls this in Phase 7.0 — it is a domain/application capability only
/// (the architecture explicitly forbids building a broad admin API in this phase). It
/// exists so a future, narrowly-scoped admin surface has a safe, already-reviewed
/// operation to call, and so the transition rules themselves are exercised by tests now.
/// </summary>
public interface IAccountLifecycleService
{
    Task<AccountLifecycleResult> ActivateAsync(Guid accountId, CancellationToken ct);
    Task<AccountLifecycleResult> SuspendAsync(Guid accountId, CancellationToken ct);
    Task<AccountLifecycleResult> DisableAsync(Guid accountId, CancellationToken ct);

    /// <summary>Terminal. No transition out of <see cref="Domain.Enums.AccountStatus.Closed"/> is ever valid — see the entity's own doc comment. Never deletes UsageRecord/BillingEvent/AuditEvent history (§20 of the architecture document); this operation only flips <see cref="Account.Status"/> and audits the transition.</summary>
    Task<AccountLifecycleResult> CloseAsync(Guid accountId, CancellationToken ct);
}

public enum AccountLifecycleOutcome
{
    /// <summary>The transition was applied (or was already in the target state — idempotent repeat).</summary>
    Success,

    /// <summary>The requested transition is not legal from the account's current state (e.g. any transition out of Closed).</summary>
    InvalidTransition,

    NotFound,
}

public sealed record AccountLifecycleResult(AccountLifecycleOutcome Outcome, Account? Account);
