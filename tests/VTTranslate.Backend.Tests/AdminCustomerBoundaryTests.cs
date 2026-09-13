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
using VTTranslate.Backend.Api.Security;

namespace VTTranslate.Backend.Tests;

/// <summary>
/// Phase 7.0 — proves the customer/admin authentication-scheme boundary
/// (docs/phase-7.0-production-identity-and-account-lifecycle.md §12/§13). Two
/// completely separate signing keys/issuers/audiences stand in for the two separate
/// production tenants (customer Entra External ID vs. admin Entra workforce tenant) —
/// no real tenant configuration is fabricated; this is the same deterministic-signing
/// test pattern already used by AuthenticationIntegrationTests/ProfileEndpointTests.
/// </summary>
public sealed class AdminCustomerBoundaryTests : IClassFixture<AdminCustomerBoundaryTests.TestApiFactory>
{
    private const string CustomerIssuer = "https://test-customer-issuer.example/";
    private const string CustomerAudience = "test-customer-audience";
    private const string AdminIssuer = "https://test-admin-issuer.example/";
    private const string AdminAudience = "test-admin-audience";
    private static readonly SymmetricSecurityKey CustomerSigningKey = new(Encoding.UTF8.GetBytes("phase-7.0-customer-test-key-not-a-real-secret-32bytes+"));
    private static readonly SymmetricSecurityKey AdminSigningKey = new(Encoding.UTF8.GetBytes("phase-7.0-admin-test-key-not-a-real-secret-32bytes-xy"));

    private readonly TestApiFactory _factory;

    public AdminCustomerBoundaryTests(TestApiFactory factory) => _factory = factory;

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
                        ValidIssuer = CustomerIssuer,
                        ValidateAudience = true,
                        ValidAudience = CustomerAudience,
                        ValidateLifetime = true,
                        ValidateIssuerSigningKey = true,
                        IssuerSigningKey = CustomerSigningKey,
                        ClockSkew = TimeSpan.Zero,
                    };
                });
                services.Configure<JwtBearerOptions>(AdminIdentityOptions.SchemeName, options =>
                {
                    options.Authority = null!;
                    options.MetadataAddress = null!;
                    options.RequireHttpsMetadata = false;
                    options.TokenValidationParameters = new TokenValidationParameters
                    {
                        ValidateIssuer = true,
                        ValidIssuer = AdminIssuer,
                        ValidateAudience = true,
                        ValidAudience = AdminAudience,
                        ValidateLifetime = true,
                        ValidateIssuerSigningKey = true,
                        IssuerSigningKey = AdminSigningKey,
                        ClockSkew = TimeSpan.Zero,
                    };
                });
            });
        }
    }

    private static string BuildToken(string issuer, string audience, SecurityKey key, string subject, IEnumerable<Claim>? extraClaims = null)
    {
        var now = DateTime.UtcNow;
        var claims = new List<Claim>
        {
            new("sub", subject),
            new("iat", new DateTimeOffset(now).ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64),
            new("email_verified", "true"),
        };
        if (extraClaims is not null) claims.AddRange(extraClaims);

        var token = new JwtSecurityToken(issuer, audience, claims, now.AddMinutes(-1), now.AddMinutes(15),
            new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static string CustomerToken(string subject, IEnumerable<Claim>? extraClaims = null) =>
        BuildToken(CustomerIssuer, CustomerAudience, CustomerSigningKey, subject, extraClaims);

    private static string AdminToken(string subject) =>
        BuildToken(AdminIssuer, AdminAudience, AdminSigningKey, subject);

    private HttpClient ClientWithToken(string token)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    [Fact]
    public async Task CustomerToken_CannotAuthenticateAsAdmin()
    {
        using var client = ClientWithToken(CustomerToken("customer-subject-1"));
        var response = await client.GetAsync("/internal/admin-boundary");

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task AdminToken_CannotAuthenticateAsCustomer()
    {
        using var client = ClientWithToken(AdminToken("admin-subject-1"));
        // /profile requires the DEFAULT (customer) scheme + a resolved AUTRAXIS Account —
        // an admin-scheme-only token must not satisfy it.
        var response = await client.GetAsync("/profile");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AdminToken_CanReachAdminBoundaryRoute()
    {
        using var client = ClientWithToken(AdminToken("admin-subject-valid"));
        var response = await client.GetAsync("/internal/admin-boundary");

        // 501 (NotImplementedPlaceholder) — proves the ADMIN SCHEME authenticated and
        // authorized successfully; the placeholder body itself is not a product feature.
        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
    }

    [Fact]
    public async Task ForgedRoleClaim_OnCustomerToken_CannotElevateToAdminBoundary()
    {
        var forgedToken = CustomerToken("forged-role-subject", [new Claim("role", "SuperAdmin"), new Claim("roles", "Admin")]);
        using var client = ClientWithToken(forgedToken);

        var response = await client.GetAsync("/internal/admin-boundary");
        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.NotImplemented, response.StatusCode); // never reaches the admin-scheme-gated placeholder
    }

    [Fact]
    public async Task ForgedAudience_CannotCrossBoundary()
    {
        // A token signed with the ADMIN key but claiming the CUSTOMER audience — proves
        // audience validation (not just signature) gates the boundary.
        var mismatchedToken = BuildToken(AdminIssuer, CustomerAudience, AdminSigningKey, "audience-mismatch-subject");
        using var client = ClientWithToken(mismatchedToken);

        var adminResponse = await client.GetAsync("/internal/admin-boundary");
        Assert.NotEqual(HttpStatusCode.NotImplemented, adminResponse.StatusCode); // wrong audience for AdminScheme's own validation

        var profileResponse = await client.GetAsync("/profile");
        Assert.Equal(HttpStatusCode.Unauthorized, profileResponse.StatusCode); // wrong issuer for the customer scheme
    }

    [Fact]
    public async Task WrongIssuer_CannotCrossBoundary()
    {
        // Correct admin audience, but signed by/issued from the customer issuer/key.
        var wrongIssuerToken = BuildToken(CustomerIssuer, AdminAudience, CustomerSigningKey, "wrong-issuer-subject");
        using var client = ClientWithToken(wrongIssuerToken);

        var response = await client.GetAsync("/internal/admin-boundary");
        Assert.NotEqual(HttpStatusCode.NotImplemented, response.StatusCode);
    }
}
