using VTTranslate.Backend.Domain.Abstractions;
using VTTranslate.Backend.Domain.Entities;
using VTTranslate.Backend.Domain.Enums;

namespace VTTranslate.Backend.Application.Identity;

/// <summary>
/// Phase 7.0 — the concrete "hybrid gated JIT provisioning" implementation. See the
/// interface's own doc comment for the invocation contract and
/// docs/phase-7.0-production-identity-and-account-lifecycle.md §7/§14/§24 for the full
/// design rationale (rate-limit key choice, transactional atomicity, and the
/// concurrency-race resolution behavior below).
///
/// Gate order (all server-side, none client-influenced):
/// 1. Provisioning enabled at all (configuration).
/// 2. Email verification required and satisfied (<see cref="AuthenticatedPrincipal.EmailVerified"/>
///    — sourced from Entra's own verified claim via <c>EntraIdentityProvider</c>, never
///    from claim presence/absence of email itself).
/// 3. Server-controlled rate limit, keyed on the external identity's own
///    (provider, subject) pair — never an IP address (data-minimization; see §19 of the
///    architecture document) and never a client-supplied value.
///
/// Concurrency: Account + Profile + the "AccountProvisioned" AuditEvent are written
/// inside one <see cref="IUnitOfWork"/> transaction. If a concurrent request wins the
/// race first, this transaction's own INSERT collides with the database's
/// (ExternalIdentityProvider, ExternalSubjectId) unique constraint, which
/// <see cref="IAccountRepository"/>'s implementation translates into
/// <see cref="Domain.DuplicateIdentityException"/> — propagating that exception out of
/// the transaction delegate causes <see cref="IUnitOfWork.ExecuteInTransactionAsync{T}"/>
/// to roll back cleanly (the exact same mechanism already proven by every other
/// transactional write in this codebase, e.g. Phase 6.6/6.8/6.9's own rollback tests).
/// This service then performs a fresh, transaction-free read for the now-existing
/// account and returns it as a normal <see cref="AccountProvisioningOutcome.Provisioned"/>
/// result — the losing request never surfaces an error to its caller.
/// </summary>
public sealed class AccountProvisioningService(
    IAccountRepository accounts,
    IProfileRepository profiles,
    IAuditEventRepository audit,
    IUnitOfWork unitOfWork,
    IClock clock,
    IProvisioningRateLimiter rateLimiter,
    bool enabled,
    bool requireEmailVerified,
    int rateLimitMaxAttempts,
    TimeSpan rateLimitWindow) : IAccountProvisioningService
{
    public async Task<AccountProvisioningResult> ProvisionAsync(AuthenticatedPrincipal principal, CancellationToken ct)
    {
        if (!enabled)
            return new AccountProvisioningResult(AccountProvisioningOutcome.Denied, null);

        if (requireEmailVerified && !principal.EmailVerified)
            return new AccountProvisioningResult(AccountProvisioningOutcome.Denied, null);

        var rateLimitKey = $"{principal.Provider}:{principal.ExternalSubjectId}";
        if (!await rateLimiter.TryAcquireAsync(rateLimitKey, rateLimitMaxAttempts, rateLimitWindow, ct))
            return new AccountProvisioningResult(AccountProvisioningOutcome.Denied, null);

        var account = new Account
        {
            Id = Guid.NewGuid(),
            Email = principal.Email ?? string.Empty,
            EmailVerified = principal.EmailVerified,
            ExternalIdentityProvider = principal.Provider,
            ExternalSubjectId = principal.ExternalSubjectId,
            Role = Role.Customer,
            Status = AccountStatus.Active,
            CreatedAt = clock.UtcNow,
        };

        try
        {
            await unitOfWork.ExecuteInTransactionAsync<object?>(async innerCt =>
            {
                await accounts.SaveAsync(account, innerCt);

                var profile = new Profile
                {
                    AccountId = account.Id,
                    CreatedAt = clock.UtcNow,
                    UpdatedAt = clock.UtcNow,
                };
                await profiles.SaveAsync(profile, innerCt);

                await audit.AddAsync(new AuditEvent
                {
                    Id = Guid.NewGuid(),
                    AccountId = account.Id,
                    EventType = "AccountProvisioned",
                    OccurredAt = clock.UtcNow,
                }, innerCt);

                return null;
            }, ct);

            return new AccountProvisioningResult(AccountProvisioningOutcome.Provisioned, account);
        }
        catch (Domain.DuplicateIdentityException)
        {
            // Lost the race — the transaction above rolled back cleanly (no partial
            // Account/Profile/audit row from THIS attempt persists). Resolve the winner
            // fresh rather than surfacing an error to a caller that did nothing wrong.
            var winner = await accounts.FindByExternalIdentityAsync(principal.Provider, principal.ExternalSubjectId, ct);
            return new AccountProvisioningResult(AccountProvisioningOutcome.Provisioned, winner);
        }
    }
}
