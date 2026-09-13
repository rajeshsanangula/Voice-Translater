using VTTranslate.Backend.Domain.Abstractions;
using VTTranslate.Backend.Domain.Entities;

namespace VTTranslate.Backend.Application.Identity;

/// <summary>
/// Implements the "AUTRAXIS Account Resolution" step of the approved architecture:
/// <c>AuthenticatedPrincipal → AUTRAXIS Account lookup → Account status check</c>
/// (docs/phase-6.4-entra-authentication.md §6). This is the ONLY place in the backend
/// that turns a verified external identity into an AUTRAXIS-authoritative account
/// context — every protected operation must go through this, never resolve an account
/// by any other means (e.g. never by email, per the Phase 6.4 instruction).
/// </summary>
public interface IAccountResolutionService
{
    Task<AccountResolutionResult> ResolveAsync(AuthenticatedPrincipal principal, CancellationToken ct);
}

public enum AccountResolutionOutcome
{
    /// <summary>Stable external identity resolved to an existing, usable AUTRAXIS account.</summary>
    Resolved,

    /// <summary>Valid, verified external identity — but no AUTRAXIS account exists for it. NOT auto-provisioned (Phase 6.4 instruction: "do not silently create accounts unless the existing approved architecture explicitly requires automatic provisioning" — it does not). This is a controlled, explicit result, not a fabricated account.</summary>
    AccountNotFound,

    /// <summary>The mapped account exists but is not usable (<see cref="Account.IsUsable"/> is false — suspended or soft-deleted).</summary>
    AccountSuspended,
}

/// <summary><see cref="Account"/> is non-null only when <see cref="Outcome"/> is <see cref="AccountResolutionOutcome.Resolved"/> or <see cref="AccountResolutionOutcome.AccountSuspended"/> (present for audit/messaging even when denied); null for <see cref="AccountResolutionOutcome.AccountNotFound"/>.</summary>
public sealed record AccountResolutionResult(AccountResolutionOutcome Outcome, Account? Account);

public sealed class AccountResolutionService(IAccountRepository accounts) : IAccountResolutionService
{
    public async Task<AccountResolutionResult> ResolveAsync(AuthenticatedPrincipal principal, CancellationToken ct)
    {
        // Stable external subject identifier ONLY — never email (Phase 6.4 instruction:
        // "Do not use email as the canonical identity key"). Email can change at the
        // identity provider; the subject identifier is defined by Entra to be stable
        // for the lifetime of the account.
        var account = await accounts.FindByExternalIdentityAsync(principal.Provider, principal.ExternalSubjectId, ct);

        if (account is null)
            return new AccountResolutionResult(AccountResolutionOutcome.AccountNotFound, null);

        if (!account.IsUsable)
            return new AccountResolutionResult(AccountResolutionOutcome.AccountSuspended, account);

        return new AccountResolutionResult(AccountResolutionOutcome.Resolved, account);
    }
}
