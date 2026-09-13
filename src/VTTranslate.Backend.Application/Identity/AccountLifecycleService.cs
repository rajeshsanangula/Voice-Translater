using VTTranslate.Backend.Domain.Abstractions;
using VTTranslate.Backend.Domain.Entities;
using VTTranslate.Backend.Domain.Enums;

namespace VTTranslate.Backend.Application.Identity;

/// <summary>
/// Phase 7.0 — implements the approved transition matrix
/// (docs/phase-7.0-production-identity-and-account-lifecycle.md §9):
///
/// Pending  -> Active, Disabled, Closed
/// Active   -> Suspended, Disabled, Closed
/// Suspended -> Active, Disabled, Closed
/// Disabled -> Active, Closed
/// Closed   -> (none — terminal)
///
/// A transition to an account's OWN current state is treated as an idempotent no-op
/// success (no audit event emitted a second time) rather than an invalid transition —
/// this matches the idempotency discipline already established for
/// TranslationSession end (Phase 6.9) and BillingEvent processing (Phase 6.6).
/// </summary>
public sealed class AccountLifecycleService(
    IAccountRepository accounts,
    IAuditEventRepository audit,
    IClock clock) : IAccountLifecycleService
{
    public Task<AccountLifecycleResult> ActivateAsync(Guid accountId, CancellationToken ct) =>
        TransitionAsync(accountId, AccountStatus.Active, "AccountActivated", ct);

    public Task<AccountLifecycleResult> SuspendAsync(Guid accountId, CancellationToken ct) =>
        TransitionAsync(accountId, AccountStatus.Suspended, "AccountSuspended", ct);

    public Task<AccountLifecycleResult> DisableAsync(Guid accountId, CancellationToken ct) =>
        TransitionAsync(accountId, AccountStatus.Disabled, "AccountDisabled", ct);

    public Task<AccountLifecycleResult> CloseAsync(Guid accountId, CancellationToken ct) =>
        TransitionAsync(accountId, AccountStatus.Closed, "AccountClosed", ct);

    private async Task<AccountLifecycleResult> TransitionAsync(Guid accountId, AccountStatus target, string auditEventType, CancellationToken ct)
    {
        var account = await accounts.FindByIdAsync(accountId, ct);
        if (account is null)
            return new AccountLifecycleResult(AccountLifecycleOutcome.NotFound, null);

        if (account.Status == target)
            return new AccountLifecycleResult(AccountLifecycleOutcome.Success, account); // idempotent no-op

        if (!IsLegalTransition(account.Status, target))
            return new AccountLifecycleResult(AccountLifecycleOutcome.InvalidTransition, account);

        account.Status = target;
        await accounts.SaveAsync(account, ct);

        await audit.AddAsync(new AuditEvent
        {
            Id = Guid.NewGuid(),
            AccountId = account.Id,
            EventType = auditEventType,
            OccurredAt = clock.UtcNow,
        }, ct);

        return new AccountLifecycleResult(AccountLifecycleOutcome.Success, account);
    }

    private static bool IsLegalTransition(AccountStatus from, AccountStatus to) => from switch
    {
        AccountStatus.Closed => false, // terminal — no transition out, ever
        AccountStatus.Pending => to is AccountStatus.Active or AccountStatus.Disabled or AccountStatus.Closed,
        AccountStatus.Active => to is AccountStatus.Suspended or AccountStatus.Disabled or AccountStatus.Closed,
        AccountStatus.Suspended => to is AccountStatus.Active or AccountStatus.Disabled or AccountStatus.Closed,
        AccountStatus.Disabled => to is AccountStatus.Active or AccountStatus.Closed,
        _ => false,
    };
}
