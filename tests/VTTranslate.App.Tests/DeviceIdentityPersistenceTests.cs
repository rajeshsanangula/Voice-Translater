using VTTranslate.App.Api;
using VTTranslate.App.Devices;

namespace VTTranslate.App.Tests;

/// <summary>
/// Corrective patch (Phase 7.1 runtime-risk audit, Risk 2) — deterministic tests for
/// device-identity persist-and-reuse and its bounded revoked-device recovery. Uses a
/// real <see cref="LocalFileDeviceIdentityStore"/> against a temporary file (never the
/// developer machine's real settings directory) so persistence across "process
/// restarts" is proven for real, not merely against an in-memory variable — a fresh
/// <see cref="LocalFileDeviceIdentityStore"/> instance pointed at the SAME file path
/// stands in for a new process, exactly as the corrective-patch instruction requires.
/// </summary>
public sealed class DeviceIdentityPersistenceTests : IDisposable
{
    private readonly string _tempFile = Path.Combine(Path.GetTempPath(), $"autraxis-device-identity-test-{Guid.NewGuid()}.json");

    public void Dispose()
    {
        if (File.Exists(_tempFile)) File.Delete(_tempFile);
    }

    private static DeviceDto NewDevice(Guid id) =>
        new(id, "Windows", "Test Machine", "Authorized", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null);

    // ---- TEST 1: no persisted id -> registers, persists ----

    [Fact]
    public async Task NoPersistedDeviceId_RegistersAndPersistsTheReturnedId()
    {
        var store = new LocalFileDeviceIdentityStore(_tempFile);
        var apiClient = new FakeAutraxisApiClient();
        var registeredId = Guid.NewGuid();
        apiClient.DevicesToReturn.Enqueue(NewDevice(registeredId));
        var tokenProvider = new FakeTokenProvider();
        var coordinator = new DeviceRegistrationCoordinator(apiClient, tokenProvider, store, "Windows", "Test Machine");

        var deviceId = await coordinator.EnsureDeviceRegisteredAsync();

        Assert.Equal(registeredId, deviceId);
        Assert.Equal(1, apiClient.RegisterDeviceCallCount);
        Assert.Equal(registeredId, await store.GetDeviceIdAsync("test-account-key"));
    }

    // ---- TEST 2: persisted valid id -> reused, no POST ----

    [Fact]
    public async Task PersistedValidDeviceId_ReusedWithoutCallingRegisterDevice()
    {
        var store = new LocalFileDeviceIdentityStore(_tempFile);
        var persistedId = Guid.NewGuid();
        await store.SetDeviceIdAsync("test-account-key", persistedId);

        var apiClient = new FakeAutraxisApiClient();
        var tokenProvider = new FakeTokenProvider();
        var coordinator = new DeviceRegistrationCoordinator(apiClient, tokenProvider, store, "Windows", "Test Machine");

        var deviceId = await coordinator.EnsureDeviceRegisteredAsync();

        Assert.Equal(persistedId, deviceId);
        Assert.Equal(0, apiClient.RegisterDeviceCallCount); // never called
    }

    // ---- TEST 3: real "process restart" simulation ----

    [Fact]
    public async Task ApplicationRestartSimulation_SamePersistedDeviceIdReused_NoDuplicateRegistration()
    {
        var apiClient = new FakeAutraxisApiClient();
        var registeredId = Guid.NewGuid();
        apiClient.DevicesToReturn.Enqueue(NewDevice(registeredId));
        var tokenProvider = new FakeTokenProvider();

        // "Process 1": fresh store instance, no file exists yet.
        var storeProcess1 = new LocalFileDeviceIdentityStore(_tempFile);
        var coordinatorProcess1 = new DeviceRegistrationCoordinator(apiClient, tokenProvider, storeProcess1, "Windows", "Test Machine");
        var firstRunDeviceId = await coordinatorProcess1.EnsureDeviceRegisteredAsync();

        // "Process 2": a BRAND NEW store instance pointed at the same file — this is
        // the actual proof of cross-process persistence, not an in-memory field.
        var storeProcess2 = new LocalFileDeviceIdentityStore(_tempFile);
        var coordinatorProcess2 = new DeviceRegistrationCoordinator(apiClient, tokenProvider, storeProcess2, "Windows", "Test Machine");
        var secondRunDeviceId = await coordinatorProcess2.EnsureDeviceRegisteredAsync();

        Assert.Equal(firstRunDeviceId, secondRunDeviceId);
        Assert.Equal(registeredId, secondRunDeviceId);
        Assert.Equal(1, apiClient.RegisterDeviceCallCount); // exactly one POST /devices across both "processes"
    }

    // ---- TEST 4: sign-out/sign-in does not create a new device ----

    [Fact]
    public async Task SignOutThenSignInAsSameAccount_ReusesSameDeviceId()
    {
        var store = new LocalFileDeviceIdentityStore(_tempFile);
        var apiClient = new FakeAutraxisApiClient();
        var registeredId = Guid.NewGuid();
        apiClient.DevicesToReturn.Enqueue(NewDevice(registeredId));
        var tokenProvider = new FakeTokenProvider { AccountKey = "same-account" };
        var coordinator = new DeviceRegistrationCoordinator(apiClient, tokenProvider, store, "Windows", "Test Machine");

        var beforeSignOut = await coordinator.EnsureDeviceRegisteredAsync();

        // Simulates MainViewModel.SignOutAsync — deliberately does NOT clear any
        // persisted device state (the corrective patch's own explicit requirement).
        // Nothing to call here; the coordinator/store are untouched by sign-out.

        var afterSignInAgain = await coordinator.EnsureDeviceRegisteredAsync();

        Assert.Equal(beforeSignOut, afterSignInAgain);
        Assert.Equal(1, apiClient.RegisterDeviceCallCount); // sign-out/sign-in never re-registers
    }

    // ---- TEST 5: malformed persisted value treated as missing ----

    [Fact]
    public async Task MalformedPersistedDeviceId_TreatedAsMissing_RegistersBoundedOnce()
    {
        await File.WriteAllTextAsync(_tempFile, """{"test-account-key":"not-a-guid"}""");
        var store = new LocalFileDeviceIdentityStore(_tempFile);
        var apiClient = new FakeAutraxisApiClient();
        var registeredId = Guid.NewGuid();
        apiClient.DevicesToReturn.Enqueue(NewDevice(registeredId));
        var tokenProvider = new FakeTokenProvider();
        var coordinator = new DeviceRegistrationCoordinator(apiClient, tokenProvider, store, "Windows", "Test Machine");

        var deviceId = await coordinator.EnsureDeviceRegisteredAsync();

        Assert.Equal(registeredId, deviceId);
        Assert.Equal(1, apiClient.RegisterDeviceCallCount); // exactly one registration, not an exception
    }

    [Fact]
    public async Task MalformedJsonFile_TreatedAsNoPersistedState_NeverThrows()
    {
        await File.WriteAllTextAsync(_tempFile, "{ this is not valid json ]");
        var store = new LocalFileDeviceIdentityStore(_tempFile);

        var result = await store.GetDeviceIdAsync("test-account-key");

        Assert.Null(result);
    }

    // ---- TEST 6/7: backend rejects persisted device -> bounded one replacement ----

    [Fact]
    public async Task DeviceNotAuthorized_ClearsPersistedId_RegistersExactlyOneReplacement_UsesNewId()
    {
        var store = new LocalFileDeviceIdentityStore(_tempFile);
        var staleId = Guid.NewGuid();
        await store.SetDeviceIdAsync("test-account-key", staleId);

        var apiClient = new FakeAutraxisApiClient();
        var replacementId = Guid.NewGuid();
        apiClient.DevicesToReturn.Enqueue(NewDevice(replacementId));
        var tokenProvider = new FakeTokenProvider();
        var coordinator = new DeviceRegistrationCoordinator(apiClient, tokenProvider, store, "Windows", "Test Machine");

        var callCount = 0;
        var (result, usedDeviceId) = await coordinator.ExecuteWithDeviceRecoveryAsync(staleId, id =>
        {
            callCount++;
            if (id == staleId) throw new AutraxisApiException(ApiErrorCategory.DeviceNotAuthorized, "device_not_authorized");
            return Task.FromResult(id); // second call, with the replacement id, succeeds
        });

        Assert.Equal(replacementId, usedDeviceId);
        Assert.Equal(replacementId, result);
        Assert.Equal(2, callCount); // original attempt + exactly one retry
        Assert.Equal(1, apiClient.RegisterDeviceCallCount); // exactly one replacement registration
        Assert.Equal(replacementId, await store.GetDeviceIdAsync("test-account-key")); // new id persisted
    }

    // ---- TEST 8: replacement registration itself hits the device limit -> fails safely ----

    [Fact]
    public async Task ReplacementRegistration_HitsDeviceLimit_FailsSafely_NoLoop()
    {
        var store = new LocalFileDeviceIdentityStore(_tempFile);
        var staleId = Guid.NewGuid();
        await store.SetDeviceIdAsync("test-account-key", staleId);

        var apiClient = new FakeAutraxisApiClient(); // no devices queued — RegisterDeviceAsync will "fail"
        var tokenProvider = new FakeTokenProvider();
        var coordinator = new DeviceRegistrationCoordinator(apiClient, tokenProvider, store, "Windows", "Test Machine");

        // Simulate the replacement registration itself failing (e.g. MaxActiveDevices
        // reached) by having the fake api client throw instead of returning a device.
        var apiClientThatDenies = new DenyingFakeApiClient();
        var coordinatorThatDenies = new DeviceRegistrationCoordinator(apiClientThatDenies, tokenProvider, store, "Windows", "Test Machine");

        await Assert.ThrowsAsync<AutraxisApiException>(() => coordinatorThatDenies.ExecuteWithDeviceRecoveryAsync<object?>(staleId, id =>
            throw new AutraxisApiException(ApiErrorCategory.DeviceNotAuthorized, "device_not_authorized")));

        Assert.Equal(1, apiClientThatDenies.RegisterDeviceCallCount); // exactly one replacement ATTEMPT, then it fails safely
    }

    private sealed class DenyingFakeApiClient : IAutraxisApiClient
    {
        public int RegisterDeviceCallCount { get; private set; }
        public Task<DeviceDto> RegisterDeviceAsync(string platform, string? displayName, CancellationToken ct = default)
        {
            RegisterDeviceCallCount++;
            throw new AutraxisApiException(ApiErrorCategory.EntitlementDenied, "device_limit_reached");
        }

        public Task<ProfileDto> GetProfileAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ProfileDto> UpdateProfileAsync(string? d, string? p, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<DeviceDto>> GetDevicesAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task RevokeDeviceAsync(Guid deviceId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ProviderAccessGrantDto> RequestProviderAccessAsync(Guid deviceId, string provider, string capability, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<TranslationSessionStartedDto> StartTranslationSessionAsync(Guid deviceId, string? clientSessionId, string? direction, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<TranslationSessionOperationDto> HeartbeatTranslationSessionAsync(Guid sessionId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<TranslationSessionOperationDto> EndTranslationSessionAsync(Guid sessionId, CancellationToken ct = default) => throw new NotSupportedException();

        // Phase 7.3: mechanical interface-completeness additions only — this fake's
        // purpose (device-registration-denial simulation) is unrelated to any of these.
        public Task<SubscriptionDto> GetSubscriptionAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<EntitlementsDto> GetEntitlementsAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<UsageSummaryDto> GetUsageAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<CancelSubscriptionResultDto> CancelSubscriptionAsync(bool immediate, CancellationToken ct = default) => throw new NotSupportedException();
    }

    // ---- TEST 9: repeated invalid-device responses never loop ----

    [Fact]
    public async Task RepeatedDeviceNotAuthorized_NeverRetriesMoreThanOnce()
    {
        var store = new LocalFileDeviceIdentityStore(_tempFile);
        var apiClient = new FakeAutraxisApiClient();
        apiClient.DevicesToReturn.Enqueue(NewDevice(Guid.NewGuid())); // one replacement device available
        var tokenProvider = new FakeTokenProvider();
        var coordinator = new DeviceRegistrationCoordinator(apiClient, tokenProvider, store, "Windows", "Test Machine");

        var attempts = 0;
        var ex = await Assert.ThrowsAsync<AutraxisApiException>(() => coordinator.ExecuteWithDeviceRecoveryAsync<object?>(Guid.NewGuid(), _ =>
        {
            attempts++;
            throw new AutraxisApiException(ApiErrorCategory.DeviceNotAuthorized, "device_not_authorized"); // ALWAYS fails, even with the replacement
        }));

        Assert.Equal(ApiErrorCategory.DeviceNotAuthorized, ex.Category);
        Assert.Equal(2, attempts); // original + exactly one retry — never a third attempt
        Assert.Equal(1, apiClient.RegisterDeviceCallCount); // exactly one replacement registration, never a second
    }

    // ---- TEST 10: account isolation ----

    [Fact]
    public async Task DifferentAccounts_NeverShareAPersistedDeviceId()
    {
        var store = new LocalFileDeviceIdentityStore(_tempFile);
        var apiClient = new FakeAutraxisApiClient();
        var deviceForAccountA = Guid.NewGuid();
        var deviceForAccountB = Guid.NewGuid();
        apiClient.DevicesToReturn.Enqueue(NewDevice(deviceForAccountA));
        apiClient.DevicesToReturn.Enqueue(NewDevice(deviceForAccountB));

        var tokenProviderA = new FakeTokenProvider { AccountKey = "account-a" };
        var tokenProviderB = new FakeTokenProvider { AccountKey = "account-b" };
        var coordinatorA = new DeviceRegistrationCoordinator(apiClient, tokenProviderA, store, "Windows", "Test Machine");
        var coordinatorB = new DeviceRegistrationCoordinator(apiClient, tokenProviderB, store, "Windows", "Test Machine");

        var resolvedForA = await coordinatorA.EnsureDeviceRegisteredAsync();
        var resolvedForB = await coordinatorB.EnsureDeviceRegisteredAsync();

        Assert.Equal(deviceForAccountA, resolvedForA);
        Assert.Equal(deviceForAccountB, resolvedForB);
        Assert.NotEqual(resolvedForA, resolvedForB);
        Assert.Equal(2, apiClient.RegisterDeviceCallCount); // each account registers its own device

        // Re-resolving for A again must still yield A's device, never B's.
        var resolvedForAAgain = await coordinatorA.EnsureDeviceRegisteredAsync();
        Assert.Equal(deviceForAccountA, resolvedForAAgain);
        Assert.Equal(2, apiClient.RegisterDeviceCallCount); // no extra registration
    }

    // ---- TEST 11: concurrency ----

    [Fact]
    public async Task ConcurrentStartOperations_NeverCauseDuplicateRegistration()
    {
        var store = new LocalFileDeviceIdentityStore(_tempFile);
        var apiClient = new FakeAutraxisApiClient();
        apiClient.DevicesToReturn.Enqueue(NewDevice(Guid.NewGuid()));
        var tokenProvider = new FakeTokenProvider();
        var coordinator = new DeviceRegistrationCoordinator(apiClient, tokenProvider, store, "Windows", "Test Machine");

        var tasks = Enumerable.Range(0, 10).Select(_ => coordinator.EnsureDeviceRegisteredAsync());
        var results = await Task.WhenAll(tasks);

        Assert.Single(results.Distinct()); // every concurrent caller resolves to the SAME device id
        Assert.Equal(1, apiClient.RegisterDeviceCallCount); // exactly one POST /devices, never one per caller
    }

    // ---- TEST 12: no client-generated identifier, ever ----

    [Fact]
    public async Task NeverGeneratesItsOwnDeviceId_OnlyEverUsesTheServerReturnedOne()
    {
        var store = new LocalFileDeviceIdentityStore(_tempFile);
        var apiClient = new FakeAutraxisApiClient();
        var serverIssuedId = Guid.NewGuid();
        apiClient.DevicesToReturn.Enqueue(NewDevice(serverIssuedId));
        var tokenProvider = new FakeTokenProvider();
        var coordinator = new DeviceRegistrationCoordinator(apiClient, tokenProvider, store, "Windows", "Test Machine");

        var deviceId = await coordinator.EnsureDeviceRegisteredAsync();

        Assert.Equal(serverIssuedId, deviceId); // exactly what the backend returned, never a client-side Guid.NewGuid()
        Assert.Single(apiClient.RegisterDeviceCalls);
        Assert.Equal("Windows", apiClient.RegisterDeviceCalls[0].Platform); // no hardware fingerprint value sent — DisplayName is a user-facing label only
    }
}
