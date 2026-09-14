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
}
