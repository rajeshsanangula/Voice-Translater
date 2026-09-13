using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
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
/// Phase 7.0 HTTP-boundary tests for GET/PUT /profile AND, since profile is the easiest
/// observable proof of account existence, the gated-JIT-provisioning flow's real HTTP
/// path (a never-before-seen, Entra-verified identity's FIRST authenticated request
/// provisions an Account+Profile and immediately succeeds against /profile, with no
/// separate "provision" call ever exposed).
/// </summary>
public sealed class ProfileEndpointTests : IClassFixture<ProfileEndpointTests.TestApiFactory>
{
    private const string TestIssuer = "https://test-issuer.example/";
    private const string TestAudience = "test-audience";
    private static readonly SymmetricSecurityKey SigningKey = new(Encoding.UTF8.GetBytes("phase-7.0-test-signing-key-not-a-real-secret-32bytes+"));

    private readonly TestApiFactory _factory;

    public ProfileEndpointTests(TestApiFactory factory) => _factory = factory;

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
                    options.TokenValidationParameters = new TokenValidationParameters
                    {
                        ValidateIssuer = true,
                        ValidIssuer = TestIssuer,
                        ValidateAudience = true,
                        ValidAudience = TestAudience,
                        ValidateLifetime = true,
                        ValidateIssuerSigningKey = true,
                        IssuerSigningKey = SigningKey,
                        ClockSkew = TimeSpan.Zero,
                    };
                });
            });
        }
    }

    private static string BuildToken(string subject, bool emailVerified = true, string? email = null)
    {
        var now = DateTime.UtcNow;
        var claims = new List<Claim>
        {
            new("sub", subject),
            new("iat", new DateTimeOffset(now).ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64),
            new("email_verified", emailVerified ? "true" : "false"),
        };
        if (email is not null) claims.Add(new Claim("email", email));

        var token = new JwtSecurityToken(TestIssuer, TestAudience, claims, now.AddMinutes(-1), now.AddMinutes(15),
            new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private HttpClient AuthenticatedClient(string token)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private sealed class ProfileResponse { public string? displayName { get; set; } public string? preferredLanguagePair { get; set; } public DateTimeOffset updatedAt { get; set; } }

    // ---- Gated JIT provisioning, exercised over real HTTP ----

    [Fact]
    public async Task NeverSeenVerifiedIdentity_FirstRequest_ProvisionsAndSucceeds()
    {
        using var client = AuthenticatedClient(BuildToken("provision-http-verified", emailVerified: true));

        var response = await client.GetAsync("/profile");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task NeverSeenUnverifiedIdentity_Denied_SameShapeAsUnknownAccount()
    {
        using var client = AuthenticatedClient(BuildToken("provision-http-unverified", emailVerified: false));

        var response = await client.GetAsync("/profile");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("account_not_found", body); // anti-enumeration: identical to "no account at all"
    }

    [Fact]
    public async Task RepeatedFirstLogin_SameIdentity_ResolvesSameAccount_NoDuplicateProvisioning()
    {
        var token = BuildToken("provision-http-repeat", emailVerified: true);
        using var client1 = AuthenticatedClient(token);
        using var client2 = AuthenticatedClient(token);

        var first = await client1.PutAsJsonAsync("/profile", new { displayName = "First Call" });
        var second = await client2.GetAsync("/profile");

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var body = await second.Content.ReadFromJsonAsync<ProfileResponse>();
        Assert.Equal("First Call", body!.displayName); // same account — the PUT from the first call is visible to the second
    }

    // ---- Profile GET/PUT ----

    [Fact]
    public async Task AnonymousRequest_Returns401()
    {
        using var client = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/profile")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PutAsJsonAsync("/profile", new { displayName = "x" })).StatusCode);
    }

    [Fact]
    public async Task Put_ThenGet_RoundTrips()
    {
        using var client = AuthenticatedClient(BuildToken("profile-roundtrip-subject"));

        var put = await client.PutAsJsonAsync("/profile", new { displayName = "Roundtrip User", preferredLanguagePair = "en-US:de-DE" });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var get = await client.GetAsync("/profile");
        var body = await get.Content.ReadFromJsonAsync<ProfileResponse>();
        Assert.Equal("Roundtrip User", body!.displayName);
        Assert.Equal("en-US:de-DE", body.preferredLanguagePair);
    }

    [Fact]
    public async Task Put_InvalidLanguagePair_Returns400()
    {
        using var client = AuthenticatedClient(BuildToken("profile-invalid-subject"));
        var response = await client.PutAsJsonAsync("/profile", new { preferredLanguagePair = "garbage" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CrossAccount_CannotReadAnotherAccountsProfile()
    {
        using var clientA = AuthenticatedClient(BuildToken("profile-isolation-a"));
        using var clientB = AuthenticatedClient(BuildToken("profile-isolation-b"));

        await clientA.PutAsJsonAsync("/profile", new { displayName = "Account A Name" });
        await clientB.PutAsJsonAsync("/profile", new { displayName = "Account B Name" });

        var bodyA = await (await clientA.GetAsync("/profile")).Content.ReadFromJsonAsync<ProfileResponse>();
        var bodyB = await (await clientB.GetAsync("/profile")).Content.ReadFromJsonAsync<ProfileResponse>();

        Assert.Equal("Account A Name", bodyA!.displayName);
        Assert.Equal("Account B Name", bodyB!.displayName);
    }

    [Fact]
    public async Task ForgedAccountIdInBody_HasNoEffect_UpdateAppliesToAuthenticatedAccountOnly()
    {
        using var clientA = AuthenticatedClient(BuildToken("profile-forged-a"));
        using var clientB = AuthenticatedClient(BuildToken("profile-forged-b"));

        // Give clientB a distinct name first.
        await clientB.PutAsJsonAsync("/profile", new { displayName = "Real B Name" });

        // clientA attempts to smuggle an accountId — the request DTO has no such member,
        // so it is silently ignored by System.Text.Json; the update still applies only
        // to clientA's own authenticated account.
        await clientA.PutAsJsonAsync("/profile", new { displayName = "A Trying To Spoof", accountId = Guid.NewGuid().ToString() });

        var bodyB = await (await clientB.GetAsync("/profile")).Content.ReadFromJsonAsync<ProfileResponse>();
        Assert.Equal("Real B Name", bodyB!.displayName); // untouched by clientA's request
    }

    [Fact]
    public async Task SuspendedAccount_DeniedAtProfileEndpoint()
    {
        var subject = "profile-suspended-subject";
        var token = BuildToken(subject);
        using var provisionClient = AuthenticatedClient(token);
        await provisionClient.GetAsync("/profile"); // provisions the account

        var accounts = (InMemoryAccountRepository)_factory.Services.GetRequiredService<IAccountRepository>();
        var account = await accounts.FindByExternalIdentityAsync("EntraExternalId", subject, CancellationToken.None);
        account!.Status = AccountStatus.Suspended;
        await accounts.SaveAsync(account, CancellationToken.None);

        using var client = AuthenticatedClient(token);
        var response = await client.GetAsync("/profile");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task DisabledAccount_DeniedAtProfileEndpoint()
    {
        var subject = "profile-disabled-subject";
        var token = BuildToken(subject);
        using var provisionClient = AuthenticatedClient(token);
        await provisionClient.GetAsync("/profile");

        var accounts = (InMemoryAccountRepository)_factory.Services.GetRequiredService<IAccountRepository>();
        var account = await accounts.FindByExternalIdentityAsync("EntraExternalId", subject, CancellationToken.None);
        account!.Status = AccountStatus.Disabled;
        await accounts.SaveAsync(account, CancellationToken.None);

        using var client = AuthenticatedClient(token);
        var response = await client.GetAsync("/profile");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ClosedAccount_DeniedAtProfileEndpoint()
    {
        var subject = "profile-closed-subject";
        var token = BuildToken(subject);
        using var provisionClient = AuthenticatedClient(token);
        await provisionClient.GetAsync("/profile");

        var accounts = (InMemoryAccountRepository)_factory.Services.GetRequiredService<IAccountRepository>();
        var account = await accounts.FindByExternalIdentityAsync("EntraExternalId", subject, CancellationToken.None);
        account!.Status = AccountStatus.Closed;
        await accounts.SaveAsync(account, CancellationToken.None);

        using var client = AuthenticatedClient(token);
        var response = await client.GetAsync("/profile");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task PendingAccount_DeniedAtProfileEndpoint()
    {
        var subject = "profile-pending-subject";
        var token = BuildToken(subject);
        using var provisionClient = AuthenticatedClient(token);
        await provisionClient.GetAsync("/profile");

        var accounts = (InMemoryAccountRepository)_factory.Services.GetRequiredService<IAccountRepository>();
        var account = await accounts.FindByExternalIdentityAsync("EntraExternalId", subject, CancellationToken.None);
        account!.Status = AccountStatus.Pending;
        await accounts.SaveAsync(account, CancellationToken.None);

        using var client = AuthenticatedClient(token);
        var response = await client.GetAsync("/profile");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ForgedRoleClaim_DuringProvisioning_NeverElevatesRole()
    {
        var now = DateTime.UtcNow;
        var claims = new List<Claim>
        {
            new("sub", "provision-forged-role-subject"),
            new("iat", new DateTimeOffset(now).ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64),
            new("email_verified", "true"),
            new("role", "SuperAdmin"),
            new("roles", "Admin"),
        };
        var token = new JwtSecurityToken(TestIssuer, TestAudience, claims, now.AddMinutes(-1), now.AddMinutes(15),
            new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256));
        var jwt = new JwtSecurityTokenHandler().WriteToken(token);

        using var client = AuthenticatedClient(jwt);
        await client.GetAsync("/profile"); // provisions

        var accounts = (InMemoryAccountRepository)_factory.Services.GetRequiredService<IAccountRepository>();
        var account = await accounts.FindByExternalIdentityAsync("EntraExternalId", "provision-forged-role-subject", CancellationToken.None);

        Assert.Equal(Role.Customer, account!.Role); // never SuperAdmin/Admin despite the forged claims
    }
}
