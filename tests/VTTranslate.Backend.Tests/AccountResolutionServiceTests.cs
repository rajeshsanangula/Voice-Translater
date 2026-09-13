using VTTranslate.Backend.Application.Identity;
using VTTranslate.Backend.Domain.Abstractions;
using VTTranslate.Backend.Domain.Entities;
using VTTranslate.Backend.Domain.Enums;
using VTTranslate.Backend.Infrastructure.Persistence;

namespace VTTranslate.Backend.Tests;

public class AccountResolutionServiceTests
{
    private readonly InMemoryAccountRepository _accounts = new();
    private readonly AccountResolutionService _service;

    public AccountResolutionServiceTests()
    {
        _service = new AccountResolutionService(_accounts);
    }

    private static AuthenticatedPrincipal Principal(string subject = "subject-1", string provider = "EntraExternalId", string? email = "user@example.com") =>
        new(provider, subject, email, true, "Test User", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(15));

    private async Task<Account> SeedAccountAsync(string provider, string subject, AccountStatus status = AccountStatus.Active, string email = "user@example.com")
    {
        var account = new Account
        {
            Id = Guid.NewGuid(),
            Email = email,
            EmailVerified = true,
            ExternalIdentityProvider = provider,
            ExternalSubjectId = subject,
            Role = Role.Customer,
            Status = status,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await _accounts.SaveAsync(account, CancellationToken.None);
        return account;
    }

    [Fact]
    public async Task ExistingActiveAccount_Resolves()
    {
        var account = await SeedAccountAsync("EntraExternalId", "subject-1");

        var result = await _service.ResolveAsync(Principal("subject-1"), CancellationToken.None);

        Assert.Equal(AccountResolutionOutcome.Resolved, result.Outcome);
        Assert.Equal(account.Id, result.Account!.Id);
    }

    [Fact]
    public async Task UnknownIdentity_ReturnsAccountNotFound_DoesNotAutoProvision()
    {
        // No account seeded at all.
        var result = await _service.ResolveAsync(Principal("never-seen-subject"), CancellationToken.None);

        Assert.Equal(AccountResolutionOutcome.AccountNotFound, result.Outcome);
        Assert.Null(result.Account);

        // Proves no side effect occurred — no account was silently created.
        Assert.Null(await _accounts.FindByExternalIdentityAsync("EntraExternalId", "never-seen-subject", CancellationToken.None));
    }

    [Fact]
    public async Task SuspendedAccount_ReturnsAccountSuspended_NotResolved()
    {
        await SeedAccountAsync("EntraExternalId", "subject-1", AccountStatus.Suspended);

        var result = await _service.ResolveAsync(Principal("subject-1"), CancellationToken.None);

        Assert.Equal(AccountResolutionOutcome.AccountSuspended, result.Outcome);
        Assert.NotNull(result.Account); // present for audit/messaging even though denied
    }

    [Fact]
    public async Task SoftDeletedAccount_ReturnsAccountSuspended_TreatedAsUnusable()
    {
        var account = await SeedAccountAsync("EntraExternalId", "subject-1");
        account.DeletionRequestedAt = DateTimeOffset.UtcNow;
        await _accounts.SaveAsync(account, CancellationToken.None);

        var result = await _service.ResolveAsync(Principal("subject-1"), CancellationToken.None);

        Assert.Equal(AccountResolutionOutcome.AccountSuspended, result.Outcome);
    }

    [Fact]
    public async Task LookupIsScopedByProvider_SameSubjectDifferentProvider_DoesNotMatch()
    {
        // "Ambiguous/invalid identity mapping" (Phase 6.4 instruction E, case 5): the
        // SAME subject string under a DIFFERENT provider must never resolve to an
        // account registered under a different provider — provider+subject together
        // form the key, not subject alone.
        await SeedAccountAsync("SomeOtherProvider", "subject-1");

        var result = await _service.ResolveAsync(Principal("subject-1", provider: "EntraExternalId"), CancellationToken.None);

        Assert.Equal(AccountResolutionOutcome.AccountNotFound, result.Outcome);
    }

    [Fact]
    public async Task EmailIsNeverUsedAsTheLookupKey()
    {
        // Two different external subjects sharing the same email (e.g. a user who
        // changed their Entra account but kept the same email address) must resolve
        // to two DIFFERENT accounts — proving email is never the identity key.
        var accountA = await SeedAccountAsync("EntraExternalId", "subject-A", email: "shared@example.com");
        var accountB = await SeedAccountAsync("EntraExternalId", "subject-B", email: "shared@example.com");

        var resultA = await _service.ResolveAsync(Principal("subject-A", email: "shared@example.com"), CancellationToken.None);
        var resultB = await _service.ResolveAsync(Principal("subject-B", email: "shared@example.com"), CancellationToken.None);

        Assert.Equal(accountA.Id, resultA.Account!.Id);
        Assert.Equal(accountB.Id, resultB.Account!.Id);
        Assert.NotEqual(resultA.Account.Id, resultB.Account.Id);
    }

    [Fact]
    public async Task PrincipalWithNoEmailAtAll_StillResolvesByExternalSubjectId()
    {
        // Confirms email is not just "not the key" but genuinely optional for resolution.
        await SeedAccountAsync("EntraExternalId", "subject-1");

        var result = await _service.ResolveAsync(Principal("subject-1", email: null), CancellationToken.None);

        Assert.Equal(AccountResolutionOutcome.Resolved, result.Outcome);
    }
}
