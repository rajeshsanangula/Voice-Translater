using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace VTTranslate.Backend.Tests;

/// <summary>
/// Phase 6.3 review-gate addition, UPDATED for Phase 6.4. Exercises the actual ASP.NET
/// Core pipeline end to end via <see cref="WebApplicationFactory{TEntryPoint}"/>. Proves,
/// against the real running pipeline (not just the application-layer services in
/// isolation), that:
/// - health endpoints work and leak nothing sensitive, and remain anonymous;
/// - every placeholder route now REQUIRES authentication (Phase 6.4) — an anonymous
///   request gets 401, never the 501 placeholder body (which would leak the fact that
///   the route exists to an unauthenticated caller — a minor but real improvement this
///   phase makes over Phase 6.3's fully-anonymous placeholders);
/// - an unrelated path is a plain 404, not accidentally captured by a placeholder route.
///
/// Authenticated-flow tests (valid/expired/malformed tokens, account resolution, role
/// gating) live in <c>AuthenticationIntegrationTests</c>, which needs a test-only JWT
/// signing setup this file deliberately does not carry, to keep this file's scope to
/// exactly what Phase 6.3 already established plus the one behavior change above.
/// </summary>
public class ApiSecurityBoundaryTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task HealthLive_Returns200_WithOnlyALiteralStatus()
    {
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal("""{"status":"live"}""", body);
    }

    [Fact]
    public async Task HealthReady_Returns200_WithOnlyALiteralStatus()
    {
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal("""{"status":"ready"}""", body);
    }

    [Theory]
    [InlineData("/account")]
    [InlineData("/account/me")]
    [InlineData("/devices")]
    [InlineData("/subscription")]
    [InlineData("/entitlements")]
    [InlineData("/entitlements/provider-token")]
    [InlineData("/usage")]
    [InlineData("/internal/diagnostics")]
    public async Task PlaceholderRoutes_RequireAuthentication_AnonymousRequestIs401_NeverAFakeSuccess(string path)
    {
        // PHASE 6.4 BEHAVIOR CHANGE from Phase 6.3: these routes are now protected by
        // real authentication (app.UseAuthentication() + .RequireAuthorization()). An
        // anonymous request must be rejected BEFORE it ever reaches the 501 placeholder
        // body — 200 or 501-without-auth would both be wrong; 401 is correct.
        using var client = factory.CreateClient();
        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        // Must never accidentally echo anything resembling a real subscription/account
        // state, nor the placeholder body itself, to an unauthenticated caller.
        Assert.DoesNotContain("not_implemented", body);
        Assert.DoesNotContain("\"status\":\"Active\"", body);
        Assert.DoesNotContain("\"allowed\":true", body);
    }

    [Fact]
    public async Task UnrelatedPath_Is404_NotAccidentallyCapturedByAnyPlaceholderRoute()
    {
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/this-route-does-not-exist");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task HealthEndpoints_NeverAppearUnderneathAPlaceholderPrefix()
    {
        // Guards against a future accidental route-ordering regression where a
        // placeholder catch-all could shadow a real health route.
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/health/live");
        var body = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain("not_implemented", body);
    }
}
