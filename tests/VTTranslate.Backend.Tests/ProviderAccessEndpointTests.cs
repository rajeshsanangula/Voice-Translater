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
/// Phase 6.8 HTTP-boundary tests for POST /provider-access — account isolation, device
/// authorization, entitlement enforcement, and the "no provider secret ever appears in a
/// response" guarantee, using the same offline-JWT pattern as every prior phase's
/// endpoint tests. No real Azure call occurs: no ProviderCredentials are configured in
/// this test host, so every request legitimately reports "unsupported_provider" once past
/// the device/entitlement checks — proving the fail-closed "unconfigured provider is
/// never silently faked" behavior is itself the thing under test for the success-path
/// cases here; the deeper "credential actually issued" behavior is covered by
/// ProviderAccessGatewayTests's in-memory fake issuer instead.
/// </summary>
public sealed class ProviderAccessEndpointTests : IClassFixture<ProviderAccessEndpointTests.TestApiFactory>
{
    private const string TestIssuer = "https://test-issuer.example/";
    private const string TestAudience = "test-audience";
    private static readonly SymmetricSecurityKey SigningKey = new(Encoding.UTF8.GetBytes("phase-6.8-test-signing-key-not-a-real-secret-32bytes+"));

    private readonly TestApiFactory _factory;

    public ProviderAccessEndpointTests(TestApiFactory factory) => _factory = factory;

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

    private static string BuildToken(string subject)
    {
        var now = DateTime.UtcNow;
        var claims = new List<Claim>
        {
            new("sub", subject),
            new("iat", new DateTimeOffset(now).ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64),
        };
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

    private async Task<Account> SeedAccountAsync(string subject)
    {
        var repo = (InMemoryAccountRepository)_factory.Services.GetRequiredService<IAccountRepository>();
        var account = new Account
        {
            Id = Guid.NewGuid(),
            Email = $"{subject}@example.com",
            EmailVerified = true,
            ExternalIdentityProvider = "EntraExternalId",
            ExternalSubjectId = subject,
            Role = Role.Customer,
            Status = AccountStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await repo.SaveAsync(account, CancellationToken.None);
        return account;
    }

    private async Task SeedSubscriptionAsync(Guid accountId, int maxActiveDevices = 2)
    {
        var plans = (InMemoryPlanRepository)_factory.Services.GetRequiredService<IPlanRepository>();
        var planId = Guid.NewGuid();
        plans.Seed(new Plan { Id = planId, Name = "Test", IsPubliclyPurchasable = false },
            [new Entitlement { Id = Guid.NewGuid(), PlanId = planId, Key = "MaxActiveDevices", Value = maxActiveDevices.ToString() }]);

        var subscriptions = (InMemorySubscriptionRepository)_factory.Services.GetRequiredService<ISubscriptionRepository>();
        await subscriptions.SaveAsync(new Subscription
        {
            Id = Guid.NewGuid(), AccountId = accountId, PlanId = planId, Status = SubscriptionStatus.Active,
            CurrentPeriodStart = DateTimeOffset.UtcNow.AddDays(-1), CurrentPeriodEnd = DateTimeOffset.UtcNow.AddDays(30),
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        }, CancellationToken.None);
    }

    private async Task<Guid> RegisterDeviceAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/devices", new { platform = "Windows" });
        var body = await response.Content.ReadFromJsonAsync<DeviceIdWrapper>();
        return body!.id;
    }

    private sealed class DeviceIdWrapper { public Guid id { get; set; } }

    [Fact]
    public async Task AnonymousRequest_Returns401()
    {
        using var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/provider-access", new { deviceId = Guid.NewGuid().ToString(), provider = "AzureSpeech", capability = "SpeechRecognition" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task UnknownAccount_ControlledForbidden()
    {
        // A validly-signed token for a subject that was never registered as an Account —
        // AccountResolutionMiddleware's existing "account_not_found" 403, unchanged.
        var token = BuildToken("provider-access-unknown-subject");
        using var client = AuthenticatedClient(token);

        var response = await client.PostAsJsonAsync("/provider-access", new { deviceId = Guid.NewGuid().ToString(), provider = "AzureSpeech", capability = "SpeechRecognition" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("account_not_found", body);
    }

    [Fact]
    public async Task SuspendedAccount_Returns403()
    {
        var repo = (InMemoryAccountRepository)_factory.Services.GetRequiredService<IAccountRepository>();
        var account = new Account
        {
            Id = Guid.NewGuid(), Email = "suspended@example.com", EmailVerified = true,
            ExternalIdentityProvider = "EntraExternalId", ExternalSubjectId = "provider-access-suspended-subject",
            Role = Role.Customer, Status = AccountStatus.Suspended, CreatedAt = DateTimeOffset.UtcNow,
        };
        await repo.SaveAsync(account, CancellationToken.None);
        var token = BuildToken("provider-access-suspended-subject");

        using var client = AuthenticatedClient(token);
        var response = await client.PostAsJsonAsync("/provider-access", new { deviceId = Guid.NewGuid().ToString(), provider = "AzureSpeech", capability = "SpeechRecognition" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("account_suspended", body);
    }

    [Fact]
    public async Task UnauthorizedDevice_UnknownId_Returns403DeviceNotAuthorized()
    {
        var account = await SeedAccountAsync("provider-access-subject-1");
        await SeedSubscriptionAsync(account.Id);
        var token = BuildToken("provider-access-subject-1");

        using var client = AuthenticatedClient(token);
        var response = await client.PostAsJsonAsync("/provider-access", new { deviceId = Guid.NewGuid().ToString(), provider = "AzureSpeech", capability = "SpeechRecognition" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("device_not_authorized", body);
    }

    [Fact]
    public async Task RevokedDevice_Returns403DeviceNotAuthorized()
    {
        var account = await SeedAccountAsync("provider-access-subject-2");
        await SeedSubscriptionAsync(account.Id);
        var token = BuildToken("provider-access-subject-2");
        using var client = AuthenticatedClient(token);
        var deviceId = await RegisterDeviceAsync(client);
        await client.PostAsync($"/devices/{deviceId}/revoke", null);

        var response = await client.PostAsJsonAsync("/provider-access", new { deviceId = deviceId.ToString(), provider = "AzureSpeech", capability = "SpeechRecognition" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("device_not_authorized", body);
    }

    [Fact]
    public async Task CrossAccountDevice_Denied_NeverLeaksOwnership()
    {
        var accountA = await SeedAccountAsync("provider-access-isolation-a");
        var accountB = await SeedAccountAsync("provider-access-isolation-b");
        await SeedSubscriptionAsync(accountA.Id);
        await SeedSubscriptionAsync(accountB.Id);

        using var clientB = AuthenticatedClient(BuildToken("provider-access-isolation-b"));
        var deviceIdOwnedByB = await RegisterDeviceAsync(clientB);

        using var clientA = AuthenticatedClient(BuildToken("provider-access-isolation-a"));
        var response = await clientA.PostAsJsonAsync("/provider-access", new { deviceId = deviceIdOwnedByB.ToString(), provider = "AzureSpeech", capability = "SpeechRecognition" });

        // device_not_authorized — identical to "device doesn't exist"; never a status
        // that would confirm the device exists and belongs to someone else.
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("device_not_authorized", body);
    }

    [Fact]
    public async Task EntitlementDenied_Returns403_BeforeEverReachingProviderCheck()
    {
        // Device registered but NO subscription seeded at all — EntitlementService's own
        // fail-closed default applies. Proves entitlement denial is enforced at the real
        // HTTP boundary, not only in the isolated gateway unit tests.
        var account = await SeedAccountAsync("provider-access-entitlement-subject");
        var token = BuildToken("provider-access-entitlement-subject");
        using var client = AuthenticatedClient(token);

        // Register the device WITHOUT a subscription — DeviceRegistrationService's own
        // no-subscription fail-closed default (limit 1) still allows exactly one device.
        var deviceId = await RegisterDeviceAsync(client);

        var response = await client.PostAsJsonAsync("/provider-access", new { deviceId = deviceId.ToString(), provider = "AzureSpeech", capability = "SpeechRecognition" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("entitlement_denied", body);
    }

    [Fact]
    public async Task UnconfiguredProvider_ReportsUnsupportedProvider_NeverFabricatesCredential()
    {
        // No ProviderCredentials are configured in this test host — this proves the
        // fail-closed contract itself: a syntactically valid, fully-authorized request
        // still never receives a fabricated credential.
        var account = await SeedAccountAsync("provider-access-subject-3");
        await SeedSubscriptionAsync(account.Id);
        var token = BuildToken("provider-access-subject-3");
        using var client = AuthenticatedClient(token);
        var deviceId = await RegisterDeviceAsync(client);

        var response = await client.PostAsJsonAsync("/provider-access", new { deviceId = deviceId.ToString(), provider = "AzureSpeech", capability = "SpeechRecognition" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("unsupported_provider", body);
        Assert.DoesNotContain("accessToken", body);
    }

    [Fact]
    public async Task MalformedProvider_Returns400()
    {
        var account = await SeedAccountAsync("provider-access-subject-4");
        await SeedSubscriptionAsync(account.Id);
        var token = BuildToken("provider-access-subject-4");
        using var client = AuthenticatedClient(token);
        var deviceId = await RegisterDeviceAsync(client);

        var response = await client.PostAsJsonAsync("/provider-access", new { deviceId = deviceId.ToString(), provider = "NotARealProvider", capability = "SpeechRecognition" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task MalformedCapability_Returns400()
    {
        var account = await SeedAccountAsync("provider-access-subject-5");
        await SeedSubscriptionAsync(account.Id);
        var token = BuildToken("provider-access-subject-5");
        using var client = AuthenticatedClient(token);
        var deviceId = await RegisterDeviceAsync(client);

        var response = await client.PostAsJsonAsync("/provider-access", new { deviceId = deviceId.ToString(), provider = "AzureSpeech", capability = "NotARealCapability" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task MalformedDeviceId_Returns400()
    {
        var account = await SeedAccountAsync("provider-access-subject-6");
        await SeedSubscriptionAsync(account.Id);
        var token = BuildToken("provider-access-subject-6");
        using var client = AuthenticatedClient(token);

        var response = await client.PostAsJsonAsync("/provider-access", new { deviceId = "not-a-guid", provider = "AzureSpeech", capability = "SpeechRecognition" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task NoLongLivedProviderKeyEndpointExists()
    {
        using var client = _factory.CreateClient();
        foreach (var path in new[] { "/azure-key", "/translator-key", "/gemini-key", "/provider-key", "/speech-key" })
        {
            var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }
}
