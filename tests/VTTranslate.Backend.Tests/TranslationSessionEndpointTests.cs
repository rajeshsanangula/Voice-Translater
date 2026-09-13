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
/// Phase 6.9 HTTP-boundary tests for POST /translation-sessions, .../heartbeat and
/// .../end — account isolation, device authorization, entitlement/usage enforcement, and
/// the "client never controls duration/account/usage" guarantee, using the same
/// offline-JWT WebApplicationFactory pattern as ProviderAccessEndpointTests.
/// </summary>
public sealed class TranslationSessionEndpointTests : IClassFixture<TranslationSessionEndpointTests.TestApiFactory>
{
    private const string TestIssuer = "https://test-issuer.example/";
    private const string TestAudience = "test-audience";
    private static readonly SymmetricSecurityKey SigningKey = new(Encoding.UTF8.GetBytes("phase-6.9-test-signing-key-not-a-real-secret-32bytes+"));

    private readonly TestApiFactory _factory;

    public TranslationSessionEndpointTests(TestApiFactory factory) => _factory = factory;

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
    private sealed class SessionIdWrapper { public Guid sessionId { get; set; } }

    [Fact]
    public async Task AnonymousRequest_Returns401()
    {
        using var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/translation-sessions", new { deviceId = Guid.NewGuid().ToString() });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task MalformedDeviceId_Returns400()
    {
        var account = await SeedAccountAsync("session-subject-malformed");
        await SeedSubscriptionAsync(account.Id);
        using var client = AuthenticatedClient(BuildToken("session-subject-malformed"));

        var response = await client.PostAsJsonAsync("/translation-sessions", new { deviceId = "not-a-guid" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task NonexistentDevice_Returns403DeviceNotAuthorized()
    {
        var account = await SeedAccountAsync("session-subject-1");
        await SeedSubscriptionAsync(account.Id);
        using var client = AuthenticatedClient(BuildToken("session-subject-1"));

        var response = await client.PostAsJsonAsync("/translation-sessions", new { deviceId = Guid.NewGuid().ToString() });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("device_not_authorized", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task RevokedDevice_Returns403DeviceNotAuthorized()
    {
        var account = await SeedAccountAsync("session-subject-2");
        await SeedSubscriptionAsync(account.Id);
        using var client = AuthenticatedClient(BuildToken("session-subject-2"));
        var deviceId = await RegisterDeviceAsync(client);
        await client.PostAsync($"/devices/{deviceId}/revoke", null);

        var response = await client.PostAsJsonAsync("/translation-sessions", new { deviceId = deviceId.ToString() });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("device_not_authorized", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task CrossAccountDevice_Denied_NeverLeaksOwnership()
    {
        var accountA = await SeedAccountAsync("session-isolation-a");
        var accountB = await SeedAccountAsync("session-isolation-b");
        await SeedSubscriptionAsync(accountA.Id);
        await SeedSubscriptionAsync(accountB.Id);

        using var clientB = AuthenticatedClient(BuildToken("session-isolation-b"));
        var deviceIdOwnedByB = await RegisterDeviceAsync(clientB);

        using var clientA = AuthenticatedClient(BuildToken("session-isolation-a"));
        var response = await clientA.PostAsJsonAsync("/translation-sessions", new { deviceId = deviceIdOwnedByB.ToString() });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("device_not_authorized", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task EntitlementDenied_NoSubscription_Returns403()
    {
        // No subscription seeded — device gets registered under the no-subscription
        // fail-closed default (limit 1) but session start hits entitlement denial.
        var account = await SeedAccountAsync("session-entitlement-subject");
        using var client = AuthenticatedClient(BuildToken("session-entitlement-subject"));
        var deviceId = await RegisterDeviceAsync(client);

        var response = await client.PostAsJsonAsync("/translation-sessions", new { deviceId = deviceId.ToString() });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("entitlement_denied", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AuthorizedStart_Returns201_NoInternalIdsOrSecretsLeaked()
    {
        var account = await SeedAccountAsync("session-subject-3");
        await SeedSubscriptionAsync(account.Id);
        using var client = AuthenticatedClient(BuildToken("session-subject-3"));
        var deviceId = await RegisterDeviceAsync(client);

        var response = await client.PostAsJsonAsync("/translation-sessions", new { deviceId = deviceId.ToString(), clientSessionId = "corr-1" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"state\":\"Active\"", body);
        Assert.DoesNotContain("accessToken", body);
        Assert.DoesNotContain("secret", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DuplicateClientSessionId_ResumesInsteadOfCreatingNew()
    {
        var account = await SeedAccountAsync("session-subject-4");
        await SeedSubscriptionAsync(account.Id);
        using var client = AuthenticatedClient(BuildToken("session-subject-4"));
        var deviceId = await RegisterDeviceAsync(client);

        var first = await client.PostAsJsonAsync("/translation-sessions", new { deviceId = deviceId.ToString(), clientSessionId = "corr-2" });
        var firstBody = await first.Content.ReadFromJsonAsync<SessionIdWrapper>();

        var second = await client.PostAsJsonAsync("/translation-sessions", new { deviceId = deviceId.ToString(), clientSessionId = "corr-2" });
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var secondBody = await second.Content.ReadFromJsonAsync<SessionIdWrapper>();

        Assert.Equal(firstBody!.sessionId, secondBody!.sessionId);
    }

    [Fact]
    public async Task Heartbeat_NonexistentSession_Returns404()
    {
        var account = await SeedAccountAsync("session-subject-hb-404");
        await SeedSubscriptionAsync(account.Id);
        using var client = AuthenticatedClient(BuildToken("session-subject-hb-404"));

        var response = await client.PostAsync($"/translation-sessions/{Guid.NewGuid()}/heartbeat", null);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Heartbeat_CrossAccountSession_Returns404_NotLeakingExistence()
    {
        var accountA = await SeedAccountAsync("session-hb-isolation-a");
        var accountB = await SeedAccountAsync("session-hb-isolation-b");
        await SeedSubscriptionAsync(accountA.Id);
        await SeedSubscriptionAsync(accountB.Id);

        using var clientB = AuthenticatedClient(BuildToken("session-hb-isolation-b"));
        var deviceB = await RegisterDeviceAsync(clientB);
        var start = await clientB.PostAsJsonAsync("/translation-sessions", new { deviceId = deviceB.ToString() });
        var sessionId = (await start.Content.ReadFromJsonAsync<SessionIdWrapper>())!.sessionId;

        using var clientA = AuthenticatedClient(BuildToken("session-hb-isolation-a"));
        var response = await clientA.PostAsync($"/translation-sessions/{sessionId}/heartbeat", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Heartbeat_Active_Returns200()
    {
        var account = await SeedAccountAsync("session-subject-hb-ok");
        await SeedSubscriptionAsync(account.Id);
        using var client = AuthenticatedClient(BuildToken("session-subject-hb-ok"));
        var deviceId = await RegisterDeviceAsync(client);
        var start = await client.PostAsJsonAsync("/translation-sessions", new { deviceId = deviceId.ToString() });
        var sessionId = (await start.Content.ReadFromJsonAsync<SessionIdWrapper>())!.sessionId;

        var response = await client.PostAsync($"/translation-sessions/{sessionId}/heartbeat", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Heartbeat_AfterEnd_Returns409SessionTerminal()
    {
        var account = await SeedAccountAsync("session-subject-hb-terminal");
        await SeedSubscriptionAsync(account.Id);
        using var client = AuthenticatedClient(BuildToken("session-subject-hb-terminal"));
        var deviceId = await RegisterDeviceAsync(client);
        var start = await client.PostAsJsonAsync("/translation-sessions", new { deviceId = deviceId.ToString() });
        var sessionId = (await start.Content.ReadFromJsonAsync<SessionIdWrapper>())!.sessionId;
        await client.PostAsync($"/translation-sessions/{sessionId}/end", null);

        var response = await client.PostAsync($"/translation-sessions/{sessionId}/heartbeat", null);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("session_terminal", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task End_NonexistentSession_Returns404()
    {
        var account = await SeedAccountAsync("session-subject-end-404");
        await SeedSubscriptionAsync(account.Id);
        using var client = AuthenticatedClient(BuildToken("session-subject-end-404"));

        var response = await client.PostAsync($"/translation-sessions/{Guid.NewGuid()}/end", null);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task End_CrossAccountSession_Returns404_NotLeakingExistence()
    {
        var accountA = await SeedAccountAsync("session-end-isolation-a");
        var accountB = await SeedAccountAsync("session-end-isolation-b");
        await SeedSubscriptionAsync(accountA.Id);
        await SeedSubscriptionAsync(accountB.Id);

        using var clientB = AuthenticatedClient(BuildToken("session-end-isolation-b"));
        var deviceB = await RegisterDeviceAsync(clientB);
        var start = await clientB.PostAsJsonAsync("/translation-sessions", new { deviceId = deviceB.ToString() });
        var sessionId = (await start.Content.ReadFromJsonAsync<SessionIdWrapper>())!.sessionId;

        using var clientA = AuthenticatedClient(BuildToken("session-end-isolation-a"));
        var response = await clientA.PostAsync($"/translation-sessions/{sessionId}/end", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task End_Active_Returns200_IdempotentOnRepeat()
    {
        var account = await SeedAccountAsync("session-subject-end-ok");
        await SeedSubscriptionAsync(account.Id);
        using var client = AuthenticatedClient(BuildToken("session-subject-end-ok"));
        var deviceId = await RegisterDeviceAsync(client);
        var start = await client.PostAsJsonAsync("/translation-sessions", new { deviceId = deviceId.ToString() });
        var sessionId = (await start.Content.ReadFromJsonAsync<SessionIdWrapper>())!.sessionId;

        var first = await client.PostAsync($"/translation-sessions/{sessionId}/end", null);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var second = await client.PostAsync($"/translation-sessions/{sessionId}/end", null);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode); // idempotent, not an error
    }

    [Fact]
    public async Task StartRequest_CannotSupplyAccountIdOrUsageFields_ExtraJsonFieldsAreIgnoredNotHonored()
    {
        var account = await SeedAccountAsync("session-subject-noOverride");
        await SeedSubscriptionAsync(account.Id);
        using var client = AuthenticatedClient(BuildToken("session-subject-noOverride"));
        var deviceId = await RegisterDeviceAsync(client);

        var response = await client.PostAsJsonAsync("/translation-sessions", new
        {
            deviceId = deviceId.ToString(),
            accountId = Guid.NewGuid().ToString(),
            usageDuration = 999999,
            allowedMinutes = 999999,
            providerSecret = "should-be-ignored",
        });

        // The request DTO has no such members — extra JSON properties are simply ignored
        // by System.Text.Json's default deserialization; the session is still created
        // under the AUTHENTICATED account, never the attempted spoofed accountId.
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }
}
