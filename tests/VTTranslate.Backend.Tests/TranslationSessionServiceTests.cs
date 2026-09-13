using VTTranslate.Backend.Application.Devices;
using VTTranslate.Backend.Application.Entitlements;
using VTTranslate.Backend.Application.Sessions;
using VTTranslate.Backend.Application.Usage;
using VTTranslate.Backend.Domain.Entities;
using VTTranslate.Backend.Domain.Enums;
using VTTranslate.Backend.Infrastructure.Persistence;

namespace VTTranslate.Backend.Tests;

public class TranslationSessionServiceTests
{
    private readonly InMemoryDeviceRepository _devices = new();
    private readonly InMemorySubscriptionRepository _subscriptions = new();
    private readonly InMemoryPlanRepository _plans = new();
    private readonly InMemoryUsageRecordRepository _usageRecords = new();
    private readonly InMemoryAccountRepository _accounts = new();
    private readonly InMemoryUnitOfWork _unitOfWork = new();
    private readonly InMemoryAuditEventRepository _audit = new();
    private readonly InMemoryTranslationSessionRepository _sessions = new();
    private readonly FakeClock _clock = new();
    private readonly DeviceRegistrationService _deviceService;
    private readonly UsageService _usageService;
    private readonly EntitlementService _entitlementService;
    private readonly TranslationSessionService _service;
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(60);

    public TranslationSessionServiceTests()
    {
        _deviceService = new DeviceRegistrationService(_devices, _subscriptions, _plans, _accounts, _unitOfWork, _audit, _clock);
        _usageService = new UsageService(_usageRecords, _clock);
        _entitlementService = new EntitlementService(_subscriptions, _plans, _deviceService, _usageService, _clock);
        _service = new TranslationSessionService(_deviceService, _entitlementService, _sessions, _accounts, _usageService, _audit, _unitOfWork, _clock, Lease);
    }

    private async Task<(Guid AccountId, Guid DeviceId)> SeedAsync(SubscriptionStatus status = SubscriptionStatus.Active, IEnumerable<Entitlement>? extra = null)
    {
        var accountId = Guid.NewGuid();
        var planId = Guid.NewGuid();
        var entitlements = new List<Entitlement> { new() { Id = Guid.NewGuid(), PlanId = planId, Key = EntitlementKeys.MaxActiveDevices, Value = "5" } };
        if (extra is not null) entitlements.AddRange(extra);
        _plans.Seed(new Plan { Id = planId, Name = "Test", IsPubliclyPurchasable = false }, entitlements);
        await _subscriptions.SaveAsync(new Subscription { Id = Guid.NewGuid(), AccountId = accountId, PlanId = planId, Status = status, CurrentPeriodStart = _clock.UtcNow.AddDays(-1), CurrentPeriodEnd = _clock.UtcNow.AddDays(30) }, CancellationToken.None);
        var device = await _deviceService.RegisterDeviceAsync(accountId, DevicePlatform.Windows, "PC", CancellationToken.None);
        return (accountId, device.Id);
    }

    // ---- A. Session start ----

    [Fact]
    public async Task Start_Authorized_Succeeds()
    {
        var (accountId, deviceId) = await SeedAsync();
        var result = await _service.StartSessionAsync(accountId, deviceId, null, "en-US:de-DE", CancellationToken.None);

        Assert.Equal(SessionStartOutcome.Started, result.Outcome);
        Assert.Equal(TranslationSessionState.Active, result.Session!.State);
    }

    [Fact]
    public async Task Start_NonexistentDevice_Denied()
    {
        var (accountId, _) = await SeedAsync();
        var result = await _service.StartSessionAsync(accountId, Guid.NewGuid(), null, null, CancellationToken.None);
        Assert.Equal(SessionStartOutcome.DeviceNotAuthorized, result.Outcome);
    }

    [Fact]
    public async Task Start_CrossAccountDevice_Denied()
    {
        var (accountA, _) = await SeedAsync();
        var (_, deviceB) = await SeedAsync();
        var result = await _service.StartSessionAsync(accountA, deviceB, null, null, CancellationToken.None);
        Assert.Equal(SessionStartOutcome.DeviceNotAuthorized, result.Outcome);
    }

    [Fact]
    public async Task Start_RevokedDevice_Denied()
    {
        var (accountId, deviceId) = await SeedAsync();
        await _deviceService.RevokeDeviceAsync(accountId, deviceId, CancellationToken.None);
        var result = await _service.StartSessionAsync(accountId, deviceId, null, null, CancellationToken.None);
        Assert.Equal(SessionStartOutcome.DeviceNotAuthorized, result.Outcome);
    }

    [Fact]
    public async Task Start_NoSubscription_EntitlementDenied()
    {
        var accountId = Guid.NewGuid();
        var device = await _deviceService.RegisterDeviceAsync(accountId, DevicePlatform.Windows, "PC", CancellationToken.None);
        var result = await _service.StartSessionAsync(accountId, device.Id, null, null, CancellationToken.None);
        Assert.Equal(SessionStartOutcome.EntitlementDenied, result.Outcome);
    }

    [Fact]
    public async Task Start_UsageLimitReached_UsageDenied()
    {
        var (accountId, deviceId) = await SeedAsync(extra: [new Entitlement { Id = Guid.NewGuid(), PlanId = Guid.Empty, Key = EntitlementKeys.UsageLimitSecondsPerPeriod, Value = "10" }]);
        await _usageService.RecordServerDerivedUsageAsync(accountId, deviceId, "x", 10, null, CancellationToken.None);

        var result = await _service.StartSessionAsync(accountId, deviceId, null, null, CancellationToken.None);
        Assert.Equal(SessionStartOutcome.UsageDenied, result.Outcome);
    }

    // ---- B/F. Idempotency and reconnect ----

    [Fact]
    public async Task Start_DuplicateClientSessionId_ReturnsSameActiveSession_NotANewOne()
    {
        var (accountId, deviceId) = await SeedAsync();
        var first = await _service.StartSessionAsync(accountId, deviceId, "client-1", null, CancellationToken.None);
        var second = await _service.StartSessionAsync(accountId, deviceId, "client-1", null, CancellationToken.None);

        Assert.Equal(SessionStartOutcome.Started, first.Outcome);
        Assert.Equal(SessionStartOutcome.Resumed, second.Outcome);
        Assert.Equal(first.Session!.Id, second.Session!.Id);
    }

    [Fact]
    public async Task Reconnect_AfterExpiry_CreatesGenuinelyNewSession()
    {
        var (accountId, deviceId) = await SeedAsync();
        var first = await _service.StartSessionAsync(accountId, deviceId, "client-2", null, CancellationToken.None);

        _clock.UtcNow = _clock.UtcNow.Add(Lease).AddSeconds(5); // past lease, no heartbeat
        var second = await _service.StartSessionAsync(accountId, deviceId, "client-2", null, CancellationToken.None);

        Assert.Equal(SessionStartOutcome.Started, second.Outcome);
        Assert.NotEqual(first.Session!.Id, second.Session!.Id);
    }

    // ---- C. Heartbeat ----

    [Fact]
    public async Task Heartbeat_Active_Succeeds_UpdatesActivity()
    {
        var (accountId, deviceId) = await SeedAsync();
        var start = await _service.StartSessionAsync(accountId, deviceId, null, null, CancellationToken.None);

        _clock.UtcNow = _clock.UtcNow.AddSeconds(10);
        var hb = await _service.HeartbeatAsync(accountId, start.Session!.Id, CancellationToken.None);

        Assert.Equal(SessionOperationOutcome.Success, hb.Outcome);
        Assert.Equal(_clock.UtcNow, hb.Session!.LastActivityAt);
    }

    [Fact]
    public async Task Heartbeat_AfterEnd_RejectedSafely()
    {
        var (accountId, deviceId) = await SeedAsync();
        var start = await _service.StartSessionAsync(accountId, deviceId, null, null, CancellationToken.None);
        await _service.EndAsync(accountId, start.Session!.Id, CancellationToken.None);

        var hb = await _service.HeartbeatAsync(accountId, start.Session.Id, CancellationToken.None);
        Assert.Equal(SessionOperationOutcome.AlreadyTerminal, hb.Outcome);
    }

    [Fact]
    public async Task Heartbeat_AfterExpiry_NeverRevives()
    {
        var (accountId, deviceId) = await SeedAsync();
        var start = await _service.StartSessionAsync(accountId, deviceId, null, null, CancellationToken.None);

        _clock.UtcNow = _clock.UtcNow.Add(Lease).AddSeconds(5);
        var hb = await _service.HeartbeatAsync(accountId, start.Session!.Id, CancellationToken.None);

        Assert.Equal(SessionOperationOutcome.AlreadyTerminal, hb.Outcome);
        Assert.Equal(TranslationSessionState.Expired, hb.Session!.State);
    }

    [Fact]
    public async Task Heartbeat_CrossAccount_Denied()
    {
        var (accountA, _) = await SeedAsync();
        var (accountB, deviceB) = await SeedAsync();
        var start = await _service.StartSessionAsync(accountB, deviceB, null, null, CancellationToken.None);

        var hb = await _service.HeartbeatAsync(accountA, start.Session!.Id, CancellationToken.None);
        Assert.Equal(SessionOperationOutcome.NotFound, hb.Outcome);
    }

    // ---- D. End ----

    [Fact]
    public async Task End_Active_CalculatesServerSideDuration_PersistsUsage()
    {
        var (accountId, deviceId) = await SeedAsync();
        var start = await _service.StartSessionAsync(accountId, deviceId, null, "en-US:de-DE", CancellationToken.None);

        _clock.UtcNow = _clock.UtcNow.AddSeconds(42);
        var end = await _service.EndAsync(accountId, start.Session!.Id, CancellationToken.None);

        Assert.Equal(SessionOperationOutcome.Success, end.Outcome);
        Assert.Equal(TranslationSessionState.Ended, end.Session!.State);

        var authoritative = await _usageService.GetAuthoritativeUsageSecondsAsync(accountId, _clock.UtcNow.ToString("yyyy-MM"), CancellationToken.None);
        Assert.Equal(42, authoritative);
    }

    [Fact]
    public async Task End_RepeatedCall_IsIdempotent_NoDuplicateUsage()
    {
        var (accountId, deviceId) = await SeedAsync();
        var start = await _service.StartSessionAsync(accountId, deviceId, null, null, CancellationToken.None);

        _clock.UtcNow = _clock.UtcNow.AddSeconds(30);
        await _service.EndAsync(accountId, start.Session!.Id, CancellationToken.None);
        var secondEnd = await _service.EndAsync(accountId, start.Session.Id, CancellationToken.None);

        Assert.Equal(SessionOperationOutcome.AlreadyTerminal, secondEnd.Outcome);
        var authoritative = await _usageService.GetAuthoritativeUsageSecondsAsync(accountId, _clock.UtcNow.ToString("yyyy-MM"), CancellationToken.None);
        Assert.Equal(30, authoritative); // not 60
    }

    [Fact]
    public async Task End_CrossAccount_Denied()
    {
        var (accountA, _) = await SeedAsync();
        var (accountB, deviceB) = await SeedAsync();
        var start = await _service.StartSessionAsync(accountB, deviceB, null, null, CancellationToken.None);

        var end = await _service.EndAsync(accountA, start.Session!.Id, CancellationToken.None);
        Assert.Equal(SessionOperationOutcome.NotFound, end.Outcome);
    }

    [Fact]
    public async Task End_ClientCannotManipulateDuration_ServerUsesItsOwnClock()
    {
        // There is no client-supplied duration parameter on EndAsync at all — this test
        // documents that fact structurally: the method signature accepts only
        // (accountId, sessionId, ct).
        var (accountId, deviceId) = await SeedAsync();
        var start = await _service.StartSessionAsync(accountId, deviceId, null, null, CancellationToken.None);
        _clock.UtcNow = _clock.UtcNow.AddSeconds(5);

        await _service.EndAsync(accountId, start.Session!.Id, CancellationToken.None);
        var authoritative = await _usageService.GetAuthoritativeUsageSecondsAsync(accountId, _clock.UtcNow.ToString("yyyy-MM"), CancellationToken.None);
        Assert.Equal(5, authoritative);
    }

    // ---- E. Expiry ----

    [Fact]
    public async Task Expiry_RecordsUsage_UpToLastActivity_NotToNow()
    {
        var (accountId, deviceId) = await SeedAsync();
        var start = await _service.StartSessionAsync(accountId, deviceId, null, null, CancellationToken.None);

        _clock.UtcNow = _clock.UtcNow.AddSeconds(20);
        await _service.HeartbeatAsync(accountId, start.Session!.Id, CancellationToken.None); // last activity at t=20

        _clock.UtcNow = _clock.UtcNow.Add(Lease).AddSeconds(100); // long past lease
        var hb = await _service.HeartbeatAsync(accountId, start.Session.Id, CancellationToken.None); // triggers reconciliation

        Assert.Equal(TranslationSessionState.Expired, hb.Session!.State);
        var authoritative = await _usageService.GetAuthoritativeUsageSecondsAsync(accountId, _clock.UtcNow.ToString("yyyy-MM"), CancellationToken.None);
        Assert.Equal(20, authoritative); // not 120 — bounded by last proven activity
    }

    // ---- I/J. Security / persistence-adjacent ----

    [Fact]
    public async Task Session_NeverExposesSecretsInDomainModel_AuditedOnStartAndEnd()
    {
        var (accountId, deviceId) = await SeedAsync();
        var start = await _service.StartSessionAsync(accountId, deviceId, null, null, CancellationToken.None);
        await _service.EndAsync(accountId, start.Session!.Id, CancellationToken.None);

        Assert.Contains(_audit.Events, e => e.EventType == "TranslationSessionStarted");
        Assert.Contains(_audit.Events, e => e.EventType == "TranslationSessionEnded");
        // TranslationSession has no property capable of holding a token/JWT/secret at all
        // — verified structurally via reflection, mirroring the Phase 6.4 pattern.
        var properties = typeof(TranslationSession).GetProperties().Select(p => p.Name);
        Assert.DoesNotContain(properties, n => n.Contains("Token", StringComparison.OrdinalIgnoreCase) || n.Contains("Secret", StringComparison.OrdinalIgnoreCase) || n.Contains("Jwt", StringComparison.OrdinalIgnoreCase));
    }
}
