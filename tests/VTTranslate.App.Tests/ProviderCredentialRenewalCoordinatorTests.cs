using VTTranslate.App.Api;
using VTTranslate.App.Providers;

namespace VTTranslate.App.Tests;

/// <summary>
/// Phase 7.2 — deterministic tests for <see cref="ProviderCredentialRenewalCoordinator"/>.
/// No real Azure, no real HTTP, no real MSAL — fakes only, mirroring the exact pattern
/// already proven for <c>DeviceRegistrationCoordinator</c>/<c>AutraxisApiClient</c> in
/// Phase 7.1. Every "wait" in these tests uses an expiry a few milliseconds in the
/// future with a near-zero safety window, never a real multi-minute delay.
/// </summary>
public sealed class ProviderCredentialRenewalCoordinatorTests
{
    private static readonly TimeSpan TestSafetyWindow = TimeSpan.FromMilliseconds(1);
    private const string ProviderName = "AzureSpeech";
    private const string Capability = "SpeechRecognition";

    private static ProviderAccessGrantDto NewGrant(string token, DateTimeOffset expiresAt) =>
        new(ProviderName, Capability, token, "eastus", expiresAt, Guid.NewGuid());

    // ---- Credential lifecycle ----

    [Fact]
    public void ComputeRenewalDelay_NotYetDue_ReturnsPositiveDelay()
    {
        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.AddMinutes(10);
        var safetyWindow = TimeSpan.FromMinutes(2);

        var delay = ProviderCredentialRenewalCoordinator.ComputeRenewalDelay(expiresAt, safetyWindow, now);

        Assert.Equal(TimeSpan.FromMinutes(8), delay); // 10 - 2 = 8 minutes before renewal is due
    }

    [Fact]
    public void ComputeRenewalDelay_AlreadyDue_ReturnsZero_NeverNegative()
    {
        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.AddSeconds(30); // safety window already consumed
        var safetyWindow = TimeSpan.FromMinutes(2);

        var delay = ProviderCredentialRenewalCoordinator.ComputeRenewalDelay(expiresAt, safetyWindow, now);

        Assert.Equal(TimeSpan.Zero, delay);
    }

    [Fact]
    public async Task SuccessfulRenewal_AppliesNewTokenToTheSameProvider_ViaLiveReplacement()
    {
        var apiClient = new FakeAutraxisApiClient();
        var newExpiry = DateTimeOffset.UtcNow.AddMinutes(10);
        apiClient.ProviderAccessResponses.Enqueue(() => NewGrant("fresh-token", newExpiry));
        var provider = new FakeRenewableCredentialProvider();
        var deviceId = Guid.NewGuid();

        var coordinator = new ProviderCredentialRenewalCoordinator(apiClient, provider, deviceId, ProviderName, Capability, "EN→DE", new RecordingDiagnosticLogger(), TestSafetyWindow);
        coordinator.Start(DateTimeOffset.UtcNow.AddMilliseconds(5), CancellationToken.None);

        await Task.Delay(200); // generous margin for the near-zero scheduled delay to fire
        await coordinator.DisposeAsync();

        Assert.Equal(1, apiClient.RequestProviderAccessCallCount);
        Assert.Equal(1, provider.UpdateCallCount);
        Assert.Contains("fresh-token", provider.AppliedTokens);
    }

    [Fact]
    public async Task SuccessfulRenewal_RequestsTheExactSameProviderAndCapability_AsTheOriginalGrant()
    {
        var apiClient = new FakeAutraxisApiClient();
        apiClient.ProviderAccessResponses.Enqueue(() => NewGrant("t", DateTimeOffset.UtcNow.AddMinutes(10)));
        var provider = new FakeRenewableCredentialProvider();
        var deviceId = Guid.NewGuid();

        var coordinator = new ProviderCredentialRenewalCoordinator(apiClient, provider, deviceId, ProviderName, Capability, "EN→DE", new RecordingDiagnosticLogger(), TestSafetyWindow);
        coordinator.Start(DateTimeOffset.UtcNow.AddMilliseconds(5), CancellationToken.None);
        await Task.Delay(200);
        await coordinator.DisposeAsync();

        var call = Assert.Single(apiClient.RequestProviderAccessCalls);
        Assert.Equal(deviceId, call.DeviceId);
        Assert.Equal(ProviderName, call.Provider);
        Assert.Equal(Capability, call.Capability);
    }

    [Fact]
    public async Task MultipleRenewalCycles_EachUsesTheNewestExpiry_NeverTheOriginal()
    {
        var apiClient = new FakeAutraxisApiClient();
        apiClient.ProviderAccessResponses.Enqueue(() => NewGrant("token-2", DateTimeOffset.UtcNow.AddMilliseconds(30)));
        apiClient.ProviderAccessResponses.Enqueue(() => NewGrant("token-3", DateTimeOffset.UtcNow.AddMinutes(10)));
        var provider = new FakeRenewableCredentialProvider();

        var coordinator = new ProviderCredentialRenewalCoordinator(apiClient, provider, Guid.NewGuid(), ProviderName, Capability, "EN→DE", new RecordingDiagnosticLogger(), TestSafetyWindow);
        coordinator.Start(DateTimeOffset.UtcNow.AddMilliseconds(5), CancellationToken.None);

        await Task.Delay(500); // enough real time for two short-lived cycles to complete
        await coordinator.DisposeAsync();

        Assert.Equal(2, apiClient.RequestProviderAccessCallCount); // second cycle scheduled from the SECOND grant's own expiry, not the first
        Assert.Contains("token-2", provider.AppliedTokens);
        Assert.Contains("token-3", provider.AppliedTokens);
    }

    // ---- Stop / cancellation ----

    [Fact]
    public async Task Cancellation_BeforeRenewalDue_PreventsAnyProviderAccessCall()
    {
        var apiClient = new FakeAutraxisApiClient(); // no responses queued — a call would throw
        var provider = new FakeRenewableCredentialProvider();
        using var cts = new CancellationTokenSource();

        var coordinator = new ProviderCredentialRenewalCoordinator(apiClient, provider, Guid.NewGuid(), ProviderName, Capability, "EN→DE", new RecordingDiagnosticLogger());
        // Long-lived expiry (real default safety window) — renewal is nowhere near due.
        coordinator.Start(DateTimeOffset.UtcNow.AddMinutes(10), cts.Token);

        cts.Cancel(); // Stop() equivalent
        await coordinator.DisposeAsync();

        Assert.Equal(0, apiClient.RequestProviderAccessCallCount);
        Assert.Equal(0, provider.UpdateCallCount);
    }

    [Fact]
    public async Task StopDuringRenewal_ResultIsDiscarded_ProviderNeverUpdatedAfterStop()
    {
        // Simulates: the HTTP renewal call is still "in flight" (from the coordinator's
        // perspective) when Stop() sets the provider into its own "stopped" state —
        // TryUpdateAuthorizationTokenAsync returning false is EXACTLY how the real
        // AzureSpeechTranslationProvider reports "Stop() already won" (docs §13/§14).
        var apiClient = new FakeAutraxisApiClient();
        apiClient.ProviderAccessResponses.Enqueue(() => NewGrant("late-token", DateTimeOffset.UtcNow.AddMinutes(10)));
        var provider = new FakeRenewableCredentialProvider { StoppedOrNotStarted = true };
        var logger = new RecordingDiagnosticLogger();

        var coordinator = new ProviderCredentialRenewalCoordinator(apiClient, provider, Guid.NewGuid(), ProviderName, Capability, "EN→DE", logger, TestSafetyWindow);
        coordinator.Start(DateTimeOffset.UtcNow.AddMilliseconds(5), CancellationToken.None);

        await Task.Delay(200);
        await coordinator.DisposeAsync();

        Assert.Empty(provider.AppliedTokens); // the grant was obtained, but never applied
        Assert.True(logger.Contains("ProviderCredentialLiveReplacementFailed"));
        Assert.False(logger.Contains("ProviderCredentialLiveReplacementSucceeded"));
    }

    [Fact]
    public async Task Cancellation_NeverLogsAsARenewalFailure()
    {
        var apiClient = new FakeAutraxisApiClient();
        var provider = new FakeRenewableCredentialProvider();
        var logger = new RecordingDiagnosticLogger();
        using var cts = new CancellationTokenSource();

        var coordinator = new ProviderCredentialRenewalCoordinator(apiClient, provider, Guid.NewGuid(), ProviderName, Capability, "EN→DE", logger, TimeSpan.FromMilliseconds(1));
        coordinator.Start(DateTimeOffset.UtcNow.AddSeconds(5), cts.Token); // due in 5s, well after we cancel below

        cts.Cancel();
        await coordinator.DisposeAsync();

        Assert.True(logger.Contains("ProviderCredentialRenewalCancelled"));
        Assert.False(logger.Contains("ProviderCredentialRenewalFailed"));
    }

    [Fact]
    public async Task DisposeAsync_IsIdempotent()
    {
        var apiClient = new FakeAutraxisApiClient();
        var provider = new FakeRenewableCredentialProvider();
        var coordinator = new ProviderCredentialRenewalCoordinator(apiClient, provider, Guid.NewGuid(), ProviderName, Capability, "EN→DE", new RecordingDiagnosticLogger());
        coordinator.Start(DateTimeOffset.UtcNow.AddMinutes(10), CancellationToken.None);

        await coordinator.DisposeAsync();
        await coordinator.DisposeAsync(); // must not throw
    }

    // ---- Bidirectional isolation ----

    [Fact]
    public async Task TwoDirections_RenewIndependently_NeverShareState()
    {
        var apiClientA = new FakeAutraxisApiClient();
        apiClientA.ProviderAccessResponses.Enqueue(() => NewGrant("en-de-token", DateTimeOffset.UtcNow.AddMinutes(10)));
        var providerA = new FakeRenewableCredentialProvider();

        var apiClientB = new FakeAutraxisApiClient(); // separate instance — no responses queued
        var providerB = new FakeRenewableCredentialProvider();

        var coordinatorA = new ProviderCredentialRenewalCoordinator(apiClientA, providerA, Guid.NewGuid(), ProviderName, Capability, "EN→DE", new RecordingDiagnosticLogger(), TestSafetyWindow);
        var coordinatorB = new ProviderCredentialRenewalCoordinator(apiClientB, providerB, Guid.NewGuid(), ProviderName, Capability, "DE→EN", new RecordingDiagnosticLogger(), TimeSpan.FromMinutes(2));

        coordinatorA.Start(DateTimeOffset.UtcNow.AddMilliseconds(5), CancellationToken.None);
        coordinatorB.Start(DateTimeOffset.UtcNow.AddMinutes(10), CancellationToken.None); // not due for a long time

        await Task.Delay(200);
        await coordinatorA.DisposeAsync();
        await coordinatorB.DisposeAsync();

        Assert.Equal(1, apiClientA.RequestProviderAccessCallCount);
        Assert.Equal(0, apiClientB.RequestProviderAccessCallCount); // B never renewed — proves no cross-direction coupling
        Assert.Contains("en-de-token", providerA.AppliedTokens);
        Assert.Empty(providerB.AppliedTokens);
    }

    [Fact]
    public async Task OneDirectionFailure_NeverAffectsTheOtherDirection()
    {
        var apiClientA = new FakeAutraxisApiClient();
        apiClientA.ProviderAccessResponses.Enqueue(() => throw new AutraxisApiException(ApiErrorCategory.DeviceNotAuthorized, "device_not_authorized")); // A's renewal fails, permanently
        var providerA = new FakeRenewableCredentialProvider();

        var apiClientB = new FakeAutraxisApiClient();
        apiClientB.ProviderAccessResponses.Enqueue(() => NewGrant("de-en-token", DateTimeOffset.UtcNow.AddMinutes(10)));
        var providerB = new FakeRenewableCredentialProvider();

        var coordinatorA = new ProviderCredentialRenewalCoordinator(apiClientA, providerA, Guid.NewGuid(), ProviderName, Capability, "EN→DE", new RecordingDiagnosticLogger(), TestSafetyWindow);
        var coordinatorB = new ProviderCredentialRenewalCoordinator(apiClientB, providerB, Guid.NewGuid(), ProviderName, Capability, "DE→EN", new RecordingDiagnosticLogger(), TestSafetyWindow);

        coordinatorA.Start(DateTimeOffset.UtcNow.AddMilliseconds(5), CancellationToken.None);
        coordinatorB.Start(DateTimeOffset.UtcNow.AddMilliseconds(5), CancellationToken.None);

        await Task.Delay(200);
        await coordinatorA.DisposeAsync();
        await coordinatorB.DisposeAsync();

        Assert.Empty(providerA.AppliedTokens); // A's renewal failed (no scripted response)
        Assert.Contains("de-en-token", providerB.AppliedTokens); // B succeeded, unaffected by A's failure
    }

    // ---- Failure handling / bounded retry ----

    [Fact]
    public async Task TransientFailure_RetriesUpToBoundThenGivesUp_NeverExceedsTheBound()
    {
        var apiClient = new FakeAutraxisApiClient();
        for (var i = 0; i < 3; i++)
            apiClient.ProviderAccessResponses.Enqueue(() => throw new AutraxisApiException(ApiErrorCategory.ServiceUnavailable, "service_unavailable"));
        var provider = new FakeRenewableCredentialProvider();
        var logger = new RecordingDiagnosticLogger();

        var coordinator = new ProviderCredentialRenewalCoordinator(apiClient, provider, Guid.NewGuid(), ProviderName, Capability, "EN→DE", logger, TestSafetyWindow);
        coordinator.Start(DateTimeOffset.UtcNow.AddMilliseconds(5), CancellationToken.None);

        await Task.Delay(8_000); // must comfortably exceed the 2s+4s backoff between the 3 bounded attempts
        await coordinator.DisposeAsync();

        Assert.Equal(3, apiClient.RequestProviderAccessCallCount); // exactly the bound — never a 4th attempt
        Assert.Empty(provider.AppliedTokens);
        Assert.True(logger.Contains("ProviderCredentialRenewalGivenUp"));
    }

    [Fact]
    public async Task PermanentAuthorizationDenial_FailsImmediately_NeverRetried()
    {
        var apiClient = new FakeAutraxisApiClient();
        apiClient.ProviderAccessResponses.Enqueue(() => throw new AutraxisApiException(ApiErrorCategory.DeviceNotAuthorized, "device_not_authorized"));
        var provider = new FakeRenewableCredentialProvider();
        var logger = new RecordingDiagnosticLogger();

        var coordinator = new ProviderCredentialRenewalCoordinator(apiClient, provider, Guid.NewGuid(), ProviderName, Capability, "EN→DE", logger, TestSafetyWindow);
        coordinator.Start(DateTimeOffset.UtcNow.AddMilliseconds(5), CancellationToken.None);

        await Task.Delay(500);
        await coordinator.DisposeAsync();

        Assert.Equal(1, apiClient.RequestProviderAccessCallCount); // no retry at all for a permanent denial
        Assert.True(logger.Contains("ProviderCredentialRenewalGivenUp"));
    }

    [Fact]
    public async Task InvalidGrantMetadata_AlreadyExpired_FailsClosed_NeverApplied()
    {
        var apiClient = new FakeAutraxisApiClient();
        apiClient.ProviderAccessResponses.Enqueue(() => NewGrant("stale-token", DateTimeOffset.UtcNow.AddSeconds(-1))); // already expired per the grant's own metadata
        var provider = new FakeRenewableCredentialProvider();

        var coordinator = new ProviderCredentialRenewalCoordinator(apiClient, provider, Guid.NewGuid(), ProviderName, Capability, "EN→DE", new RecordingDiagnosticLogger(), TestSafetyWindow);
        coordinator.Start(DateTimeOffset.UtcNow.AddMilliseconds(5), CancellationToken.None);

        await Task.Delay(200);
        await coordinator.DisposeAsync();

        Assert.Empty(provider.AppliedTokens); // never applied a credential the grant itself says is already unusable
    }

    // ---- Session accounting (structural) ----

    [Fact]
    public async Task Renewal_NeverCallsAnyTranslationSessionEndpoint()
    {
        // FakeAutraxisApiClient throws NotSupportedException for Start/Heartbeat/End —
        // if the coordinator ever called any of them, this test would fail with that
        // exception rather than completing normally.
        var apiClient = new FakeAutraxisApiClient();
        apiClient.ProviderAccessResponses.Enqueue(() => NewGrant("t", DateTimeOffset.UtcNow.AddMinutes(10)));
        var provider = new FakeRenewableCredentialProvider();

        var coordinator = new ProviderCredentialRenewalCoordinator(apiClient, provider, Guid.NewGuid(), ProviderName, Capability, "EN→DE", new RecordingDiagnosticLogger(), TestSafetyWindow);
        coordinator.Start(DateTimeOffset.UtcNow.AddMilliseconds(5), CancellationToken.None);

        await Task.Delay(200);
        await coordinator.DisposeAsync(); // no exception => no session-lifecycle endpoint was ever touched
    }

    // ---- Security ----

    [Fact]
    public async Task DiagnosticLog_NeverContainsTheTokenValue()
    {
        var apiClient = new FakeAutraxisApiClient();
        const string secretShapedToken = "super-secret-token-value-should-never-be-logged";
        apiClient.ProviderAccessResponses.Enqueue(() => NewGrant(secretShapedToken, DateTimeOffset.UtcNow.AddMinutes(10)));
        var provider = new FakeRenewableCredentialProvider();
        var logger = new RecordingDiagnosticLogger();

        var coordinator = new ProviderCredentialRenewalCoordinator(apiClient, provider, Guid.NewGuid(), ProviderName, Capability, "EN→DE", logger, TestSafetyWindow);
        coordinator.Start(DateTimeOffset.UtcNow.AddMilliseconds(5), CancellationToken.None);

        await Task.Delay(200);
        await coordinator.DisposeAsync();

        Assert.DoesNotContain(logger.Events, e => e.Details.Contains(secretShapedToken));
    }
}
