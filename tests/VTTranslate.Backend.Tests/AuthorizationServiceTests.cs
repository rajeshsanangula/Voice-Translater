using VTTranslate.Backend.Application.Authorization;
using VTTranslate.Backend.Domain;
using VTTranslate.Backend.Domain.Entities;
using VTTranslate.Backend.Domain.Enums;

namespace VTTranslate.Backend.Tests;

/// <summary>
/// PHASE 6.4 CHANGE: <see cref="AuthorizationService"/> now operates on a resolved
/// <see cref="Account"/> (the backend-owned, single-role record), not a claim-sourced
/// role list — see docs/phase-6.4-entra-authentication.md §8 for why. These tests
/// replace the Phase 6.3 versions that constructed an <c>AuthenticatedPrincipal</c>
/// directly with a role list; that shape no longer exists (role is never carried on
/// <c>AuthenticatedPrincipal</c> at all now).
/// </summary>
public class AuthorizationServiceTests
{
    private readonly AuthorizationService _service = new();

    private static Account MakeAccount(Role role, AccountStatus status = AccountStatus.Active) => new()
    {
        Id = Guid.NewGuid(),
        Email = "user@example.com",
        EmailVerified = true,
        ExternalIdentityProvider = "EntraExternalId",
        ExternalSubjectId = "ext-1",
        Role = role,
        Status = status,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public void Customer_DoesNotHaveAdminRole()
    {
        var account = MakeAccount(Role.Customer);
        Assert.False(_service.HasAtLeastRole(account, Role.Admin));
    }

    [Fact]
    public void Admin_HasAtLeastCustomerRole()
    {
        var account = MakeAccount(Role.Admin);
        Assert.True(_service.HasAtLeastRole(account, Role.Customer));
    }

    [Fact]
    public void Admin_DoesNotHaveSuperAdminRole()
    {
        var account = MakeAccount(Role.Admin);
        Assert.False(_service.HasAtLeastRole(account, Role.SuperAdmin));
    }

    [Fact]
    public void SuperAdmin_HasEveryLowerRole()
    {
        var account = MakeAccount(Role.SuperAdmin);
        Assert.True(_service.HasAtLeastRole(account, Role.Customer));
        Assert.True(_service.HasAtLeastRole(account, Role.Admin));
        Assert.True(_service.HasAtLeastRole(account, Role.SuperAdmin));
    }

    [Fact]
    public void RequireAtLeastRole_Throws_WhenInsufficient()
    {
        var account = MakeAccount(Role.Customer);
        var ex = Assert.Throws<InsufficientRoleException>(() => _service.RequireAtLeastRole(account, Role.SuperAdmin));
        Assert.Equal(Role.SuperAdmin, ex.Required);
        Assert.Equal(Role.Customer, ex.Actual);
    }

    [Fact]
    public void RequireAtLeastRole_DoesNotThrow_WhenSufficient()
    {
        var account = MakeAccount(Role.Admin);
        var exception = Record.Exception(() => _service.RequireAtLeastRole(account, Role.Admin));
        Assert.Null(exception);
    }

    // ---- PHASE 6.4: suspended/unusable accounts fail closed regardless of Role ----

    [Fact]
    public void SuspendedAccount_SuperAdminRole_StillDeniedEvenTheLowestRequirement()
    {
        // The single most important new invariant this phase adds: a suspended
        // account satisfies NO role requirement, no matter how high its stored Role
        // value is — Account.IsUsable is checked FIRST, unconditionally.
        var account = MakeAccount(Role.SuperAdmin, AccountStatus.Suspended);
        Assert.False(_service.HasAtLeastRole(account, Role.Customer));
    }

    [Fact]
    public void SuspendedAccount_RequireAtLeastRole_ThrowsAccountNotUsable_NotInsufficientRole()
    {
        var account = MakeAccount(Role.SuperAdmin, AccountStatus.Suspended);
        var ex = Assert.Throws<AccountNotUsableException>(() => _service.RequireAtLeastRole(account, Role.Customer));
        Assert.Equal(account.Id, ex.AccountId);
        Assert.Equal(AccountStatus.Suspended, ex.Status);
    }

    [Fact]
    public void SoftDeletedAccount_IsNotUsable_EvenIfStatusStillActive()
    {
        var account = MakeAccount(Role.Admin);
        account.DeletionRequestedAt = DateTimeOffset.UtcNow;
        Assert.False(_service.HasAtLeastRole(account, Role.Customer));
    }

    [Fact]
    public void ActiveAccount_IsUsable()
    {
        var account = MakeAccount(Role.Customer);
        Assert.True(account.IsUsable);
    }

    // ---- Documented trust-boundary note carried forward from Phase 6.3's review ----

    [Fact]
    public void OutOfRangeRoleValue_IsTrustedNumerically_ByDesign()
    {
        // DOCUMENTED TRUST BOUNDARY (originally Phase 6.3 review gate §6, now closed
        // structurally by Phase 6.4's design rather than by validation logic here):
        // AuthorizationService performs a pure numeric ">=" comparison against
        // Account.Role. This test remains meaningful because Account.Role is a plain
        // enum field that COULD in principle hold an out-of-range value if ever set
        // incorrectly (e.g. by a future admin tool or data migration) — there is no
        // parsing of an external claim into Role anywhere in this codebase (Phase 6.4
        // eliminated that attack surface entirely; see EntraIdentityProvider, which
        // never reads a role claim at all), so this documents a data-integrity
        // expectation on writers of Account.Role, not a claim-parsing concern.
        var account = MakeAccount((Role)99);
        Assert.True(_service.HasAtLeastRole(account, Role.SuperAdmin));
    }
}
