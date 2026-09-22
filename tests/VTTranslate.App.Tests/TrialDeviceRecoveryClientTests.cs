using System.Net;
using VTTranslate.App.Api;
using VTTranslate.App.Authentication;

namespace VTTranslate.App.Tests;

/// <summary>
/// Phase 25C/25E — client-side half of the device-recovery investigation: confirms, against the real
/// <see cref="AutraxisApiClient"/> mapping (not a re-implementation), that a <c>device_limit_exceeded</c> response
/// does NOT map to <see cref="ApiErrorCategory.DeviceNotAuthorized"/> — the one category
/// <see cref="Devices.DeviceRegistrationCoordinator.ExecuteWithDeviceRecoveryAsync{T}"/> catches to trigger its
/// bounded device-replacement recovery. Updated for Phase 25E: it now maps to its own distinct
/// <see cref="ApiErrorCategory.DeviceLimitExceeded"/> category (rather than the generic 403 catch-all), so the UI
/// can offer explicit replacement — see <see cref="DeviceReplacementCoordinatorTests"/> for the confirmation flow.
/// </summary>
public class TrialDeviceRecoveryClientTests
{
    private static AutraxisApiClient CreateClient(FakeHttpMessageHandler handler)
    {
        var tokens = new FakeTokenProvider();
        tokens.SilentTokens.Enqueue("valid-token");
        return new AutraxisApiClient(new HttpClient(handler), tokens,
            new AuthenticationOptions("https://example-test-tenant.example/", "test-client", "api://test/access", "https://api.example.test/"));
    }

    [Fact]
    public async Task DeviceLimitExceeded_DoesNotMapToDeviceNotAuthorized_SoTheCoordinatorsRecoveryNeverTriggers()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.Forbidden, """{"status":"device_limit_exceeded"}""");

        var ex = await Assert.ThrowsAsync<AutraxisApiException>(() => CreateClient(handler).RegisterDeviceAsync("Windows", "New PC"));

        Assert.NotEqual(ApiErrorCategory.DeviceNotAuthorized, ex.Category);
        Assert.Equal(ApiErrorCategory.DeviceLimitExceeded, ex.Category); // Phase 25E: its own distinct category, not the generic 403 catch-all
        Assert.Equal("device_limit_exceeded", ex.BackendStatus);
    }

    [Fact]
    public async Task DeviceNotAuthorized_DoesMapDistinctly_ThisIsTheOnlyCaseTheCoordinatorRecoversFrom()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.Forbidden, """{"status":"device_not_authorized"}""");

        var ex = await Assert.ThrowsAsync<AutraxisApiException>(() => CreateClient(handler).StartTranslationSessionAsync(Guid.NewGuid(), null, "en-US:de-DE"));

        Assert.Equal(ApiErrorCategory.DeviceNotAuthorized, ex.Category);
    }
}
