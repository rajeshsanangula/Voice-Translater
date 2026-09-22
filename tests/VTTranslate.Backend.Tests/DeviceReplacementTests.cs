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
using VTTranslate.Backend.Application.Devices;
using VTTranslate.Backend.Domain;
using VTTranslate.Backend.Domain.Abstractions;
using VTTranslate.Backend.Domain.Entities;
using VTTranslate.Backend.Domain.Enums;
using VTTranslate.Backend.Infrastructure.Persistence;

namespace VTTranslate.Backend.Tests;

/// <summary>
/// Phase 25E — the atomic, explicit "replace my device" operation
/// (<see cref="DeviceRegistrationService.ReplaceDeviceAsync"/> and <c>POST /devices/replace</c>). Fixes the Phase 25C
/// gap (MaxActiveDevices=1, old device still Authorized, no way to register on a new machine) without increasing
/// the limit, without accepting a client-supplied account or device id, and without weakening MaxActiveDevices.
/// </summary>
public sealed class DeviceReplacementServiceTests
{
    private readonly InMemoryDeviceRepository _devices = new();
    private readonly InMemorySubscriptionRepository _subscriptions = new();
    private readonly InMemoryPlanRepository _plans = new();
    private readonly InMemoryAccountRepository _accounts = new();
    private readonly InMemoryUnitOfWork _unitOfWork = new();
    private readonly InMemoryAuditEventRepository _audit = new();
    private readonly FakeClock _clock = new();
    private readonly DeviceRegistrationService _service;

    public DeviceReplacementServiceTests() =>
        _service = new DeviceRegistrationService(_devices, _subscriptions, _plans, _accounts, _unitOfWork, _audit, _clock);

    private async Task<Guid> SeedAccountWithPlanAsync(int maxActiveDevices)
    {
        var accountId = Guid.NewGuid();
        var planId = Guid.NewGuid();
        _plans.Seed(new Plan { Id = planId, Name = "Test", IsPubliclyPurchasable = false },
            [new Entitlement { Id = Guid.NewGuid(), PlanId = planId, Key = EntitlementKeys.MaxActiveDevices, Value = maxActiveDevices.ToString() }]);
        await _subscriptions.SaveAsync(new Subscription
        {
            Id = Guid.NewGuid(), AccountId = accountId, PlanId = planId, Status = SubscriptionStatus.Active,
            CurrentPeriodStart = _clock.UtcNow, CurrentPeriodEnd = _clock.UtcNow.AddDays(30),
        }, CancellationToken.None);
        return accountId;
    }

    [Fact]
    public async Task Replace_OldDeviceStillAuthorized_RevokesItAndRegistersNew_ExactlyOneLiveDevice()
    {
        var accountId = await SeedAccountWithPlanAsync(maxActiveDevices: 1);
        var oldDevice = await _service.RegisterDeviceAsync(accountId, DevicePlatform.Windows, "Old PC", CancellationToken.None);

        var newDevice = await _service.ReplaceDeviceAsync(accountId, DevicePlatform.Windows, "New PC", CancellationToken.None);

        var all = await _service.ListDevicesAsync(accountId, CancellationToken.None);
        Assert.Equal(2, all.Count); // history preserved, not deleted
        var storedOld = all.Single(d => d.Id == oldDevice.Id);
        Assert.Equal(DeviceStatus.Revoked, storedOld.Status);
        Assert.NotNull(storedOld.RevokedAt);
        var storedNew = all.Single(d => d.Id == newDevice.Id);
        Assert.Equal(DeviceStatus.Authorized, storedNew.Status);
        Assert.Equal(_clock.UtcNow, storedNew.RegisteredAt);
        Assert.Equal(_clock.UtcNow, storedNew.LastSeenAt);
        Assert.Equal(1, all.Count(d => d.Status != DeviceStatus.Revoked));

        Assert.False(await _service.IsDeviceAuthorizedAsync(accountId, oldDevice.Id, CancellationToken.None));
        Assert.True(await _service.IsDeviceAuthorizedAsync(accountId, newDevice.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Replace_WithNoExistingDevice_StillSucceeds()
    {
        var accountId = await SeedAccountWithPlanAsync(maxActiveDevices: 1);

        var device = await _service.ReplaceDeviceAsync(accountId, DevicePlatform.Windows, "First PC", CancellationToken.None);

        Assert.Equal(DeviceStatus.Authorized, device.Status);
        Assert.Single(await _service.ListDevicesAsync(accountId, CancellationToken.None));
    }

    [Fact]
    public async Task Replace_RevokesEveryLiveDevice_NotJustOne_ForAPooledMultiDeviceAccount()
    {
        // Documents the deliberate "replace my whole current device set" semantics (not a per-device operation) —
        // safe today because it is the only interpretation an explicit customer-initiated replacement can have
        // without a client-supplied device id to distinguish "which one."
        var accountId = await SeedAccountWithPlanAsync(maxActiveDevices: 3);
        var a = await _service.RegisterDeviceAsync(accountId, DevicePlatform.Windows, "A", CancellationToken.None);
        var b = await _service.RegisterDeviceAsync(accountId, DevicePlatform.Windows, "B", CancellationToken.None);

        var replacement = await _service.ReplaceDeviceAsync(accountId, DevicePlatform.Windows, "C", CancellationToken.None);

        var all = await _service.ListDevicesAsync(accountId, CancellationToken.None);
        Assert.Equal(DeviceStatus.Revoked, all.Single(d => d.Id == a.Id).Status);
        Assert.Equal(DeviceStatus.Revoked, all.Single(d => d.Id == b.Id).Status);
        Assert.Equal(DeviceStatus.Authorized, all.Single(d => d.Id == replacement.Id).Status);
        Assert.Equal(1, all.Count(d => d.Status != DeviceStatus.Revoked));
    }

    [Fact]
    public async Task Replace_NeverBypassesAZeroDeviceLimit()
    {
        var accountId = await SeedAccountWithPlanAsync(maxActiveDevices: 0);

        await Assert.ThrowsAsync<DeviceLimitExceededException>(() =>
            _service.ReplaceDeviceAsync(accountId, DevicePlatform.Windows, "New PC", CancellationToken.None));

        Assert.Empty(await _service.ListDevicesAsync(accountId, CancellationToken.None));
    }

    [Fact]
    public async Task Replace_WithAlreadyRevokedDevicePresent_LeavesItRevoked_AndStillSucceeds()
    {
        var accountId = await SeedAccountWithPlanAsync(maxActiveDevices: 1);
        var old = await _service.RegisterDeviceAsync(accountId, DevicePlatform.Windows, "Old", CancellationToken.None);
        await _service.RevokeDeviceAsync(accountId, old.Id, CancellationToken.None);
        var revokedAtBefore = (await _service.ListDevicesAsync(accountId, CancellationToken.None)).Single().RevokedAt;

        var replacement = await _service.ReplaceDeviceAsync(accountId, DevicePlatform.Windows, "New", CancellationToken.None);

        var stored = (await _service.ListDevicesAsync(accountId, CancellationToken.None)).Single(d => d.Id == old.Id);
        Assert.Equal(revokedAtBefore, stored.RevokedAt); // untouched — already revoked, not re-audited
        Assert.Equal(DeviceStatus.Authorized, replacement.Status);
    }

    [Fact]
    public async Task Replace_OnlyEverAffectsTheNamedAccount()
    {
        var accountA = await SeedAccountWithPlanAsync(maxActiveDevices: 1);
        var accountB = await SeedAccountWithPlanAsync(maxActiveDevices: 1);
        var deviceA = await _service.RegisterDeviceAsync(accountA, DevicePlatform.Windows, "A", CancellationToken.None);
        var deviceB = await _service.RegisterDeviceAsync(accountB, DevicePlatform.Windows, "B", CancellationToken.None);

        await _service.ReplaceDeviceAsync(accountA, DevicePlatform.Windows, "A2", CancellationToken.None);

        Assert.Equal(DeviceStatus.Revoked, (await _service.ListDevicesAsync(accountA, CancellationToken.None)).Single(d => d.Id == deviceA.Id).Status);
        Assert.Equal(DeviceStatus.Authorized, (await _service.ListDevicesAsync(accountB, CancellationToken.None)).Single(d => d.Id == deviceB.Id).Status); // B untouched
    }

    [Fact]
    public async Task Replace_Audits_TheRevocationAndTheNewRegistration()
    {
        var accountId = await SeedAccountWithPlanAsync(maxActiveDevices: 1);
        var old = await _service.RegisterDeviceAsync(accountId, DevicePlatform.Windows, "Old", CancellationToken.None);

        await _service.ReplaceDeviceAsync(accountId, DevicePlatform.Windows, "New", CancellationToken.None);

        Assert.Contains(_audit.Events, e => e.EventType == "DeviceRevoked" && e.Metadata!.Contains(old.Id.ToString()) && e.Metadata.Contains("replaced"));
        Assert.Contains(_audit.Events, e => e.EventType == "DeviceRegistered" && e.Metadata!.Contains("replacedCount=1"));
    }

    // NOTE: there is deliberately no in-memory "concurrent replace yields exactly one live device" test here.
    // Concurrency/transaction serialization is an integration property of the PostgreSQL repository (the real
    // account-row lock plus the partial unique index) and is verified by the Testcontainers tests in
    // DeviceReplacementPostgresTests (A/B/E). InMemoryAccountRepository.LockAccountForDeviceRegistrationAsync is a
    // documented no-op — the in-memory repository intentionally does not emulate database row locks — so asserting
    // "exactly one survivor" against real parallel Task.Run callers here would be testing a guarantee this double
    // cannot provide, not a property of DeviceRegistrationService.ReplaceDeviceAsync itself.

    [Fact]
    public async Task NormalRegistration_StillEnforcesTheLimit_Unchanged()
    {
        // Regression: ReplaceDeviceAsync must not have altered RegisterDeviceAsync's own behavior.
        var accountId = await SeedAccountWithPlanAsync(maxActiveDevices: 1);
        await _service.RegisterDeviceAsync(accountId, DevicePlatform.Windows, "Only", CancellationToken.None);

        await Assert.ThrowsAsync<DeviceLimitExceededException>(() =>
            _service.RegisterDeviceAsync(accountId, DevicePlatform.Windows, "Second", CancellationToken.None));
    }

    [Fact]
    public async Task PaidPlan_MultiDevice_NormalRegistrationUnaffectedByReplacementExisting()
    {
        var accountId = await SeedAccountWithPlanAsync(maxActiveDevices: 2);
        await _service.RegisterDeviceAsync(accountId, DevicePlatform.Windows, "One", CancellationToken.None);

        var second = await _service.RegisterDeviceAsync(accountId, DevicePlatform.Windows, "Two", CancellationToken.None);

        Assert.Equal(DeviceStatus.Authorized, second.Status);
        Assert.Equal(2, (await _service.ListDevicesAsync(accountId, CancellationToken.None)).Count(d => d.Status != DeviceStatus.Revoked));
    }
}

/// <summary>
/// Phase 25E — a deliberately self-contained offline-JWT helper (same pattern as <see cref="BillingCustomerEndpointsTests"/>'s
/// own, duplicated rather than shared with any Phase 25/25B test-only type, so this file has no compile-time dependency
/// on anything outside the approved Phase 25E file set).
/// </summary>
internal static class DeviceReplacementTestAuth
{
    private const string TestIssuer = "https://test-issuer.example/";
    private const string TestAudience = "test-audience";
    private static readonly SymmetricSecurityKey SigningKey = new(Encoding.UTF8.GetBytes("phase-25e-test-signing-key-not-a-real-secret-32bytes+"));

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

/// <summary>Phase 25E — HTTP-boundary security tests for <c>POST /devices/replace</c> over the real authentication /
/// account-resolution pipeline (same offline-JWT pattern as <see cref="BillingCustomerEndpointsTests"/>).</summary>
public sealed class DeviceReplacementEndpointTests : IClassFixture<DeviceReplacementEndpointTests.TestApiFactory>
{
    private readonly TestApiFactory _factory;
    public DeviceReplacementEndpointTests(TestApiFactory factory) => _factory = factory;

    public sealed class TestApiFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.ConfigureTestServices(DeviceReplacementTestAuth.UseOfflineJwt);
    }

    private HttpClient Client(string subject)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", DeviceReplacementTestAuth.BuildToken(subject));
        return client;
    }

    private async Task<Account> SeedAccountWithPlanAsync(string subject, int maxActiveDevices)
    {
        var account = DeviceReplacementTestAuth.NewAccount(subject);
        await ((InMemoryAccountRepository)_factory.Services.GetRequiredService<IAccountRepository>()).SaveAsync(account, CancellationToken.None);
        var planId = Guid.NewGuid();
        ((InMemoryPlanRepository)_factory.Services.GetRequiredService<IPlanRepository>()).Seed(
            new Plan { Id = planId, Name = "Test", IsPubliclyPurchasable = false },
            [new Entitlement { Id = Guid.NewGuid(), PlanId = planId, Key = EntitlementKeys.MaxActiveDevices, Value = maxActiveDevices.ToString() }]);
        await ((InMemorySubscriptionRepository)_factory.Services.GetRequiredService<ISubscriptionRepository>()).SaveAsync(new Subscription
        {
            Id = Guid.NewGuid(), AccountId = account.Id, PlanId = planId, Status = SubscriptionStatus.Active,
            CurrentPeriodStart = DateTimeOffset.UtcNow.AddDays(-1), CurrentPeriodEnd = DateTimeOffset.UtcNow.AddDays(30),
        }, CancellationToken.None);
        return account;
    }

    [Fact]
    public async Task Unauthenticated_Returns401()
    {
        var response = await _factory.CreateClient().PostAsJsonAsync("/devices/replace", new { platform = "Windows" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task SameAccount_Replacement_Succeeds_ExactlyOneLiveDeviceAfterward()
    {
        var account = await SeedAccountWithPlanAsync("replace-endpoint-1", maxActiveDevices: 1);
        using var client = Client("replace-endpoint-1");
        var reg = await client.PostAsJsonAsync("/devices", new { platform = "Windows", displayName = "Old" });
        var oldId = (await reg.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var response = await client.PostAsJsonAsync("/devices/replace", new { platform = "Windows", displayName = "New" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Authorized", body.GetProperty("status").GetString());
        var newId = body.GetProperty("id").GetGuid();
        Assert.NotEqual(oldId, newId);

        var devices = await (await client.GetAsync("/devices")).Content.ReadFromJsonAsync<JsonElement>();
        var list = devices.EnumerateArray().ToList();
        Assert.Equal(2, list.Count);
        Assert.Equal("Revoked", list.Single(d => d.GetProperty("id").GetGuid() == oldId).GetProperty("status").GetString());
        Assert.Equal("Authorized", list.Single(d => d.GetProperty("id").GetGuid() == newId).GetProperty("status").GetString());
    }

    [Fact]
    public async Task CrossAccount_CannotReplaceOrRevokeAnotherAccountsDevice()
    {
        var victim = await SeedAccountWithPlanAsync("replace-victim", maxActiveDevices: 1);
        using var victimClient = Client("replace-victim");
        var reg = await victimClient.PostAsJsonAsync("/devices", new { platform = "Windows", displayName = "Victim device" });
        var victimDeviceId = (await reg.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        await SeedAccountWithPlanAsync("replace-attacker", maxActiveDevices: 1);
        using var attackerClient = Client("replace-attacker");

        // The attacker calls their OWN replace endpoint (no device id is ever accepted client-side) — this must
        // never touch the victim's device no matter what.
        var response = await attackerClient.PostAsJsonAsync("/devices/replace", new { platform = "Windows", displayName = "Attacker device" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var victimDevices = await (await victimClient.GetAsync("/devices")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Authorized", victimDevices.EnumerateArray().Single(d => d.GetProperty("id").GetGuid() == victimDeviceId).GetProperty("status").GetString());
    }

    [Fact]
    public async Task ClientSuppliedAccountIdOrDeviceId_IsIgnored_ServerOnlyEverActsOnTheAuthenticatedAccount()
    {
        var account = await SeedAccountWithPlanAsync("replace-hostile", maxActiveDevices: 1);
        using var client = Client("replace-hostile");
        var reg = await client.PostAsJsonAsync("/devices", new { platform = "Windows", displayName = "Old" });
        var oldId = (await reg.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        // Hostile payload: an arbitrary accountId and an arbitrary (unrelated, made-up) deviceId. Neither field
        // exists on the request contract, so this proves the endpoint has nothing to read them from even if it tried.
        var hostile = JsonContent.Create(new { platform = "Windows", displayName = "New", accountId = Guid.NewGuid(), deviceId = Guid.NewGuid() });
        var response = await client.PostAsync("/devices/replace?accountId=" + Guid.NewGuid(), hostile);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var devices = await (await client.GetAsync("/devices")).Content.ReadFromJsonAsync<JsonElement>();
        var list = devices.EnumerateArray().ToList();
        Assert.Equal(2, list.Count); // only THIS account's old device + the new one — nothing from the hostile ids
        Assert.Equal("Revoked", list.Single(d => d.GetProperty("id").GetGuid() == oldId).GetProperty("status").GetString());
    }

    [Fact]
    public async Task NormalDeviceRegistration_StillEnforcesMaxActiveDevices_Regression()
    {
        var account = await SeedAccountWithPlanAsync("replace-regression-limit", maxActiveDevices: 1);
        using var client = Client("replace-regression-limit");
        await client.PostAsJsonAsync("/devices", new { platform = "Windows", displayName = "Only" });

        var second = await client.PostAsJsonAsync("/devices", new { platform = "Windows", displayName = "Second" });

        Assert.Equal(HttpStatusCode.Forbidden, second.StatusCode);
        Assert.Equal("device_limit_exceeded", (await second.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
    }

    [Fact]
    public async Task PaidMultiDevicePlan_NormalRegistrationBehaviorUnaffected_Regression()
    {
        var account = await SeedAccountWithPlanAsync("replace-regression-multi", maxActiveDevices: 3);
        using var client = Client("replace-regression-multi");

        var first = await client.PostAsJsonAsync("/devices", new { platform = "Windows", displayName = "One" });
        var second = await client.PostAsJsonAsync("/devices", new { platform = "Windows", displayName = "Two" });

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        var devices = await (await client.GetAsync("/devices")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, devices.EnumerateArray().Count(d => d.GetProperty("status").GetString() == "Authorized"));
    }

    [Fact]
    public async Task DeviceNotAuthorizedSessionRecovery_PathIsUnrelatedAndUnchanged_Regression()
    {
        // Confirms /devices/replace introduces no new route collision or behavior change on the session-start
        // device-authorization check the client's OWN recovery (ExecuteWithDeviceRecoveryAsync) depends on.
        var account = await SeedAccountWithPlanAsync("replace-regression-session", maxActiveDevices: 1);
        using var client = Client("replace-regression-session");
        var reg = await client.PostAsJsonAsync("/devices", new { platform = "Windows", displayName = "Device" });
        var deviceId = (await reg.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        await client.PostAsync($"/devices/{deviceId}/revoke", null);

        var session = await client.PostAsJsonAsync("/translation-sessions", new { deviceId = deviceId.ToString(), direction = "en-US:de-DE" });

        Assert.Equal(HttpStatusCode.Forbidden, session.StatusCode);
        Assert.Equal("device_not_authorized", (await session.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
    }
}
