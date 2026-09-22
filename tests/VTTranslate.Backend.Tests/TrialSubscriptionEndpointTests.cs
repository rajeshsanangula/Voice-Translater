using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
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
/// Phase 25 — HTTP-boundary tests for <c>POST /subscription/trial</c> against the real authentication /
/// account-resolution / authorization pipeline (same offline-JWT pattern as
/// <see cref="BillingCustomerEndpointsTests"/>). Entitlement only — no payment is involved.
/// </summary>
public static class TrialTestSupport
{
    public const string TestIssuer = "https://test-issuer.example/";
    public const string TestAudience = "test-audience";
    public static readonly SymmetricSecurityKey SigningKey = new(Encoding.UTF8.GetBytes("phase-25-test-signing-key-not-a-real-secret-32bytes+"));

    public static void UseOfflineJwt(IServiceCollection services) =>
        services.Configure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
        {
            options.Authority = null!;
            options.MetadataAddress = null!;
            options.RequireHttpsMetadata = false;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true, ValidIssuer = TestIssuer,
                ValidateAudience = true, ValidAudience = TestAudience,
                ValidateLifetime = true, ValidateIssuerSigningKey = true,
                IssuerSigningKey = SigningKey, ClockSkew = TimeSpan.Zero,
            };
        });

    public static string BuildToken(string subject)
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

    public static Account NewAccount(string subject) => new()
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
}

public sealed class TrialSubscriptionEndpointTests : IClassFixture<TrialSubscriptionEndpointTests.EnabledFactory>, IClassFixture<TrialSubscriptionEndpointTests.DefaultFactory>, IClassFixture<TrialSubscriptionEndpointTests.MissingPlanFactory>
{
    private static readonly Guid ConfiguredPlanId = Guid.Parse("00000000-0000-0000-0000-0000000025a1");
    private static readonly Guid MissingPlanId = Guid.Parse("00000000-0000-0000-0000-0000000025a2");

    /// <summary>Operator has enabled the Trial and named an existing, complete plan.</summary>
    public sealed class EnabledFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Subscriptions:Trial:Enabled", "true");
            builder.UseSetting("Subscriptions:Trial:PlanId", ConfiguredPlanId.ToString());
            builder.ConfigureTestServices(TrialTestSupport.UseOfflineJwt);
        }
    }

    /// <summary>No Subscriptions:Trial configuration at all — the safe default.</summary>
    public sealed class DefaultFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder.ConfigureTestServices(TrialTestSupport.UseOfflineJwt);
    }

    /// <summary>Enabled, but the configured plan id does not exist.</summary>
    public sealed class MissingPlanFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Subscriptions:Trial:Enabled", "true");
            builder.UseSetting("Subscriptions:Trial:PlanId", MissingPlanId.ToString());
            builder.ConfigureTestServices(TrialTestSupport.UseOfflineJwt);
        }
    }

    private readonly EnabledFactory _enabled;
    private readonly DefaultFactory _default;
    private readonly MissingPlanFactory _missing;

    public TrialSubscriptionEndpointTests(EnabledFactory enabled, DefaultFactory @default, MissingPlanFactory missing)
    {
        _enabled = enabled;
        _default = @default;
        _missing = missing;
        // The plan is operator-created data; seed it as an operator would (durable row in production, in-memory here).
        var plans = (InMemoryPlanRepository)_enabled.Services.GetRequiredService<IPlanRepository>();
        plans.Seed(new Plan { Id = ConfiguredPlanId, Name = "Trial (test)", IsPubliclyPurchasable = false },
        [
            new Entitlement { Id = Guid.NewGuid(), PlanId = ConfiguredPlanId, Key = EntitlementKeys.TrialDurationDays, Value = "14" },
            new Entitlement { Id = Guid.NewGuid(), PlanId = ConfiguredPlanId, Key = EntitlementKeys.TrialUsageLimitSeconds, Value = "36000" },
            new Entitlement { Id = Guid.NewGuid(), PlanId = ConfiguredPlanId, Key = EntitlementKeys.MaxActiveDevices, Value = "1" },
        ]);
    }

    private static HttpClient Client(WebApplicationFactory<Program> factory, string subject)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TrialTestSupport.BuildToken(subject));
        return client;
    }

    private static async Task<Account> SeedAccountAsync(WebApplicationFactory<Program> factory, string subject)
    {
        var account = TrialTestSupport.NewAccount(subject);
        await ((InMemoryAccountRepository)factory.Services.GetRequiredService<IAccountRepository>()).SaveAsync(account, CancellationToken.None);
        return account;
    }

    [Fact]
    public async Task Unauthenticated_Returns401()
    {
        using var client = _enabled.CreateClient();
        var response = await client.PostAsync("/subscription/trial", content: null);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AuthenticatedCustomer_StartsBoundedTrial_ThenSubscriptionEndpointShowsIt_AndRepeatIsIdempotent()
    {
        await SeedAccountAsync(_enabled, "trial-subject-1");
        using var client = Client(_enabled, "trial-subject-1");

        var created = await client.PostAsync("/subscription/trial", content: null);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var body = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Trial", body.GetProperty("status").GetString());
        Assert.Equal(ConfiguredPlanId, body.GetProperty("planId").GetGuid());
        Assert.True(body.GetProperty("created").GetBoolean());
        var start = body.GetProperty("currentPeriodStart").GetDateTimeOffset();
        var end = body.GetProperty("currentPeriodEnd").GetDateTimeOffset();
        Assert.Equal(14, Math.Round((end - start).TotalDays));

        var current = await client.GetAsync("/subscription");
        Assert.Equal(HttpStatusCode.OK, current.StatusCode);
        Assert.Equal("Trial", (await current.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());

        var again = await client.PostAsync("/subscription/trial", content: null);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        var againBody = await again.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(againBody.GetProperty("created").GetBoolean());
        Assert.Equal(end, againBody.GetProperty("currentPeriodEnd").GetDateTimeOffset());
    }

    [Fact]
    public async Task ClientSuppliedPlanStatusQuotaOrPeriod_IsIgnored_ServerDecidesEverything()
    {
        await SeedAccountAsync(_enabled, "trial-subject-override");
        using var client = Client(_enabled, "trial-subject-override");

        var hostile = JsonContent.Create(new
        {
            planId = Guid.NewGuid(),
            accountId = Guid.NewGuid(),
            status = "Active",
            currentPeriodEnd = DateTimeOffset.UtcNow.AddYears(50),
            usageLimitSecondsPerPeriod = 99999999,
            maxActiveDevices = 1000,
            billingProviderSubscriptionId = "free-forever",
        });
        var response = await client.PostAsync("/subscription/trial?planId=" + Guid.NewGuid() + "&status=Active", hostile);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Trial", body.GetProperty("status").GetString());
        Assert.Equal(ConfiguredPlanId, body.GetProperty("planId").GetGuid());
        var end = body.GetProperty("currentPeriodEnd").GetDateTimeOffset();
        Assert.True(end < DateTimeOffset.UtcNow.AddDays(15), "period must come from the plan's TrialDurationDays, not from the request");
    }

    [Fact]
    public async Task CustomerCannotProvisionAnotherAccount_OtherAccountStaysUnsubscribed()
    {
        await SeedAccountAsync(_enabled, "trial-subject-A");
        await SeedAccountAsync(_enabled, "trial-subject-B");

        using (var clientA = Client(_enabled, "trial-subject-A"))
        {
            // Attempts to smuggle B's identity in every client-controllable channel.
            var bAccount = await ((InMemoryAccountRepository)_enabled.Services.GetRequiredService<IAccountRepository>())
                .FindByExternalIdentityAsync("EntraExternalId", "trial-subject-B", CancellationToken.None);
            var response = await clientA.PostAsync($"/subscription/trial?accountId={bAccount!.Id}", JsonContent.Create(new { accountId = bAccount.Id }));
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }

        using var clientB = Client(_enabled, "trial-subject-B");
        var bView = await clientB.GetAsync("/subscription");
        Assert.Equal(HttpStatusCode.NotFound, bView.StatusCode);
    }

    [Fact]
    public async Task UnknownAccount_ValidTokenWithoutAccount_IsNotProvisioned()
    {
        using var client = Client(_enabled, "trial-subject-never-seen-no-account");
        var response = await client.PostAsync("/subscription/trial", content: null);
        Assert.NotEqual(HttpStatusCode.Created, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task NotConfigured_EndpointDoesNotExist_SafeDefault()
    {
        await SeedAccountAsync(_default, "trial-subject-default");
        using var client = Client(_default, "trial-subject-default");

        var response = await client.PostAsync("/subscription/trial", content: null);

        // No trial route exists: the request falls through to the pre-existing /subscription/{**catchAll}
        // "not implemented" placeholder (501) — never a success, and nothing is granted.
        Assert.Contains(response.StatusCode, new[] { HttpStatusCode.NotFound, HttpStatusCode.NotImplemented });
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/subscription")).StatusCode);
    }

    [Fact]
    public async Task ConfiguredPlanDoesNotExist_Returns503_AndGrantsNothing()
    {
        await SeedAccountAsync(_missing, "trial-subject-missing-plan");
        using var client = Client(_missing, "trial-subject-missing-plan");

        var response = await client.PostAsync("/subscription/trial", content: null);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/subscription")).StatusCode);
    }
}
