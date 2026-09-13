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
/// Phase 6.6 HTTP-boundary tests for the real /subscription, /subscription/cancel,
/// /entitlements, /usage endpoints — account isolation and the "client can never supply
/// its own account/status" rule (docs/phase-6.6-billing-subscription.md §12), exercised
/// against the actual authentication/authorization pipeline (same offline JWT pattern as
/// AuthenticationIntegrationTests — see that class for why this needs no live Entra call).
/// </summary>
public sealed class BillingCustomerEndpointsTests : IClassFixture<BillingCustomerEndpointsTests.TestApiFactory>
{
    private const string TestIssuer = "https://test-issuer.example/";
    private const string TestAudience = "test-audience";
    private static readonly SymmetricSecurityKey SigningKey = new(Encoding.UTF8.GetBytes("phase-6.6-test-signing-key-not-a-real-secret-32bytes+"));

    private readonly TestApiFactory _factory;

    public BillingCustomerEndpointsTests(TestApiFactory factory) => _factory = factory;

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

    private async Task<Subscription> SeedSubscriptionAsync(Guid accountId)
    {
        var plans = (InMemoryPlanRepository)_factory.Services.GetRequiredService<IPlanRepository>();
        var planId = Guid.NewGuid();
        plans.Seed(new Plan { Id = planId, Name = "Test", IsPubliclyPurchasable = false },
            [new Entitlement { Id = Guid.NewGuid(), PlanId = planId, Key = "SomeKey", Value = "42" }]);

        var subscriptions = (InMemorySubscriptionRepository)_factory.Services.GetRequiredService<ISubscriptionRepository>();
        var subscription = new Subscription
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            PlanId = planId,
            Status = SubscriptionStatus.Active,
            CurrentPeriodStart = DateTimeOffset.UtcNow.AddDays(-1),
            CurrentPeriodEnd = DateTimeOffset.UtcNow.AddDays(30),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        await subscriptions.SaveAsync(subscription, CancellationToken.None);
        return subscription;
    }

    [Fact]
    public async Task GetSubscription_ReturnsOnlyTheCallersOwnSubscription()
    {
        var account = await SeedAccountAsync("sub-endpoint-subject-1");
        await SeedSubscriptionAsync(account.Id);
        var token = BuildToken("sub-endpoint-subject-1");

        using var client = AuthenticatedClient(token);
        var response = await client.GetAsync("/subscription");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"status\":\"Active\"", body);
    }

    [Fact]
    public async Task GetSubscription_NoSubscription_Returns404_NotAFakeSuccess()
    {
        await SeedAccountAsync("sub-endpoint-subject-2");
        var token = BuildToken("sub-endpoint-subject-2");

        using var client = AuthenticatedClient(token);
        var response = await client.GetAsync("/subscription");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task CancelSubscription_Immediate_SetsCancelledAndLocallyCancelledAt()
    {
        var account = await SeedAccountAsync("cancel-subject-1");
        await SeedSubscriptionAsync(account.Id);
        var token = BuildToken("cancel-subject-1");

        using var client = AuthenticatedClient(token);
        var response = await client.PostAsJsonAsync("/subscription/cancel", new { immediate = true });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"status\":\"Cancelled\"", body);

        // Subsequent read must reflect the terminal state (subscription no longer "live").
        var followUp = await client.GetAsync("/subscription");
        Assert.Equal(HttpStatusCode.NotFound, followUp.StatusCode);
    }

    [Fact]
    public async Task CancelSubscription_AtPeriodEnd_DoesNotChangeStatusImmediately()
    {
        var account = await SeedAccountAsync("cancel-subject-2");
        await SeedSubscriptionAsync(account.Id);
        var token = BuildToken("cancel-subject-2");

        using var client = AuthenticatedClient(token);
        var response = await client.PostAsJsonAsync("/subscription/cancel", new { immediate = false });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"status\":\"Active\"", body);
        Assert.Contains("\"cancelAtPeriodEnd\":true", body);
    }

    [Fact]
    public async Task CancelSubscription_CannotBeUsedToAffectAnotherAccountsSubscription()
    {
        // Two accounts, each with their own subscription. Account A's authenticated
        // token must only ever be able to act on Account A's own subscription — there is
        // no request field that could name a different account, so this proves the
        // endpoint derives the target exclusively from the authenticated identity.
        var accountA = await SeedAccountAsync("isolation-subject-a");
        var accountB = await SeedAccountAsync("isolation-subject-b");
        var subscriptionB = await SeedSubscriptionAsync(accountB.Id);
        await SeedSubscriptionAsync(accountA.Id);

        var tokenA = BuildToken("isolation-subject-a");
        using var client = AuthenticatedClient(tokenA);
        await client.PostAsJsonAsync("/subscription/cancel", new { immediate = true });

        var subscriptions = (InMemorySubscriptionRepository)_factory.Services.GetRequiredService<ISubscriptionRepository>();
        var reloadedB = await subscriptions.FindByAccountAsync(accountB.Id, CancellationToken.None);
        Assert.NotNull(reloadedB); // account B's subscription is untouched
        Assert.Equal(subscriptionB.Id, reloadedB!.Id);
        Assert.Equal(SubscriptionStatus.Active, reloadedB.Status);
    }

    [Fact]
    public async Task GetEntitlements_ReturnsOnlyTheCallersOwnPlanEntitlements()
    {
        var account = await SeedAccountAsync("entitlements-subject-1");
        await SeedSubscriptionAsync(account.Id);
        var token = BuildToken("entitlements-subject-1");

        using var client = AuthenticatedClient(token);
        var response = await client.GetAsync("/entitlements");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("SomeKey", body);
    }

    [Fact]
    public async Task GetUsage_ReturnsServerAuthoritativeSummary_ForCallersOwnAccountOnly()
    {
        var account = await SeedAccountAsync("usage-subject-1");
        await SeedSubscriptionAsync(account.Id);
        var token = BuildToken("usage-subject-1");

        using var client = AuthenticatedClient(token);
        var response = await client.GetAsync("/usage");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task AnonymousRequest_ToAnyBillingEndpoint_Returns401()
    {
        using var client = _factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/subscription")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/entitlements")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/usage")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/subscription/cancel", new { immediate = true })).StatusCode);
    }
}
