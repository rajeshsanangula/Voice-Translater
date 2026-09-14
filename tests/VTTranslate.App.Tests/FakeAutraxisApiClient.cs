using VTTranslate.App.Api;

namespace VTTranslate.App.Tests;

/// <summary>Deterministic <see cref="IAutraxisApiClient"/> test double — used to drive <see cref="VTTranslate.App.Devices.DeviceRegistrationCoordinator"/> tests without any real HTTP/MSAL dependency.</summary>
public sealed class FakeAutraxisApiClient : IAutraxisApiClient
{
    public int RegisterDeviceCallCount { get; private set; }
    public Queue<DeviceDto> DevicesToReturn { get; } = new();
    public List<(string Platform, string? DisplayName)> RegisterDeviceCalls { get; } = new();

    public Task<DeviceDto> RegisterDeviceAsync(string platform, string? displayName, CancellationToken ct = default)
    {
        RegisterDeviceCallCount++;
        RegisterDeviceCalls.Add((platform, displayName));
        if (DevicesToReturn.Count == 0)
            throw new InvalidOperationException("FakeAutraxisApiClient: no more scripted devices to return.");
        return Task.FromResult(DevicesToReturn.Dequeue());
    }

    // ---- Phase 7.2: scriptable provider-access behavior, for
    // ProviderCredentialRenewalCoordinator tests. Each entry in
    // ProviderAccessResponses is either a grant to return or an exception to throw,
    // dequeued in order — lets a test script an exact sequence (e.g. transient
    // failure, transient failure, success) without any real HTTP/network dependency. ----
    public int RequestProviderAccessCallCount { get; private set; }
    public List<(Guid DeviceId, string Provider, string Capability)> RequestProviderAccessCalls { get; } = new();
    public Queue<Func<ProviderAccessGrantDto>> ProviderAccessResponses { get; } = new();

    public Task<ProviderAccessGrantDto> RequestProviderAccessAsync(Guid deviceId, string provider, string capability, CancellationToken ct = default)
    {
        RequestProviderAccessCallCount++;
        RequestProviderAccessCalls.Add((deviceId, provider, capability));
        if (ProviderAccessResponses.Count == 0)
            throw new InvalidOperationException("FakeAutraxisApiClient: no more scripted provider-access responses.");
        return Task.FromResult(ProviderAccessResponses.Dequeue()());
    }

    public Task<ProfileDto> GetProfileAsync(CancellationToken ct = default) => throw new NotSupportedException();
    public Task<ProfileDto> UpdateProfileAsync(string? displayName, string? preferredLanguagePair, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<DeviceDto>> GetDevicesAsync(CancellationToken ct = default) => throw new NotSupportedException();
    public Task RevokeDeviceAsync(Guid deviceId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<TranslationSessionStartedDto> StartTranslationSessionAsync(Guid deviceId, string? clientSessionId, string? direction, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<TranslationSessionOperationDto> HeartbeatTranslationSessionAsync(Guid sessionId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<TranslationSessionOperationDto> EndTranslationSessionAsync(Guid sessionId, CancellationToken ct = default) => throw new NotSupportedException();

    // ---- Phase 7.3: scriptable subscription/entitlement/usage/cancellation
    // responses, for AccountViewModel and API-client tests. Each queue entry is a
    // Func so a test can script either a value or a thrown AutraxisApiException,
    // mirroring the Phase 7.2 ProviderAccessResponses pattern exactly. Cancellation-
    // aware: honors the caller's CancellationToken like a real HTTP call would, so
    // cancellation-during-load tests are meaningful against this fake too. ----
    public int GetSubscriptionCallCount { get; private set; }
    public Queue<Func<SubscriptionDto>> SubscriptionResponses { get; } = new();
    /// <summary>Optional, awaited (never blocked-on) before the queued response is produced — lets a test observe genuine mid-flight state (e.g. IsLoadingSubscription) via a real async suspension point, instead of a deadlock-prone synchronous block (xUnit1031).</summary>
    public Func<Task>? SubscriptionResponseGate { get; set; }
    public async Task<SubscriptionDto> GetSubscriptionAsync(CancellationToken ct = default)
    {
        GetSubscriptionCallCount++;
        ct.ThrowIfCancellationRequested();
        if (SubscriptionResponseGate is not null) await SubscriptionResponseGate();
        if (SubscriptionResponses.Count == 0)
            throw new InvalidOperationException("FakeAutraxisApiClient: no more scripted subscription responses.");
        return SubscriptionResponses.Dequeue()();
    }

    public int GetEntitlementsCallCount { get; private set; }
    public Queue<Func<EntitlementsDto>> EntitlementsResponses { get; } = new();
    public Task<EntitlementsDto> GetEntitlementsAsync(CancellationToken ct = default)
    {
        GetEntitlementsCallCount++;
        ct.ThrowIfCancellationRequested();
        if (EntitlementsResponses.Count == 0)
            throw new InvalidOperationException("FakeAutraxisApiClient: no more scripted entitlements responses.");
        return Task.FromResult(EntitlementsResponses.Dequeue()());
    }

    public int GetUsageCallCount { get; private set; }
    public Queue<Func<UsageSummaryDto>> UsageResponses { get; } = new();
    public Task<UsageSummaryDto> GetUsageAsync(CancellationToken ct = default)
    {
        GetUsageCallCount++;
        ct.ThrowIfCancellationRequested();
        if (UsageResponses.Count == 0)
            throw new InvalidOperationException("FakeAutraxisApiClient: no more scripted usage responses.");
        return Task.FromResult(UsageResponses.Dequeue()());
    }

    public int CancelSubscriptionCallCount { get; private set; }
    public List<bool> CancelSubscriptionCalls { get; } = new();
    public Queue<Func<CancelSubscriptionResultDto>> CancelSubscriptionResponses { get; } = new();
    /// <summary>Same purpose as <see cref="SubscriptionResponseGate"/> — an awaited (never blocked-on) hook so a test can observe genuine mid-flight <c>IsCancelling</c> state.</summary>
    public Func<Task>? CancelSubscriptionResponseGate { get; set; }
    public async Task<CancelSubscriptionResultDto> CancelSubscriptionAsync(bool immediate, CancellationToken ct = default)
    {
        CancelSubscriptionCallCount++;
        CancelSubscriptionCalls.Add(immediate);
        ct.ThrowIfCancellationRequested();
        if (CancelSubscriptionResponseGate is not null) await CancelSubscriptionResponseGate();
        if (CancelSubscriptionResponses.Count == 0)
            throw new InvalidOperationException("FakeAutraxisApiClient: no more scripted cancel-subscription responses.");
        return CancelSubscriptionResponses.Dequeue()();
    }
}
