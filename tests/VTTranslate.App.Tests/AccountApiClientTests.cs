using System.Net;
using VTTranslate.App.Api;
using VTTranslate.App.Authentication;

namespace VTTranslate.App.Tests;

/// <summary>
/// Phase 7.3 — deterministic tests for the four new subscription/entitlement/usage/
/// cancellation methods on <see cref="AutraxisApiClient"/>. Verifies DTO mapping
/// against the actual backend response shapes (inspected directly from Program.cs
/// before implementation — see docs/phase-7.3-customer-subscription-and-usage-visibility.md
/// §5) and the exact request contract for cancellation. No real network.
/// </summary>
public class AccountApiClientTests
{
    private static AuthenticationOptions Options() => new(
        authority: "https://example-test-tenant.example/", clientId: "test-client",
        apiScope: "api://test/access", apiBaseUrl: "https://api.example.test/");

    private static AutraxisApiClient CreateClient(FakeHttpMessageHandler handler, FakeTokenProvider tokenProvider)
    {
        var httpClient = new HttpClient(handler);
        return new AutraxisApiClient(httpClient, tokenProvider, Options());
    }

    private static FakeTokenProvider TokenProvider()
    {
        var tp = new FakeTokenProvider();
        tp.SilentTokens.Enqueue("valid-token");
        return tp;
    }

    [Fact]
    public async Task GetSubscriptionAsync_Success_MapsExactBackendShape()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK,
            """{"status":"Active","planId":"11111111-1111-1111-1111-111111111111","currentPeriodStart":"2026-01-01T00:00:00Z","currentPeriodEnd":"2026-02-01T00:00:00Z","cancelAtPeriodEnd":false}""");

        var client = CreateClient(handler, TokenProvider());
        var dto = await client.GetSubscriptionAsync();

        Assert.Equal("Active", dto.Status);
        Assert.Equal(Guid.Parse("11111111-1111-1111-1111-111111111111"), dto.PlanId);
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), dto.CurrentPeriodStart);
        Assert.Equal(new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero), dto.CurrentPeriodEnd);
        Assert.False(dto.CancelAtPeriodEnd);
        Assert.Equal(HttpMethod.Get, handler.Requests[0].Method);
        Assert.Equal("/subscription", handler.Requests[0].Path);
    }

    [Fact]
    public async Task GetSubscriptionAsync_NoSubscription_MapsToNoSubscriptionCategory_NotBadRequest()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.NotFound, """{"status":"no_subscription"}""");

        var client = CreateClient(handler, TokenProvider());
        var ex = await Assert.ThrowsAsync<AutraxisApiException>(() => client.GetSubscriptionAsync());

        Assert.Equal(ApiErrorCategory.NoSubscription, ex.Category);
    }

    [Fact]
    public async Task GetSubscriptionAsync_ArbitraryNotFound_NeverInferredAsNoSubscription()
    {
        // A 404 WITHOUT the guaranteed "no_subscription" status string must never be
        // treated as the no-subscription case (docs §18) — it falls through to the
        // existing generic BadRequest mapping, unchanged.
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.NotFound, """{"status":"some_other_reason"}""");

        var client = CreateClient(handler, TokenProvider());
        var ex = await Assert.ThrowsAsync<AutraxisApiException>(() => client.GetSubscriptionAsync());

        Assert.Equal(ApiErrorCategory.BadRequest, ex.Category);
    }

    [Fact]
    public async Task GetEntitlementsAsync_Success_MapsStatusAndDictionary()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK,
            """{"subscriptionStatus":"Active","entitlements":{"UsageLimitSecondsPerPeriod":"36000","MaxActiveDevices":"3"}}""");

        var client = CreateClient(handler, TokenProvider());
        var dto = await client.GetEntitlementsAsync();

        Assert.Equal("Active", dto.SubscriptionStatus);
        Assert.Equal("36000", dto.Entitlements["UsageLimitSecondsPerPeriod"]);
        Assert.Equal("3", dto.Entitlements["MaxActiveDevices"]);
        Assert.Equal("/entitlements", handler.Requests[0].Path);
    }

    [Fact]
    public async Task GetEntitlementsAsync_NoSubscription_MapsToNoSubscriptionCategory()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.NotFound, """{"status":"no_subscription"}""");

        var client = CreateClient(handler, TokenProvider());
        var ex = await Assert.ThrowsAsync<AutraxisApiException>(() => client.GetEntitlementsAsync());

        Assert.Equal(ApiErrorCategory.NoSubscription, ex.Category);
    }

    [Fact]
    public async Task GetUsageAsync_Success_MapsServerDerivedAndClientReportedSeparately()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, """{"periodBucket":"2026-01","serverDerivedSeconds":1234.5,"clientReportedSeconds":1300.0}""");

        var client = CreateClient(handler, TokenProvider());
        var dto = await client.GetUsageAsync();

        Assert.Equal("2026-01", dto.PeriodBucket);
        Assert.Equal(1234.5, dto.ServerDerivedSeconds);
        Assert.Equal(1300.0, dto.ClientReportedSeconds);
        Assert.Equal("/usage", handler.Requests[0].Path);
    }

    [Fact]
    public async Task CancelSubscriptionAsync_Immediate_SendsExactRequestContract_MapsResponse()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, """{"status":"Cancelled","cancelAtPeriodEnd":false}""");

        var client = CreateClient(handler, TokenProvider());
        var dto = await client.CancelSubscriptionAsync(immediate: true);

        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.Equal("/subscription/cancel", handler.Requests[0].Path);
        Assert.Equal("""{"immediate":true}""", handler.RequestBodies[0]);
        Assert.Equal("Cancelled", dto.Status);
        Assert.False(dto.CancelAtPeriodEnd);
    }

    [Fact]
    public async Task CancelSubscriptionAsync_AtPeriodEnd_SendsExactRequestContract_MapsResponse()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, """{"status":"Active","cancelAtPeriodEnd":true}""");

        var client = CreateClient(handler, TokenProvider());
        var dto = await client.CancelSubscriptionAsync(immediate: false);

        Assert.Equal("""{"immediate":false}""", handler.RequestBodies[0]);
        Assert.Equal("Active", dto.Status);
        Assert.True(dto.CancelAtPeriodEnd);
    }

    [Fact]
    public async Task CancelSubscriptionAsync_NoSubscription_MapsToNoSubscriptionCategory()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.NotFound, """{"status":"no_subscription"}""");

        var client = CreateClient(handler, TokenProvider());
        var ex = await Assert.ThrowsAsync<AutraxisApiException>(() => client.CancelSubscriptionAsync(immediate: true));

        Assert.Equal(ApiErrorCategory.NoSubscription, ex.Category);
    }

    [Fact]
    public async Task CancelSubscriptionAsync_ServiceUnavailable_MapsToServiceUnavailableCategory()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.ServiceUnavailable);

        var client = CreateClient(handler, TokenProvider());
        var ex = await Assert.ThrowsAsync<AutraxisApiException>(() => client.CancelSubscriptionAsync(immediate: true));

        Assert.Equal(ApiErrorCategory.ServiceUnavailable, ex.Category);
    }
}
