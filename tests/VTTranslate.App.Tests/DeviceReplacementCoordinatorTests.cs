using VTTranslate.App.Api;
using VTTranslate.App.Devices;

namespace VTTranslate.App.Tests;

/// <summary>
/// Phase 25E — client-side device-replacement tests: the coordinator's explicit <c>ReplaceDeviceAsync</c> (persists
/// the new id, never auto-invoked), the wire mapping for <c>device_limit_exceeded</c>, and confirmation that
/// <c>ExecuteWithDeviceRecoveryAsync</c>'s existing <c>DeviceNotAuthorized</c> recovery is unaffected.
/// </summary>
public sealed class DeviceReplacementCoordinatorTests : IDisposable
{
    private readonly string _tempFile = Path.Combine(Path.GetTempPath(), $"autraxis-device-replace-test-{Guid.NewGuid()}.json");
    public void Dispose() { if (File.Exists(_tempFile)) File.Delete(_tempFile); }

    private (DeviceRegistrationCoordinator Coordinator, FakeAutraxisApiClient Api, LocalFileDeviceIdentityStore Store) NewCoordinator()
    {
        var api = new FakeAutraxisApiClient();
        var store = new LocalFileDeviceIdentityStore(_tempFile);
        var tokenProvider = new FakeTokenProvider();
        var coordinator = new DeviceRegistrationCoordinator(api, tokenProvider, store, "Windows", "Test Machine");
        return (coordinator, api, store);
    }

    [Fact]
    public async Task ReplaceDeviceAsync_CallsTheReplaceEndpoint_NotRegister_AndPersistsTheNewId()
    {
        var (coordinator, api, store) = NewCoordinator();
        var newDeviceId = Guid.NewGuid();
        api.ReplaceDeviceResponses.Enqueue(() => new DeviceDto(newDeviceId, "Windows", "Test Machine", "Authorized", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null));

        var result = await coordinator.ReplaceDeviceAsync();

        Assert.Equal(newDeviceId, result);
        Assert.Equal(1, api.ReplaceDeviceCallCount);
        Assert.Equal(0, api.RegisterDeviceCallCount); // replacement never goes through the normal register call
        Assert.Equal(newDeviceId, await store.GetDeviceIdAsync("test-account-key"));
    }

    [Fact]
    public async Task ReplaceDeviceAsync_IsNeverCalled_ByEnsureDeviceRegisteredAsync_OnDeviceLimitExceeded()
    {
        // EnsureDeviceRegisteredAsync (the INITIAL registration path) must not auto-replace — only an explicit,
        // separate ReplaceDeviceAsync call (driven by customer confirmation) may do that.
        var (coordinator, api, _) = NewCoordinator();
        api.DevicesToReturn.Clear();
        var apiThatDenies = new FakeAutraxisApiClient();
        // no RegisterDeviceCalls scripted -> registering throws "no more scripted devices" if reached in a way
        // that assumes auto-replacement; instead we assert the real failure IS device_limit_exceeded and propagates.

        var throwingApi = new ThrowingOnLimitApiClient();
        var throwingCoordinator = new DeviceRegistrationCoordinator(throwingApi, new FakeTokenProvider(),
            new LocalFileDeviceIdentityStore(Path.Combine(Path.GetTempPath(), $"autraxis-device-replace-test-{Guid.NewGuid()}.json")), "Windows", "Test Machine");

        var ex = await Assert.ThrowsAsync<AutraxisApiException>(() => throwingCoordinator.EnsureDeviceRegisteredAsync());

        Assert.Equal(ApiErrorCategory.DeviceLimitExceeded, ex.Category);
        Assert.Equal(1, throwingApi.RegisterDeviceCallCount); // exactly one attempt — no automatic replace call at all
        Assert.Equal(0, throwingApi.ReplaceDeviceCallCount);
    }

    [Fact]
    public async Task ExecuteWithDeviceRecoveryAsync_StillOnlyRecoversFrom_DeviceNotAuthorized_Regression()
    {
        var (coordinator, api, _) = NewCoordinator();
        api.DevicesToReturn.Enqueue(new DeviceDto(Guid.NewGuid(), "Windows", "Test Machine", "Authorized", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null));
        var originalDeviceId = Guid.NewGuid();
        var attempts = 0;

        var (result, deviceId) = await coordinator.ExecuteWithDeviceRecoveryAsync<string>(originalDeviceId, id =>
        {
            attempts++;
            if (attempts == 1) throw new AutraxisApiException(ApiErrorCategory.DeviceNotAuthorized, "device_not_authorized");
            return Task.FromResult("ok");
        });

        Assert.Equal("ok", result);
        Assert.Equal(2, attempts);
        Assert.Equal(1, api.RegisterDeviceCallCount); // recovered via the EXISTING register-replacement path, unrelated to /devices/replace
        Assert.Equal(0, api.ReplaceDeviceCallCount);   // /devices/replace is never invoked by this recovery path
    }

    [Fact]
    public async Task ExecuteWithDeviceRecoveryAsync_DoesNotRecoverFrom_DeviceLimitExceeded_Regression()
    {
        var (coordinator, _, _) = NewCoordinator();
        var attempts = 0;

        var ex = await Assert.ThrowsAsync<AutraxisApiException>(() => coordinator.ExecuteWithDeviceRecoveryAsync<string>(Guid.NewGuid(), _ =>
        {
            attempts++;
            throw new AutraxisApiException(ApiErrorCategory.DeviceLimitExceeded, "device_limit_exceeded");
        }));

        Assert.Equal(ApiErrorCategory.DeviceLimitExceeded, ex.Category);
        Assert.Equal(1, attempts); // no replacement attempted for this category — it propagates unchanged
    }

    private sealed class ThrowingOnLimitApiClient : IAutraxisApiClient
    {
        public int RegisterDeviceCallCount { get; private set; }
        public int ReplaceDeviceCallCount { get; private set; }
        public Task<DeviceDto> RegisterDeviceAsync(string platform, string? displayName, CancellationToken ct = default)
        {
            RegisterDeviceCallCount++;
            throw new AutraxisApiException(ApiErrorCategory.DeviceLimitExceeded, "device_limit_exceeded");
        }
        public Task<DeviceDto> ReplaceDeviceAsync(string platform, string? displayName, CancellationToken ct = default)
        {
            ReplaceDeviceCallCount++;
            throw new NotSupportedException("must not be called automatically");
        }
        public Task<ProfileDto> GetProfileAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ProfileDto> UpdateProfileAsync(string? d, string? p, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<DeviceDto>> GetDevicesAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task RevokeDeviceAsync(Guid deviceId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ProviderAccessGrantDto> RequestProviderAccessAsync(Guid deviceId, string provider, string capability, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<TranslationSessionStartedDto> StartTranslationSessionAsync(Guid deviceId, string? clientSessionId, string? direction, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<TranslationSessionOperationDto> HeartbeatTranslationSessionAsync(Guid sessionId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<TranslationSessionOperationDto> EndTranslationSessionAsync(Guid sessionId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SubscriptionDto> GetSubscriptionAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<EntitlementsDto> GetEntitlementsAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<UsageSummaryDto> GetUsageAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<CancelSubscriptionResultDto> CancelSubscriptionAsync(bool immediate, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
