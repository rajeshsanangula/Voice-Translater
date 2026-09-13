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

    public Task<ProfileDto> GetProfileAsync(CancellationToken ct = default) => throw new NotSupportedException();
    public Task<ProfileDto> UpdateProfileAsync(string? displayName, string? preferredLanguagePair, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<DeviceDto>> GetDevicesAsync(CancellationToken ct = default) => throw new NotSupportedException();
    public Task RevokeDeviceAsync(Guid deviceId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<ProviderAccessGrantDto> RequestProviderAccessAsync(Guid deviceId, string provider, string capability, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<TranslationSessionStartedDto> StartTranslationSessionAsync(Guid deviceId, string? clientSessionId, string? direction, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<TranslationSessionOperationDto> HeartbeatTranslationSessionAsync(Guid sessionId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<TranslationSessionOperationDto> EndTranslationSessionAsync(Guid sessionId, CancellationToken ct = default) => throw new NotSupportedException();
}
