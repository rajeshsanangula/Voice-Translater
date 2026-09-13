using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using VTTranslate.Backend.Domain.Abstractions;
using VTTranslate.Backend.Domain.Entities;
using VTTranslate.Backend.Domain.Enums;
using VTTranslate.Backend.Infrastructure.Persistence;

namespace VTTranslate.Backend.Tests;

/// <summary>
/// Phase 6.4's headline security test suite. Proves the ACTUAL HTTP authentication and
/// authorization boundary end to end — not just the isolated application-layer
/// services — using the REAL <c>Microsoft.AspNetCore.Authentication.JwtBearer</c>
/// handler (no fakes, no shortcuts for the cryptographic path) wired to a static,
/// offline, test-only signing key. NO network call to any real identity provider ever
/// occurs: <see cref="JwtBearerOptions.Authority"/>/<c>MetadataAddress</c> are left
/// unset and <see cref="JwtBearerOptions.TokenValidationParameters"/> is supplied
/// directly with a local symmetric key, issuer, and audience — the SAME
/// Microsoft.IdentityModel validation code path a real Entra-backed deployment uses,
/// just pointed at test-controlled, offline values (Phase 6.4 instruction K: "Do NOT
/// require live Entra calls for ordinary unit tests").
/// </summary>
public sealed class AuthenticationIntegrationTests : IClassFixture<AuthenticationIntegrationTests.TestApiFactory>
{
    private const string TestIssuer = "https://test-issuer.example/";
    private const string TestAudience = "test-audience";
    private static readonly SymmetricSecurityKey SigningKey = new(Encoding.UTF8.GetBytes("phase-6.4-test-signing-key-not-a-real-secret-32bytes+"));
    private static readonly SymmetricSecurityKey WrongSigningKey = new(Encoding.UTF8.GetBytes("a-completely-different-test-key-that-must-fail-validation!!"));

    private readonly TestApiFactory _factory;

    public AuthenticationIntegrationTests(TestApiFactory factory) => _factory = factory;

    /// <summary>Test-only WebApplicationFactory customization — see Phase 6.4 instruction L: isolated to test infrastructure, never enabled in production (this class exists only in the test assembly; nothing in Program.cs or any production configuration path references it).</summary>
    public sealed class TestApiFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureTestServices(services =>
            {
                services.Configure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
                {
                    options.Authority = null!;
                    options.MetadataAddress = null!;
                    options.RequireHttpsMetadata = false;
                    options.IncludeErrorDetails = true;
                    options.TokenValidationParameters = new TokenValidationParameters
                    {
                        ValidateIssuer = true,
                        ValidIssuer = TestIssuer,
                        ValidateAudience = true,
                        ValidAudience = TestAudience,
                        ValidateLifetime = true,
                        ValidateIssuerSigningKey = true,
                        IssuerSigningKey = SigningKey,
                        ClockSkew = TimeSpan.Zero, // exact expiry testing, not the 5-minute default leeway
                    };
                });
            });
        }
    }

    private static string BuildToken(
        SecurityKey signingKey,
        string issuer = TestIssuer,
        string audience = TestAudience,
        string subject = "test-subject-1",
        DateTime? notBefore = null,
        DateTime? expires = null,
        bool includeEmail = true)
    {
        var now = DateTime.UtcNow;
        // "exp"/"nbf" are added automatically by the JwtSecurityToken constructor below
        // from the notBefore/expires parameters, but "iat" (issued-at) is NOT — it must
        // be added explicitly as a claim, since EntraIdentityProvider requires it.
        var claims = new List<Claim>
        {
            new("sub", subject),
            new("iat", new DateTimeOffset(now).ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64),
        };
        if (includeEmail) claims.Add(new Claim("email", "user@example.com"));

        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: claims,
            notBefore: notBefore ?? now.AddMinutes(-1),
            expires: expires ?? now.AddMinutes(15),
            signingCredentials: new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private HttpClient AuthenticatedClient(string? token)
    {
        var client = _factory.CreateClient();
        if (token is not null)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private async Task<Account> SeedAccountAsync(string subject, Role role = Role.Customer, AccountStatus status = AccountStatus.Active)
    {
        var repo = (InMemoryAccountRepository)_factory.Services.GetRequiredService<IAccountRepository>();
        var account = new Account
        {
            Id = Guid.NewGuid(),
            Email = "user@example.com",
            EmailVerified = true,
            ExternalIdentityProvider = "EntraExternalId",
            ExternalSubjectId = subject,
            Role = role,
            Status = status,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await repo.SaveAsync(account, CancellationToken.None);
        return account;
    }

    // ==================== AUTHENTICATION (Phase 6.4 instruction K, items 1-7) ====================

    [Fact]
    public async Task MissingToken_Rejected401()
    {
        using var client = AuthenticatedClient(null);
        var response = await client.GetAsync("/account");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task MalformedToken_Rejected401()
    {
        using var client = AuthenticatedClient("this-is-not-a-jwt-at-all");
        var response = await client.GetAsync("/account");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ExpiredToken_Rejected401()
    {
        var token = BuildToken(SigningKey, expires: DateTime.UtcNow.AddMinutes(-5), notBefore: DateTime.UtcNow.AddMinutes(-20));
        using var client = AuthenticatedClient(token);
        var response = await client.GetAsync("/account");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task InvalidIssuer_Rejected401()
    {
        var token = BuildToken(SigningKey, issuer: "https://not-the-trusted-issuer.example/");
        using var client = AuthenticatedClient(token);
        var response = await client.GetAsync("/account");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task InvalidAudience_Rejected401()
    {
        var token = BuildToken(SigningKey, audience: "some-other-audience");
        using var client = AuthenticatedClient(token);
        var response = await client.GetAsync("/account");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task InvalidSignature_Rejected401()
    {
        // Signed with a DIFFERENT key than the server trusts — the real
        // Microsoft.IdentityModel signature-verification path must reject this.
        var token = BuildToken(WrongSigningKey);
        using var client = AuthenticatedClient(token);
        var response = await client.GetAsync("/account");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ValidToken_KnownActiveAccount_AcceptedByAuthenticationLayer_ReachesPlaceholder()
    {
        await SeedAccountAsync("valid-subject-1");
        var token = BuildToken(SigningKey, subject: "valid-subject-1");

        using var client = AuthenticatedClient(token);
        var response = await client.GetAsync("/account");

        // Authenticated + resolved + authorized (Customer-level, the default) -> reaches
        // the still-not-implemented placeholder body, i.e. 501, never 401/403.
        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("not_implemented", body);
    }

    // ==================== ACCOUNT RESOLUTION (items 8-11) ====================

    [Fact]
    public async Task ValidIdentity_UnknownAccount_ControlledFailure_403AccountNotFound()
    {
        var token = BuildToken(SigningKey, subject: "never-registered-subject");
        using var client = AuthenticatedClient(token);
        var response = await client.GetAsync("/account");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("account_not_found", body);
    }

    [Fact]
    public async Task ValidIdentity_SuspendedAccount_Rejected403()
    {
        await SeedAccountAsync("suspended-subject", status: AccountStatus.Suspended);
        var token = BuildToken(SigningKey, subject: "suspended-subject");

        using var client = AuthenticatedClient(token);
        var response = await client.GetAsync("/account");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("account_suspended", body);
    }

    [Fact]
    public async Task TokenMissingRequiredSubjectClaim_FailClosed_401_NotSilentlyAccepted()
    {
        // A token that passes cryptographic validation (real signature/issuer/audience/
        // expiry all correct) but carries no "sub" claim at all — EntraIdentityProvider
        // must fail closed (AccountResolutionMiddleware returns 401), never fabricate an
        // identity or fall through to the placeholder.
        var now = DateTime.UtcNow;
        var noSubjectToken = new JwtSecurityToken(
            issuer: TestIssuer, audience: TestAudience,
            claims: [new Claim("iat", new DateTimeOffset(now).ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64), new Claim("email", "user@example.com")], // deliberately no "sub"/"oid"
            notBefore: now.AddMinutes(-1), expires: now.AddMinutes(15),
            signingCredentials: new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256));
        var tokenString = new JwtSecurityTokenHandler().WriteToken(noSubjectToken);

        using var client = AuthenticatedClient(tokenString);
        var response = await client.GetAsync("/account");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("invalid_identity_claims", body);
    }

    // ==================== ROLE SECURITY (items 12-18) ====================

    [Fact]
    public async Task CustomerAccount_CannotAccessSuperAdminOperation_403()
    {
        await SeedAccountAsync("customer-subject", role: Role.Customer);
        var token = BuildToken(SigningKey, subject: "customer-subject");

        using var client = AuthenticatedClient(token);
        var response = await client.GetAsync("/internal/diagnostics");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("insufficient_role", body);
    }

    [Fact]
    public async Task AdminAccount_CannotAccessSuperAdminOperation_403()
    {
        await SeedAccountAsync("admin-subject", role: Role.Admin);
        var token = BuildToken(SigningKey, subject: "admin-subject");

        using var client = AuthenticatedClient(token);
        var response = await client.GetAsync("/internal/diagnostics");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task SuperAdminAccount_CanAccessPermittedOperation_ReachesPlaceholder()
    {
        await SeedAccountAsync("superadmin-subject", role: Role.SuperAdmin);
        var token = BuildToken(SigningKey, subject: "superadmin-subject");

        using var client = AuthenticatedClient(token);
        var response = await client.GetAsync("/internal/diagnostics");

        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode); // passed the role gate, reached the (still unimplemented) placeholder
    }

    [Fact]
    public async Task ClientSuppliedRoleHeader_CannotEscalatePrivileges()
    {
        // The client attaches a header CLAIMING admin/super-admin privilege. The backend
        // must never read this — role comes exclusively from the resolved Account.
        await SeedAccountAsync("customer-subject-2", role: Role.Customer);
        var token = BuildToken(SigningKey, subject: "customer-subject-2");

        using var client = AuthenticatedClient(token);
        client.DefaultRequestHeaders.Add("X-AUTRAXIS-Role", "SuperAdmin");
        client.DefaultRequestHeaders.Add("X-Role", "ADMIN");
        var response = await client.GetAsync("/internal/diagnostics");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ClientSuppliedRoleClaimInsideJwt_CannotEscalatePrivileges()
    {
        // Even a role claim embedded IN the (validly signed, by the legitimate test
        // key — simulating "what if Entra were ever configured to emit a role claim")
        // token must have zero effect, since EntraIdentityProvider never reads one and
        // AuthorizationService only ever consults Account.Role.
        await SeedAccountAsync("customer-subject-3", role: Role.Customer);
        var now = DateTime.UtcNow;
        var tokenWithRoleClaim = new JwtSecurityToken(
            issuer: TestIssuer, audience: TestAudience,
            claims: [new Claim("sub", "customer-subject-3"), new Claim("iat", new DateTimeOffset(now).ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64), new Claim("role", "SuperAdmin"), new Claim("roles", "SuperAdmin")],
            notBefore: now.AddMinutes(-1), expires: now.AddMinutes(15),
            signingCredentials: new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256));
        var tokenString = new JwtSecurityTokenHandler().WriteToken(tokenWithRoleClaim);

        using var client = AuthenticatedClient(tokenString);
        var response = await client.GetAsync("/internal/diagnostics");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ==================== AUTHORIZATION (items 19-22) ====================

    [Fact]
    public async Task Unauthenticated_Returns401()
    {
        using var client = AuthenticatedClient(null);
        var response = await client.GetAsync("/internal/diagnostics");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AuthenticatedButUnauthorized_Returns403_NotSomeOtherStatus()
    {
        await SeedAccountAsync("customer-subject-4", role: Role.Customer);
        var token = BuildToken(SigningKey, subject: "customer-subject-4");

        using var client = AuthenticatedClient(token);
        var response = await client.GetAsync("/internal/diagnostics");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AuthorizedRequest_ReachesApplicationLayerPlaceholder_Never200()
    {
        await SeedAccountAsync("superadmin-subject-2", role: Role.SuperAdmin);
        var token = BuildToken(SigningKey, subject: "superadmin-subject-2");

        using var client = AuthenticatedClient(token);
        var response = await client.GetAsync("/internal/diagnostics");

        // Reaches the application boundary (past auth+authz) but nothing is
        // IMPLEMENTED yet — must never be a fabricated 200.
        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
    }

    [Fact]
    public async Task HealthEndpoints_RemainAnonymous_NoAuthenticationRequired()
    {
        using var client = AuthenticatedClient(null);
        var response = await client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ResponseNeverContainsTheBearerTokenOrAuthorizationHeaderValue()
    {
        // No response body from any of these flows should ever echo the caller's own
        // token back (a sanity check against accidental debug-echo endpoints).
        var token = BuildToken(SigningKey, subject: "never-registered-subject-2");
        using var client = AuthenticatedClient(token);
        var response = await client.GetAsync("/account");
        var body = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain(token, body);
    }
}
