using VTTranslate.Backend.Infrastructure.Identity;

namespace VTTranslate.Backend.Tests;

public class EntraIdentityProviderTests
{
    private readonly EntraIdentityProvider _provider = new();

    private static Dictionary<string, string> BaseClaims(string sub = "subject-123") => new()
    {
        [EntraIdentityProvider.SubjectClaimType] = sub,
        [EntraIdentityProvider.IssuedAtClaimType] = "1700000000",
        [EntraIdentityProvider.ExpiryClaimType] = "1700003600",
    };

    [Fact]
    public void ValidClaims_ProducesPrincipal_WithProviderName()
    {
        var claims = BaseClaims();
        claims[EntraIdentityProvider.EmailClaimType] = "user@example.com";
        claims[EntraIdentityProvider.EmailVerifiedClaimType] = "true";
        claims[EntraIdentityProvider.NameClaimType] = "Test User";

        var principal = _provider.TryCreatePrincipal(claims);

        Assert.NotNull(principal);
        Assert.Equal("EntraExternalId", principal!.Provider);
        Assert.Equal("subject-123", principal.ExternalSubjectId);
        Assert.Equal("user@example.com", principal.Email);
        Assert.True(principal.EmailVerified);
        Assert.Equal("Test User", principal.DisplayName);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000000), principal.IssuedAt);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700003600), principal.ExpiresAt);
    }

    [Fact]
    public void MissingSubjectAndObjectId_ReturnsNull_FailClosed()
    {
        var claims = BaseClaims();
        claims.Remove(EntraIdentityProvider.SubjectClaimType);

        Assert.Null(_provider.TryCreatePrincipal(claims));
    }

    [Fact]
    public void EmptySubject_TreatedAsMissing_ReturnsNull()
    {
        var claims = BaseClaims(sub: "   ");
        Assert.Null(_provider.TryCreatePrincipal(claims));
    }

    [Fact]
    public void FallsBackToObjectId_WhenSubjectAbsent()
    {
        var claims = BaseClaims();
        claims.Remove(EntraIdentityProvider.SubjectClaimType);
        claims[EntraIdentityProvider.ObjectIdClaimType] = "oid-456";

        var principal = _provider.TryCreatePrincipal(claims);

        Assert.NotNull(principal);
        Assert.Equal("oid-456", principal!.ExternalSubjectId);
    }

    [Fact]
    public void MissingIssuedAt_ReturnsNull_FailClosed()
    {
        var claims = BaseClaims();
        claims.Remove(EntraIdentityProvider.IssuedAtClaimType);
        Assert.Null(_provider.TryCreatePrincipal(claims));
    }

    [Fact]
    public void MissingExpiry_ReturnsNull_FailClosed()
    {
        var claims = BaseClaims();
        claims.Remove(EntraIdentityProvider.ExpiryClaimType);
        Assert.Null(_provider.TryCreatePrincipal(claims));
    }

    [Fact]
    public void MalformedIssuedAt_ReturnsNull_FailClosed()
    {
        var claims = BaseClaims();
        claims[EntraIdentityProvider.IssuedAtClaimType] = "not-a-number";
        Assert.Null(_provider.TryCreatePrincipal(claims));
    }

    [Fact]
    public void EmailVerifiedClaim_Absent_DefaultsFalse_NeverTrueByDefault()
    {
        var claims = BaseClaims();
        claims[EntraIdentityProvider.EmailClaimType] = "user@example.com";
        // No email_verified claim at all.

        var principal = _provider.TryCreatePrincipal(claims);

        Assert.NotNull(principal);
        Assert.False(principal!.EmailVerified);
    }

    [Fact]
    public void EmailVerifiedClaim_Malformed_DefaultsFalse_NeverTrueByDefault()
    {
        var claims = BaseClaims();
        claims[EntraIdentityProvider.EmailVerifiedClaimType] = "not-a-bool";

        var principal = _provider.TryCreatePrincipal(claims);

        Assert.NotNull(principal);
        Assert.False(principal!.EmailVerified);
    }

    [Fact]
    public void EmailFallsBackToPreferredUsername_WhenEmailClaimAbsent()
    {
        var claims = BaseClaims();
        claims[EntraIdentityProvider.PreferredUsernameClaimType] = "user@fallback.example";

        var principal = _provider.TryCreatePrincipal(claims);

        Assert.NotNull(principal);
        Assert.Equal("user@fallback.example", principal!.Email);
    }

    [Fact]
    public void NoEmailClaimsAtAll_EmailIsNull_NotFabricated()
    {
        var claims = BaseClaims();
        var principal = _provider.TryCreatePrincipal(claims);

        Assert.NotNull(principal);
        Assert.Null(principal!.Email);
    }

    [Fact]
    public void NeverProducesARoleOrAnyAuthorizationInformation()
    {
        // Structural proof of the core Phase 6.4 design decision: AuthenticatedPrincipal
        // has no Role/Roles property at all — verified here by reflection so this test
        // breaks loudly if a future change ever reintroduces one.
        var claims = BaseClaims();
        var principal = _provider.TryCreatePrincipal(claims);
        Assert.NotNull(principal);

        var properties = principal!.GetType().GetProperties().Select(p => p.Name);
        Assert.DoesNotContain(properties, name => name.Contains("Role", StringComparison.OrdinalIgnoreCase));
    }
}
