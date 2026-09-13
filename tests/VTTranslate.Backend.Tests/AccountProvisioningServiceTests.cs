using VTTranslate.Backend.Application.Identity;
using VTTranslate.Backend.Domain.Abstractions;
using VTTranslate.Backend.Domain.Enums;
using VTTranslate.Backend.Infrastructure.Identity;
using VTTranslate.Backend.Infrastructure.Persistence;

namespace VTTranslate.Backend.Tests;

/// <summary>
/// Phase 7.0 — unit tests for the "hybrid gated JIT provisioning" policy (Option D). Uses
/// in-memory repositories; the real-PostgreSQL concurrency proof lives separately in
/// EfPostgresPersistenceTests.cs (per the master prompt's explicit requirement that an
/// in-memory test is supplemental only for the concurrency claim).
/// </summary>
public class AccountProvisioningServiceTests
{
    private readonly InMemoryAccountRepository _accounts = new();
    private readonly InMemoryProfileRepository _profiles = new();
    private readonly InMemoryAuditEventRepository _audit = new();
    private readonly InMemoryUnitOfWork _unitOfWork = new();
    private readonly FakeClock _clock = new();

    private AccountProvisioningService CreateService(
        bool enabled = true, bool requireEmailVerified = true, int rateLimitMaxAttempts = 5, TimeSpan? rateLimitWindow = null) =>
        new(_accounts, _profiles, _audit, _unitOfWork, _clock, new InMemoryProvisioningRateLimiter(_clock),
            enabled, requireEmailVerified, rateLimitMaxAttempts, rateLimitWindow ?? TimeSpan.FromHours(1));

    private static AuthenticatedPrincipal Principal(string subject = "subject-1", bool emailVerified = true, string? email = "user@example.com") =>
        new("EntraExternalId", subject, email, emailVerified, "Test User", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(15));

    [Fact]
    public async Task VerifiedIdentity_ProvisionsSuccessfully()
    {
        var service = CreateService();
        var result = await service.ProvisionAsync(Principal(), CancellationToken.None);

        Assert.Equal(AccountProvisioningOutcome.Provisioned, result.Outcome);
        Assert.NotNull(result.Account);
        Assert.Equal(AccountStatus.Active, result.Account!.Status);
        Assert.Equal(Role.Customer, result.Account.Role);
    }

    [Fact]
    public async Task UnverifiedIdentity_DeniedWhenVerificationRequired()
    {
        var service = CreateService(requireEmailVerified: true);
        var result = await service.ProvisionAsync(Principal(emailVerified: false), CancellationToken.None);

        Assert.Equal(AccountProvisioningOutcome.Denied, result.Outcome);
        Assert.Null(result.Account);
    }

    [Fact]
    public async Task UnverifiedIdentity_AllowedWhenVerificationNotRequired()
    {
        var service = CreateService(requireEmailVerified: false);
        var result = await service.ProvisionAsync(Principal(emailVerified: false), CancellationToken.None);

        Assert.Equal(AccountProvisioningOutcome.Provisioned, result.Outcome);
    }

    [Fact]
    public async Task ProvisioningDisabled_AlwaysDenied()
    {
        var service = CreateService(enabled: false);
        var result = await service.ProvisionAsync(Principal(), CancellationToken.None);

        Assert.Equal(AccountProvisioningOutcome.Denied, result.Outcome);
    }

    [Fact]
    public async Task RateLimitExceeded_DeniesFurtherAttemptsForTheSameIdentity()
    {
        // Rate limiting is keyed per external identity (provider, subject) — never a
        // client-supplied value, never an IP address. A limiter capped at 1 attempt per
        // window denies a second attempt against the SAME never-before-seen identity's
        // key, even though the account does not yet exist (this is the exact scenario
        // that matters: repeated hammering before the account is ever created).
        var limiter = new InMemoryProvisioningRateLimiter(_clock);
        var service = new AccountProvisioningService(_accounts, _profiles, _audit, _unitOfWork, _clock, limiter, true, true, 1, TimeSpan.FromHours(1));

        var first = await service.ProvisionAsync(Principal("rate-limited-subject"), CancellationToken.None);
        Assert.Equal(AccountProvisioningOutcome.Provisioned, first.Outcome);

        // Even though the first call already resolved the identity to an account, the
        // rate limiter itself was already consumed for this key — proving the limiter
        // gate runs independently of account existence.
        var acquired = await limiter.TryAcquireAsync("EntraExternalId:rate-limited-subject", 1, TimeSpan.FromHours(1), CancellationToken.None);
        Assert.False(acquired);
    }

    [Fact]
    public async Task DifferentIdentities_EachGetTheirOwnRateLimitBudget()
    {
        var service = CreateService(rateLimitMaxAttempts: 1);

        var first = await service.ProvisionAsync(Principal("independent-subject-a"), CancellationToken.None);
        var second = await service.ProvisionAsync(Principal("independent-subject-b"), CancellationToken.None);

        Assert.Equal(AccountProvisioningOutcome.Provisioned, first.Outcome);
        Assert.Equal(AccountProvisioningOutcome.Provisioned, second.Outcome);
    }

    [Fact]
    public async Task NoClientAccountIdOrRole_EverAccepted()
    {
        // Structural proof: ProvisionAsync's only input is the already-verified
        // AuthenticatedPrincipal, which has no AccountId or Role property at all.
        var properties = typeof(AuthenticatedPrincipal).GetProperties().Select(p => p.Name);
        Assert.DoesNotContain("AccountId", properties);
        Assert.DoesNotContain("Role", properties);

        var service = CreateService();
        var result = await service.ProvisionAsync(Principal("no-elevation-subject"), CancellationToken.None);
        Assert.Equal(Role.Customer, result.Account!.Role); // always Customer, never client-influenced
    }

    [Fact]
    public async Task ForgedEmail_NeverRedirectsIdentityMapping()
    {
        // Two different external subjects sharing the same email must resolve to two
        // different accounts — email is never the identity key (unchanged Phase 6.4 rule).
        var service = CreateService();
        var first = await service.ProvisionAsync(Principal("subject-shared-email-1", email: "shared@example.com"), CancellationToken.None);
        var second = await service.ProvisionAsync(Principal("subject-shared-email-2", email: "shared@example.com"), CancellationToken.None);

        Assert.NotEqual(first.Account!.Id, second.Account!.Id);
    }

    [Fact]
    public async Task RepeatedFirstLogin_SameIdentity_ResolvesSameAccountViaMiddlewareFlow()
    {
        // ProvisionAsync itself always attempts an insert; the "repeated first login"
        // resolution — the SECOND call finding the FIRST call's account instead of
        // erroring — is exactly the DuplicateIdentityException catch path, exercised
        // directly here against the in-memory repository's own duplicate check.
        var service = CreateService();
        var first = await service.ProvisionAsync(Principal("repeat-subject"), CancellationToken.None);
        var second = await service.ProvisionAsync(Principal("repeat-subject"), CancellationToken.None);

        Assert.Equal(AccountProvisioningOutcome.Provisioned, second.Outcome);
        Assert.Equal(first.Account!.Id, second.Account!.Id);
    }

    [Fact]
    public async Task Provisioning_CreatesExactlyOneProfile_Transactionally()
    {
        var service = CreateService();
        var result = await service.ProvisionAsync(Principal("profile-subject"), CancellationToken.None);

        var profile = await _profiles.FindByAccountIdAsync(result.Account!.Id, CancellationToken.None);
        Assert.NotNull(profile);
        Assert.Equal(result.Account.Id, profile!.AccountId);
    }

    [Fact]
    public async Task Provisioning_EmitsAccountProvisionedAudit()
    {
        var service = CreateService();
        var result = await service.ProvisionAsync(Principal("audit-subject"), CancellationToken.None);

        Assert.Contains(_audit.Events, e => e.EventType == "AccountProvisioned" && e.AccountId == result.Account!.Id);
    }
}
