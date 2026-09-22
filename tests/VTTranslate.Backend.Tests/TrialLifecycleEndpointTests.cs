using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using VTTranslate.Backend.Domain.Abstractions;
using VTTranslate.Backend.Domain.Entities;
using VTTranslate.Backend.Domain.Enums;
using VTTranslate.Backend.Infrastructure.Persistence;

namespace VTTranslate.Backend.Tests;

/// <summary>
/// Phase 25B — HTTP-boundary tests of the approved trial lifecycle over the real authentication / account-resolution /
/// authorization pipeline: start, session start, exhaustion and expiry denial with machine-readable codes,
/// <c>GET /usage</c> trial reporting, one device. Entitlement only — no payment is involved.
/// </summary>
public sealed class TrialLifecycleEndpointTests : IClassFixture<TrialLifecycleEndpointTests.TrialFactory>
{
    private static readonly Guid PlanId = Guid.Parse("00000000-0000-0000-0000-0000000025b1");

    public sealed class TrialFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Subscriptions:Trial:Enabled", "true");
            builder.UseSetting("Subscriptions:Trial:PlanId", PlanId.ToString());
            builder.ConfigureTestServices(TrialTestSupport.UseOfflineJwt);
        }
    }

    private readonly TrialFactory _factory;

    public TrialLifecycleEndpointTests(TrialFactory factory)
    {
        _factory = factory;
        // The approved production trial plan, seeded as an operator would create it.
        ((InMemoryPlanRepository)_factory.Services.GetRequiredService<IPlanRepository>()).Seed(
            new Plan { Id = PlanId, Name = "Trial", IsPubliclyPurchasable = false },
            [
                new Entitlement { Id = Guid.NewGuid(), PlanId = PlanId, Key = EntitlementKeys.TrialDurationDays, Value = "14" },
                new Entitlement { Id = Guid.NewGuid(), PlanId = PlanId, Key = EntitlementKeys.TrialUsageLimitSeconds, Value = "36000" },
                new Entitlement { Id = Guid.NewGuid(), PlanId = PlanId, Key = EntitlementKeys.MaxActiveDevices, Value = "1" },
            ]);
    }

    private async Task<(HttpClient Client, Account Account)> NewCustomerAsync(string subject)
    {
        var account = TrialTestSupport.NewAccount(subject);
        await ((InMemoryAccountRepository)_factory.Services.GetRequiredService<IAccountRepository>()).SaveAsync(account, CancellationToken.None);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TrialTestSupport.BuildToken(subject));
        return (client, account);
    }

    private static async Task<Guid> RegisterDeviceAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/devices", new { platform = "Windows", displayName = "PC" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private Task AddServerUsageAsync(Guid accountId, Guid deviceId, double seconds) =>
        _factory.Services.GetRequiredService<IUsageRecordRepository>().AddAsync(new UsageRecord
        {
            Id = Guid.NewGuid(), AccountId = accountId, DeviceId = deviceId, Direction = "en-US:de-DE", SecondsUsed = seconds,
            Source = UsageRecordSource.ServerDerived, PeriodBucket = DateTimeOffset.UtcNow.ToString("yyyy-MM"), RecordedAt = DateTimeOffset.UtcNow,
        }, CancellationToken.None);

    private static Task<HttpResponseMessage> StartSessionAsync(HttpClient client, Guid deviceId) =>
        client.PostAsJsonAsync("/translation-sessions", new { deviceId = deviceId.ToString(), direction = "en-US:de-DE" });

    [Fact]
    public async Task RegisterDevice_ThenStartTrial_ThenTranslationSessionStarts()
    {
        var (client, _) = await NewCustomerAsync("life-flow");
        var deviceId = await RegisterDeviceAsync(client);                        // registered first, as the real client does

        Assert.Equal(HttpStatusCode.Created, (await client.PostAsync("/subscription/trial", null)).StatusCode);
        var session = await StartSessionAsync(client, deviceId);

        Assert.Equal(HttpStatusCode.Created, session.StatusCode);
    }

    [Fact]
    public async Task TrialAllows_OnlyOneDevice()
    {
        var (client, _) = await NewCustomerAsync("life-one-device");
        await RegisterDeviceAsync(client);
        await client.PostAsync("/subscription/trial", null);

        var second = await client.PostAsJsonAsync("/devices", new { platform = "Windows", displayName = "Second PC" });

        Assert.Equal(HttpStatusCode.Forbidden, second.StatusCode);
        Assert.Equal("device_limit_exceeded", (await second.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
    }

    [Fact]
    public async Task UsageExhausted_NewSessionDenied_WithTrialUsageExhaustedCode()
    {
        var (client, account) = await NewCustomerAsync("life-exhausted");
        var deviceId = await RegisterDeviceAsync(client);
        await client.PostAsync("/subscription/trial", null);
        await AddServerUsageAsync(account.Id, deviceId, 36000);

        var denied = await StartSessionAsync(client, deviceId);

        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        var body = await denied.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("usage_limit_exceeded", body.GetProperty("status").GetString());
        Assert.Equal("trial_usage_exhausted", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task ExpiredTrial_NewSessionDenied_WithTrialExpiredCode_AndCannotRestart_AndMyAccountShowsEnded()
    {
        var (client, account) = await NewCustomerAsync("life-expired");
        var deviceId = await RegisterDeviceAsync(client);
        await client.PostAsync("/subscription/trial", null);

        var subs = _factory.Services.GetRequiredService<ISubscriptionRepository>();
        var stored = (await subs.FindByAccountAsync(account.Id, CancellationToken.None))!;
        stored.CurrentPeriodEnd = DateTimeOffset.UtcNow.AddMinutes(-1);          // the trial period has passed

        var denied = await StartSessionAsync(client, deviceId);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        var body = await denied.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("entitlement_denied", body.GetProperty("status").GetString());
        Assert.Equal("trial_expired", body.GetProperty("code").GetString());

        // Reading the subscription reconciles the time-based transition (persisted Expired) ...
        var current = await client.GetAsync("/subscription");
        Assert.Equal("Expired", (await current.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());

        // ... it cannot be restarted ...
        var restart = await client.PostAsync("/subscription/trial", null);
        Assert.Equal(HttpStatusCode.Conflict, restart.StatusCode);
        Assert.Equal("trial_already_used", (await restart.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        // ... the denial keeps its upgrade reason once expiry is persisted ...
        var deniedAgain = await StartSessionAsync(client, deviceId);
        Assert.Equal("trial_expired", (await deniedAgain.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        // ... and My Account's data source reports an ended trial.
        var usage = (await (await client.GetAsync("/usage")).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("trial");
        Assert.True(usage.GetProperty("ended").GetBoolean());
    }

    [Fact]
    public async Task UsageEndpoint_ReportsTheTrialAllowance_NotTheMonthlyOrPaidLimit()
    {
        var (client, account) = await NewCustomerAsync("life-usage");
        var deviceId = await RegisterDeviceAsync(client);
        await client.PostAsync("/subscription/trial", null);
        await AddServerUsageAsync(account.Id, deviceId, 9000);

        var body = await (await client.GetAsync("/usage")).Content.ReadFromJsonAsync<JsonElement>();
        var trial = body.GetProperty("trial");

        Assert.Equal("Trial", trial.GetProperty("status").GetString());
        Assert.Equal(9000, trial.GetProperty("usedSeconds").GetDouble());
        Assert.Equal(36000, trial.GetProperty("limitSeconds").GetDouble());
        Assert.Equal(27000, trial.GetProperty("remainingSeconds").GetDouble());
        Assert.False(trial.GetProperty("ended").GetBoolean());
        Assert.False(trial.GetProperty("exhausted").GetBoolean());
        Assert.Equal(14, Math.Round((trial.GetProperty("periodEnd").GetDateTimeOffset() - trial.GetProperty("periodStart").GetDateTimeOffset()).TotalDays));
        Assert.Equal(9000, body.GetProperty("serverDerivedSeconds").GetDouble());  // existing monthly summary field is unchanged
    }

    [Fact]
    public async Task UsageEndpoint_HasNoTrial_ForAnAccountWithoutOne()
    {
        var (client, _) = await NewCustomerAsync("life-no-trial");

        var body = await (await client.GetAsync("/usage")).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(JsonValueKind.Null, body.GetProperty("trial").ValueKind);
    }
}
