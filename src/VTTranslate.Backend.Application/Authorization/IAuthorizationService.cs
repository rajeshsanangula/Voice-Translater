using VTTranslate.Backend.Domain.Entities;
using VTTranslate.Backend.Domain.Enums;

namespace VTTranslate.Backend.Application.Authorization;

/// <summary>
/// The backend-authoritative role check (Phase 6.2B §8, refined in Phase 6.4): "the
/// client must never grant itself administrative privileges." PHASE 6.4 CHANGE (see
/// docs/phase-6.4-entra-authentication.md §8): this service now operates on an
/// <see cref="Account"/> — the resolved, backend-owned record — rather than on a list of
/// claim-sourced roles. This is a deliberate correction to match the approved
/// architecture diagram (Entra → IIdentityProvider → AuthenticatedPrincipal → Account
/// Resolution → Account Status → AUTRAXIS Role → AuthorizationService): AUTRAXIS owns
/// exactly one role per account, stored on <see cref="Account.Role"/>, and that role is
/// never sourced from an Entra token claim.
///
/// Every check here REQUIRES the account to already be <see cref="Account.IsUsable"/> —
/// a suspended or soft-deleted account satisfies no role requirement at all, regardless
/// of what <see cref="Account.Role"/> says, as a defense-in-depth backstop even though
/// <c>IAccountResolutionService</c> (Phase 6.4) is expected to have already filtered
/// suspended accounts out before this service is ever called.
/// </summary>
public interface IAuthorizationService
{
    /// <summary>True iff the account is usable AND holds at least the given role, using the CUSTOMER &lt; ADMIN &lt; SUPER_ADMIN ordering.</summary>
    bool HasAtLeastRole(Account account, Role minimumRole);

    /// <summary>Throws <see cref="Domain.InsufficientRoleException"/> (insufficient role) or <see cref="Domain.AccountNotUsableException"/> (suspended/soft-deleted) if <see cref="HasAtLeastRole"/> would return false. Intended as a guard clause at the start of any protected application-service method.</summary>
    void RequireAtLeastRole(Account account, Role minimumRole);
}

public sealed class AuthorizationService : IAuthorizationService
{
    public bool HasAtLeastRole(Account account, Role minimumRole) =>
        account.IsUsable && account.Role >= minimumRole;

    public void RequireAtLeastRole(Account account, Role minimumRole)
    {
        if (!account.IsUsable)
            throw new Domain.AccountNotUsableException(account.Id, account.Status);

        if (account.Role < minimumRole)
            throw new Domain.InsufficientRoleException(minimumRole, account.Role);
    }
}
