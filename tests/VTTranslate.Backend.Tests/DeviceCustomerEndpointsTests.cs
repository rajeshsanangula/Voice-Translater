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
/// Phase 6.7 HTTP-boundary tests for the real /devices, POST /devices,
/// POST /devices/{id}/revoke endpoints — account isolation and the 404-not-403
/// non-enumeration rule (docs/phase-6.7-device-licensing-policy.md §5), using the same
/// offline-JWT pattern as AuthenticationIntegrationTests/BillingCustomerEndpointsTests.
/// </summary>
public sealed class DeviceCustomerEndpointsTests : IClassFixture<DeviceCustomerEndpointsTests.TestApiFactory>
{
    private const string TestIssuer = "https://test-issuer.example/";
    private const string TestAudience = "test-audience";
    private static readonly SymmetricSecurityKey SigningKey = new(Encoding.UTF8.GetBytes("phase-6.7-test-signing-key-not-a-real-secret-32bytes+"));

    private readonly TestApiFactory _factory;

    public DeviceCustomerEndpointsTests(TestApiFactory factory) => _factory = factory;

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

    private async Task SeedSubscriptionAsync(Guid accountId, int maxActiveDevices)
    {
        var plans = (InMemoryPlanRepository)_factory.Services.GetRequiredService<IPlanRepository>();
        var planId = Guid.NewGuid();
        plans.Seed(new Plan { Id = planId, Name = "Test", IsPubliclyPurchasable = false },
            [new Entitlement { Id = Guid.NewGuid(), PlanId = planId, Key = "MaxActiveDevices", Value = maxActiveDevices.ToString() }]);

        var subscriptions = (InMemorySubscriptionRepository)_factory.Services.GetRequiredService<ISubscriptionRepository>();
        await subscriptions.SaveAsync(new Subscription
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            PlanId = planId,
            Status = SubscriptionStatus.Active,
            CurrentPeriodStart = DateTimeOffset.UtcNow.AddDays(-1),
            CurrentPeriodEnd = DateTimeOffset.UtcNow.AddDays(30),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        }, CancellationToken.None);
    }

    [Fact]
    public async Task GetDevices_EmptyForNewAccount_ReturnsEmptyArray_Not404()
    {
        await SeedAccountAsync("devices-subject-1");
        var token = BuildToken("devices-subject-1");

        using var client = AuthenticatedClient(token);
        var response = await client.GetAsync("/devices");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal("[]", body);
    }

    [Fact]
    public async Task PostDevices_ValidPlatform_Returns201_ServerGeneratedId()
    {
        var account = await SeedAccountAsync("devices-subject-2");
        await SeedSubscriptionAsync(account.Id, maxActiveDevices: 2);
        var token = BuildToken("devices-subject-2");

        using var client = AuthenticatedClient(token);
        var response = await client.PostAsJsonAsync("/devices", new { platform = "Windows", displayName = "My PC" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"status\":\"Authorized\"", body);
        Assert.Contains("\"platform\":\"Windows\"", body);
    }

    [Fact]
    public async Task PostDevices_InvalidPlatform_Returns400()
    {
        await SeedAccountAsync("devices-subject-3");
        var token = BuildToken("devices-subject-3");

        using var client = AuthenticatedClient(token);
        var response = await client.PostAsJsonAsync("/devices", new { platform = "PlayStation" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PostDevices_OverLimit_Returns403()
    {
        var account = await SeedAccountAsync("devices-subject-4");
        await SeedSubscriptionAsync(account.Id, maxActiveDevices: 1);
        var token = BuildToken("devices-subject-4");

        using var client = AuthenticatedClient(token);
        await client.PostAsJsonAsync("/devices", new { platform = "Windows" });
        var second = await client.PostAsJsonAsync("/devices", new { platform = "Windows" });

        Assert.Equal(HttpStatusCode.Forbidden, second.StatusCode);
        var body = await second.Content.ReadAsStringAsync();
        Assert.Contains("device_limit_exceeded", body);
    }

    [Fact]
    public async Task RevokeOwnDevice_Returns200_Idempotent()
    {
        var account = await SeedAccountAsync("devices-subject-5");
        await SeedSubscriptionAsync(account.Id, maxActiveDevices: 2);
        var token = BuildToken("devices-subject-5");

        using var client = AuthenticatedClient(token);
        var created = await client.PostAsJsonAsync("/devices", new { platform = "Windows" });
        var createdBody = await created.Content.ReadFromJsonAsync<JsonElementWrapper>();
        var deviceId = createdBody!.id;

        var first = await client.PostAsync($"/devices/{deviceId}/revoke", null);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var second = await client.PostAsync($"/devices/{deviceId}/revoke", null);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode); // idempotent, not an error
    }

    [Fact]
    public async Task RevokeNonexistentDevice_Returns404()
    {
        await SeedAccountAsync("devices-subject-6");
        var token = BuildToken("devices-subject-6");

        using var client = AuthenticatedClient(token);
        var response = await client.PostAsync($"/devices/{Guid.NewGuid()}/revoke", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task RevokeAnotherAccountsDevice_Returns404_NotAcccessibleOrDistinguishable()
    {
        var accountA = await SeedAccountAsync("devices-isolation-a");
        var accountB = await SeedAccountAsync("devices-isolation-b");
        await SeedSubscriptionAsync(accountA.Id, maxActiveDevices: 2);
        await SeedSubscriptionAsync(accountB.Id, maxActiveDevices: 2);

        var tokenB = BuildToken("devices-isolation-b");
        using var clientB = AuthenticatedClient(tokenB);
        var createdByB = await clientB.PostAsJsonAsync("/devices", new { platform = "Windows" });
        var createdBody = await createdByB.Content.ReadFromJsonAsync<JsonElementWrapper>();
        var deviceIdOwnedByB = createdBody!.id;

        var tokenA = BuildToken("devices-isolation-a");
        using var clientA = AuthenticatedClient(tokenA);
        var response = await clientA.PostAsync($"/devices/{deviceIdOwnedByB}/revoke", null);

        // Same 404 as a genuinely nonexistent ID — never a 403 that would confirm the ID exists.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        // Account B's device must remain untouched.
        var devicesRepo = (InMemoryDeviceRepository)_factory.Services.GetRequiredService<IDeviceRepository>();
        var stillOwnedByB = await devicesRepo.FindByIdAsync(deviceIdOwnedByB, CancellationToken.None);
        Assert.Equal(DeviceStatus.Authorized, stillOwnedByB!.Status);
    }

    [Fact]
    public async Task GetDevices_NeverReturnsAnotherAccountsDevices()
    {
        var accountA = await SeedAccountAsync("devices-list-isolation-a");
        var accountB = await SeedAccountAsync("devices-list-isolation-b");
        await SeedSubscriptionAsync(accountA.Id, maxActiveDevices: 2);
        await SeedSubscriptionAsync(accountB.Id, maxActiveDevices: 2);

        using (var clientB = AuthenticatedClient(BuildToken("devices-list-isolation-b")))
            await clientB.PostAsJsonAsync("/devices", new { platform = "Windows" });

        using var clientA = AuthenticatedClient(BuildToken("devices-list-isolation-a"));
        var response = await clientA.GetAsync("/devices");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal("[]", body); // Account A sees none of Account B's devices
    }

    [Fact]
    public async Task AnonymousRequest_ToAnyDeviceEndpoint_Returns401()
    {
        using var client = _factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/devices")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/devices", new { platform = "Windows" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync($"/devices/{Guid.NewGuid()}/revoke", null)).StatusCode);
    }

    private sealed class JsonElementWrapper
    {
        public Guid id { get; set; }
    }
}
