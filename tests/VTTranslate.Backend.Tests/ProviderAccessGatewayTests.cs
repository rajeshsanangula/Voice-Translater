using VTTranslate.Backend.Application.Devices;
using VTTranslate.Backend.Application.Entitlements;
using VTTranslate.Backend.Application.ProviderAccess;
using VTTranslate.Backend.Application.Usage;
using VTTranslate.Backend.Domain.Abstractions;
using VTTranslate.Backend.Domain.Entities;
using VTTranslate.Backend.Domain.Enums;
using VTTranslate.Backend.Infrastructure.Persistence;

namespace VTTranslate.Backend.Tests;

/// <summary>Deterministic test double — no real Azure/HTTP call, no real secret. Mirrors
/// the pattern already established for FakeBillingProvider (Phase 6.6).</summary>
internal sealed class FakeProviderCredentialIssuer(Provider provider, IReadOnlySet<ProviderCapability> capabilities) : IProviderCredentialIssuer
{
    public Provider Provider => provider;
    public bool Fail { get; set; }
    public bool SupportsCapability(ProviderCapability capability) => capabilities.Contains(capability);

    public Task<IssuedProviderCredential?> IssueAsync(ProviderCapability capability, TimeSpan lifetime, CancellationToken ct) =>
        Task.FromResult(Fail || !SupportsCapability(capability)
            ? null
            : new IssuedProviderCredential("fake-short-lived-token-not-a-real-secret", "fake-region", DateTimeOffset.UtcNow.Add(lifetime)));
}

public class ProviderAccessGatewayTests
{
    private readonly InMemoryDeviceRepository _devices = new();
    private readonly InMemorySubscriptionRepository _subscriptions = new();
    private readonly InMemoryPlanRepository _plans = new();
    private readonly InMemoryUsageRecordRepository _usageRecords = new();
    private readonly InMemoryAccountRepository _accounts = new();
    private readonly InMemoryUnitOfWork _unitOfWork = new();
    private readonly InMemoryAuditEventRepository _audit = new();
    private readonly InMemoryProviderAccessRepository _grants = new();
    private readonly FakeClock _clock = new();
    private readonly DeviceRegistrationService _deviceService;
    private readonly UsageService _usageService;
    private readonly EntitlementService _entitlementService;
    private readonly FakeProviderCredentialIssuer _speechIssuer = new(Provider.AzureSpeech, new HashSet<ProviderCapability> { ProviderCapability.SpeechRecognition, ProviderCapability.SpeechSynthesis });
    private readonly ProviderAccessGateway _gateway;

    public ProviderAccessGatewayTests()
    {
        _deviceService = new DeviceRegistrationService(_devices, _subscriptions, _plans, _accounts, _unitOfWork, _audit, _clock);
        _usageService = new UsageService(_usageRecords, _clock);
        _entitlementService = new EntitlementService(_subscriptions, _plans, _deviceService, _usageService, _clock);
        _gateway = new ProviderAccessGateway(_deviceService, _entitlementService, [_speechIssuer], _grants, _audit, _unitOfWork, _clock, TimeSpan.FromMinutes(10));
    }

    private async Task<(Guid AccountId, Guid DeviceId)> SeedAsync(SubscriptionStatus status, DateTimeOffset periodEnd, IEnumerable<Entitlement>? extraEntitlements = null)
    {
        var accountId = Guid.NewGuid();
        var planId = Guid.NewGuid();
        var entitlements = new List<Entitlement> { new() { Id = Guid.NewGuid(), PlanId = planId, Key = EntitlementKeys.MaxActiveDevices, Value = "5" } };
        if (extraEntitlements is not null) entitlements.AddRange(extraEntitlements);
        _plans.Seed(new Plan { Id = planId, Name = "Test", IsPubliclyPurchasable = false }, entitlements);

        await _subscriptions.SaveAsync(new Subscription
        {
            Id = Guid.NewGuid(), AccountId = accountId, PlanId = planId, Status = status,
            CurrentPeriodStart = _clock.UtcNow.AddDays(-1), CurrentPeriodEnd = periodEnd,
        }, CancellationToken.None);

        var device = await _deviceService.RegisterDeviceAsync(accountId, DevicePlatform.Windows, "PC", CancellationToken.None);
        return (accountId, device.Id);
    }

    [Fact]
    public async Task AuthorizedRequest_Succeeds_ReturnsShortLivedCredential_NeverTheMasterSecret()
    {
        var (accountId, deviceId) = await SeedAsync(SubscriptionStatus.Active, _clock.UtcNow.AddDays(10));

        var result = await _gateway.RequestAccessAsync(accountId, deviceId, Provider.AzureSpeech, ProviderCapability.SpeechRecognition, CancellationToken.None);

        Assert.Equal(ProviderAccessOutcome.Granted, result.Outcome);
        Assert.NotNull(result.Credential);
        Assert.Equal("fake-short-lived-token-not-a-real-secret", result.Credential!.AccessToken);
        Assert.True(result.Credential.ExpiresAt > _clock.UtcNow);
        Assert.NotNull(result.CorrelationId);
    }

    [Fact]
    public async Task UnknownProvider_Denied()
    {
        var (accountId, deviceId) = await SeedAsync(SubscriptionStatus.Active, _clock.UtcNow.AddDays(10));

        var result = await _gateway.RequestAccessAsync(accountId, deviceId, Provider.AzureTranslator, ProviderCapability.TextTranslation, CancellationToken.None);

        Assert.Equal(ProviderAccessOutcome.UnsupportedProvider, result.Outcome);
        Assert.Null(result.Credential);
    }

    [Fact]
    public async Task UnsupportedCapability_Denied()
    {
        var (accountId, deviceId) = await SeedAsync(SubscriptionStatus.Active, _clock.UtcNow.AddDays(10));

        var result = await _gateway.RequestAccessAsync(accountId, deviceId, Provider.AzureSpeech, ProviderCapability.TextTranslation, CancellationToken.None);

        Assert.Equal(ProviderAccessOutcome.UnsupportedCapability, result.Outcome);
    }

    [Fact]
    public async Task MissingEntitlement_NoSubscription_Denied()
    {
        var accountId = Guid.NewGuid();
        var device = await _deviceService.RegisterDeviceAsync(accountId, DevicePlatform.Windows, "PC", CancellationToken.None);

        // No subscription seeded at all — EntitlementService's own fail-closed default applies.
        var result = await _gateway.RequestAccessAsync(accountId, device.Id, Provider.AzureSpeech, ProviderCapability.SpeechRecognition, CancellationToken.None);

        Assert.Equal(ProviderAccessOutcome.EntitlementDenied, result.Outcome);
    }

    [Fact]
    public async Task UnauthorizedDevice_UnknownId_Denied()
    {
        var (accountId, _) = await SeedAsync(SubscriptionStatus.Active, _clock.UtcNow.AddDays(10));

        var result = await _gateway.RequestAccessAsync(accountId, Guid.NewGuid(), Provider.AzureSpeech, ProviderCapability.SpeechRecognition, CancellationToken.None);

        Assert.Equal(ProviderAccessOutcome.DeviceNotAuthorized, result.Outcome);
    }

    [Fact]
    public async Task RevokedDevice_Denied()
    {
        var (accountId, deviceId) = await SeedAsync(SubscriptionStatus.Active, _clock.UtcNow.AddDays(10));
        await _deviceService.RevokeDeviceAsync(accountId, deviceId, CancellationToken.None);

        var result = await _gateway.RequestAccessAsync(accountId, deviceId, Provider.AzureSpeech, ProviderCapability.SpeechRecognition, CancellationToken.None);

        Assert.Equal(ProviderAccessOutcome.DeviceNotAuthorized, result.Outcome);
    }

    [Fact]
    public async Task SuspendedAccount_ExpiredSubscription_Denied()
    {
        // "Suspended account" itself is enforced upstream by AccountResolutionMiddleware
        // (Phase 6.4) before this gateway is ever reached — here we prove the equivalent
        // backend-authoritative denial for the closest in-scope analog: an expired
        // subscription, which EntitlementService already denies unconditionally.
        var (accountId, deviceId) = await SeedAsync(SubscriptionStatus.Expired, _clock.UtcNow.AddDays(30));

        var result = await _gateway.RequestAccessAsync(accountId, deviceId, Provider.AzureSpeech, ProviderCapability.SpeechRecognition, CancellationToken.None);

        Assert.Equal(ProviderAccessOutcome.EntitlementDenied, result.Outcome);
    }

    [Fact]
    public async Task UsageLimitReached_Denied_AsUsageDenied_NotGenericEntitlementDenied()
    {
        var (accountId, deviceId) = await SeedAsync(
            SubscriptionStatus.Active, _clock.UtcNow.AddDays(10),
            [new Entitlement { Id = Guid.NewGuid(), PlanId = Guid.Empty, Key = EntitlementKeys.UsageLimitSecondsPerPeriod, Value = "100" }]);
        await _usageService.RecordServerDerivedUsageAsync(accountId, deviceId, "en-US:de-DE", 100, "AzureSpeech", CancellationToken.None);

        var result = await _gateway.RequestAccessAsync(accountId, deviceId, Provider.AzureSpeech, ProviderCapability.SpeechRecognition, CancellationToken.None);

        Assert.Equal(ProviderAccessOutcome.UsageDenied, result.Outcome);
    }

    [Fact]
    public async Task ProviderIssuanceFailure_ReportedAsProviderUnavailable_NeverFabricatesCredential()
    {
        var (accountId, deviceId) = await SeedAsync(SubscriptionStatus.Active, _clock.UtcNow.AddDays(10));
        _speechIssuer.Fail = true;

        var result = await _gateway.RequestAccessAsync(accountId, deviceId, Provider.AzureSpeech, ProviderCapability.SpeechRecognition, CancellationToken.None);

        Assert.Equal(ProviderAccessOutcome.ProviderUnavailable, result.Outcome);
        Assert.Null(result.Credential);
    }

    [Fact]
    public async Task ClientCannotOverrideAccountId_GatewayOnlyEverActsOnTheProvidedAccountDeviceLink()
    {
        // The gateway itself has no concept of a "client-supplied" account at all — its
        // only parameter is the accountId the CALLER (the API layer, which derives it
        // exclusively from AccountResolutionMiddleware) passes in. This test proves a
        // device belonging to Account B is never authorized when queried under Account A.
        var (accountA, _) = await SeedAsync(SubscriptionStatus.Active, _clock.UtcNow.AddDays(10));
        var (accountB, deviceB) = await SeedAsync(SubscriptionStatus.Active, _clock.UtcNow.AddDays(10));

        var result = await _gateway.RequestAccessAsync(accountA, deviceB, Provider.AzureSpeech, ProviderCapability.SpeechRecognition, CancellationToken.None);

        Assert.Equal(ProviderAccessOutcome.DeviceNotAuthorized, result.Outcome);
    }

    [Fact]
    public async Task Renewal_ReRunsFullAuthorization_RevokedDeviceCannotRenew()
    {
        var (accountId, deviceId) = await SeedAsync(SubscriptionStatus.Active, _clock.UtcNow.AddDays(10));

        var first = await _gateway.RequestAccessAsync(accountId, deviceId, Provider.AzureSpeech, ProviderCapability.SpeechRecognition, CancellationToken.None);
        Assert.Equal(ProviderAccessOutcome.Granted, first.Outcome);

        await _deviceService.RevokeDeviceAsync(accountId, deviceId, CancellationToken.None);

        // "Renewal" is just calling the same endpoint/gateway again — there is no separate
        // lightweight renew path, so authorization is unconditionally re-run every time.
        var second = await _gateway.RequestAccessAsync(accountId, deviceId, Provider.AzureSpeech, ProviderCapability.SpeechRecognition, CancellationToken.None);
        Assert.Equal(ProviderAccessOutcome.DeviceNotAuthorized, second.Outcome);
    }

    [Fact]
    public async Task GrantedAccess_PersistsGrantAndAuditRecord()
    {
        var (accountId, deviceId) = await SeedAsync(SubscriptionStatus.Active, _clock.UtcNow.AddDays(10));

        var result = await _gateway.RequestAccessAsync(accountId, deviceId, Provider.AzureSpeech, ProviderCapability.SpeechRecognition, CancellationToken.None);

        Assert.Contains(_grants.Grants, g => g.AccountId == accountId && g.DeviceId == deviceId && g.CorrelationId == result.CorrelationId);
        Assert.Contains(_audit.Events, e => e.EventType == "ProviderAccessIssued" && e.AccountId == accountId);
    }

    [Fact]
    public async Task DeniedAccess_NeverPersistsAGrant_ButStillAudits()
    {
        var (accountId, deviceId) = await SeedAsync(SubscriptionStatus.Active, _clock.UtcNow.AddDays(10));

        await _gateway.RequestAccessAsync(accountId, deviceId, Provider.AzureTranslator, ProviderCapability.TextTranslation, CancellationToken.None);

        Assert.Empty(_grants.Grants);
        Assert.Contains(_audit.Events, e => e.EventType == "ProviderAccessDeniedUnsupportedProvider");
    }
}
