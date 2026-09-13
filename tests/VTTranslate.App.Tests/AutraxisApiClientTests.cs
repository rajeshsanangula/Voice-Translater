using System.Net;
using VTTranslate.App.Api;
using VTTranslate.App.Authentication;

namespace VTTranslate.App.Tests;

/// <summary>
/// Phase 7.1 — deterministic tests for the bounded 401 policy (docs §13) and the
/// explicit "403 never renews" rule (docs §14). No real network, no real MSAL/browser.
/// </summary>
public class AutraxisApiClientTests
{
    private static AuthenticationOptions Options() => new(
        authority: "https://example-test-tenant.example/", clientId: "test-client",
        apiScope: "api://test/access", apiBaseUrl: "https://api.example.test/");

    private static AutraxisApiClient CreateClient(FakeHttpMessageHandler handler, FakeTokenProvider tokenProvider)
    {
        var httpClient = new HttpClient(handler);
        return new AutraxisApiClient(httpClient, tokenProvider, Options());
    }

    [Fact]
    public async Task Success_AttachesBearerToken_ReturnsDeserializedBody()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, """{"displayName":"Sanan","preferredLanguagePair":null,"updatedAt":"2026-01-01T00:00:00Z"}""");
        var tokenProvider = new FakeTokenProvider();
        tokenProvider.SilentTokens.Enqueue("valid-token");

        var client = CreateClient(handler, tokenProvider);
        var profile = await client.GetProfileAsync();

        Assert.Equal("Sanan", profile.DisplayName);
        Assert.Equal("valid-token", handler.RequestBearerTokens[0]);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task Unauthorized_Once_RenewsTokenExactlyOnce_RetriesExactlyOnce_ThenSucceeds()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.Unauthorized);
        handler.Enqueue(HttpStatusCode.OK, """{"displayName":null,"preferredLanguagePair":null,"updatedAt":"2026-01-01T00:00:00Z"}""");

        var tokenProvider = new FakeTokenProvider();
        tokenProvider.SilentTokens.Enqueue("stale-token");
        tokenProvider.ForceRefreshTokens.Enqueue("fresh-token");

        var client = CreateClient(handler, tokenProvider);
        var profile = await client.GetProfileAsync();

        Assert.Equal(2, handler.RequestCount); // original + exactly one retry
        Assert.Equal("stale-token", handler.RequestBearerTokens[0]);
        Assert.Equal("fresh-token", handler.RequestBearerTokens[1]);
        Assert.Equal(1, tokenProvider.ForceRefreshCallCount); // exactly one forced renewal
        Assert.Null(profile.DisplayName);
    }

    [Fact]
    public async Task Unauthorized_Twice_FailsClosed_NeverRetriesAThirdTime()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.Unauthorized);
        handler.Enqueue(HttpStatusCode.Unauthorized); // renewed token STILL rejected

        var tokenProvider = new FakeTokenProvider();
        tokenProvider.SilentTokens.Enqueue("stale-token");
        tokenProvider.ForceRefreshTokens.Enqueue("still-rejected-token");

        var client = CreateClient(handler, tokenProvider);
        var ex = await Assert.ThrowsAsync<AutraxisApiException>(() => client.GetProfileAsync());

        Assert.Equal(ApiErrorCategory.AuthenticationRequired, ex.Category);
        Assert.Equal(2, handler.RequestCount); // NEVER a third attempt
        Assert.Equal(1, tokenProvider.ForceRefreshCallCount); // renewal attempted exactly once, never twice
    }

    [Fact]
    public async Task Forbidden_NeverTriggersTokenRenewal()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.Forbidden, """{"status":"entitlement_denied"}""");

        var tokenProvider = new FakeTokenProvider();
        tokenProvider.SilentTokens.Enqueue("valid-token");

        var client = CreateClient(handler, tokenProvider);
        var ex = await Assert.ThrowsAsync<AutraxisApiException>(() => client.GetProfileAsync());

        Assert.Equal(ApiErrorCategory.EntitlementDenied, ex.Category);
        Assert.Equal(1, handler.RequestCount); // no retry at all
        Assert.Equal(0, tokenProvider.ForceRefreshCallCount); // 403 must NEVER renew
    }

    [Fact]
    public async Task AccountNotUsable_MapsToStableCategory_NeverLeaksWhichStateApplies()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.Forbidden, """{"status":"account_suspended"}""");
        var tokenProvider = new FakeTokenProvider();
        tokenProvider.SilentTokens.Enqueue("valid-token");

        var client = CreateClient(handler, tokenProvider);
        var ex = await Assert.ThrowsAsync<AutraxisApiException>(() => client.GetProfileAsync());

        Assert.Equal(ApiErrorCategory.AccountNotUsable, ex.Category);
    }

    [Fact]
    public async Task DeviceNotAuthorized_MapsToStableCategory()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.Forbidden, """{"status":"device_not_authorized"}""");
        var tokenProvider = new FakeTokenProvider();
        tokenProvider.SilentTokens.Enqueue("valid-token");

        var client = CreateClient(handler, tokenProvider);
        var ex = await Assert.ThrowsAsync<AutraxisApiException>(() => client.GetProfileAsync());

        Assert.Equal(ApiErrorCategory.DeviceNotAuthorized, ex.Category);
    }

    [Fact]
    public async Task NoTokenAvailable_NeverLaunchesInteractive_MapsToAuthenticationRequired()
    {
        var handler = new FakeHttpMessageHandler();
        var tokenProvider = new FakeTokenProvider();
        // SilentTokens empty, allowInteractive is always false inside the API client
        // (docs §20: the API client never launches a browser itself).

        var client = CreateClient(handler, tokenProvider);
        var ex = await Assert.ThrowsAsync<AutraxisApiException>(() => client.GetProfileAsync());

        Assert.Equal(ApiErrorCategory.AuthenticationRequired, ex.Category);
        Assert.Equal(0, tokenProvider.InteractiveCallCount); // never launches a browser from the API client
        Assert.Equal(0, handler.RequestCount); // never even reaches the network without a token
    }

    [Fact]
    public async Task Cancellation_PropagatesWithoutRetrying()
    {
        var handler = new FakeHttpMessageHandler();
        var tokenProvider = new FakeTokenProvider();
        tokenProvider.SilentTokens.Enqueue("valid-token");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var client = CreateClient(handler, tokenProvider);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetProfileAsync(cts.Token));
    }

    [Fact]
    public void RequiresHttps_RejectsPlainHttpNonLocalBaseUrl()
    {
        var handler = new FakeHttpMessageHandler();
        var httpClient = new HttpClient(handler);
        var insecureOptions = new AuthenticationOptions("https://tenant.example/", "client", "scope", "http://not-localhost.example/");

        Assert.Throws<InvalidOperationException>(() => new AutraxisApiClient(httpClient, new FakeTokenProvider(), insecureOptions));
    }
}
