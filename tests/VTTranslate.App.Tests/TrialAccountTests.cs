using System.Net;
using VTTranslate.App.Account;
using VTTranslate.App.Api;
using VTTranslate.App.Authentication;

namespace VTTranslate.App.Tests;

/// <summary>
/// Phase 25B — client behavior for the approved free trial: the trial allowance shown on My Account (never the paid
/// UsageLimitSecondsPerPeriod), the upgrade messages for machine-readable denial codes, and the wire contract with the
/// backend's <c>code</c> field. No payment is implemented and no upgrade URL exists — wording only.
/// </summary>
public class TrialAccountTests
{
    private static readonly DateTimeOffset Start = new(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);

    private static SubscriptionDto TrialSubscription() => new("Trial", Guid.NewGuid(), Start, Start.AddDays(14), false);

    private static UsageSummaryDto UsageWithTrial(double used, bool ended = false, bool exhausted = false, double limit = 36000) => new(
        "2026-03", used, used + 100,
        new TrialUsageDto("Trial", Start, Start.AddDays(14), used, limit, Math.Max(0, limit - used), ended, exhausted));

    private static async Task<AccountViewModel> LoadAsync(UsageSummaryDto usage, EntitlementsDto? entitlements = null)
    {
        var fake = new FakeAutraxisApiClient();
        fake.SubscriptionResponses.Enqueue(TrialSubscription);
        fake.EntitlementsResponses.Enqueue(() => entitlements ?? new EntitlementsDto("Trial", new Dictionary<string, string>
        {
            ["TrialDurationDays"] = "14", ["TrialUsageLimitSeconds"] = "36000", ["MaxActiveDevices"] = "1",
        }));
        fake.UsageResponses.Enqueue(() => usage);
        var vm = new AccountViewModel(fake, new RecordingDiagnosticLogger());
        await vm.LoadAsync();
        return vm;
    }

    [Fact]
    public async Task TrialCustomer_SeesPeriod_UsedOfTenHours_Remaining_AndEndDate()
    {
        var vm = await LoadAsync(UsageWithTrial(used: 9000));

        Assert.True(vm.HasTrial);
        Assert.Equal("Trial · 14-day period", vm.TrialHeadline);
        Assert.Equal("2h 30m / 10 hours used", vm.TrialUsageDisplay);
        Assert.Equal("7h 30m remaining", vm.TrialRemainingDisplay);
        Assert.Equal("Trial ends Mar 15, 2026", vm.TrialEndDisplay);
        Assert.Equal("", vm.TrialUpgradeMessage);
    }

    [Fact]
    public async Task TrialCustomer_NeverSeesThePaidUsageLimitPresentedAsTheTrialAllowance()
    {
        // A (mis)configured trial plan that ALSO carries the paid key must not leak into what the trial shows.
        var entitlements = new EntitlementsDto("Trial", new Dictionary<string, string>
        {
            ["TrialUsageLimitSeconds"] = "36000", ["UsageLimitSecondsPerPeriod"] = "999999",
        });

        var vm = await LoadAsync(UsageWithTrial(used: 3600), entitlements);

        Assert.Equal("", vm.UsageDisplay);                                        // the monthly / paid-limit line is suppressed
        Assert.Contains("10 hours", vm.TrialUsageDisplay);
        Assert.DoesNotContain("277", vm.TrialUsageDisplay);                        // 999,999 s ≈ 277 h must not appear
        Assert.DoesNotContain("999", vm.TrialUsageDisplay);
    }

    [Fact]
    public async Task ExhaustedTrial_ShowsUpgradeMessage()
    {
        var vm = await LoadAsync(UsageWithTrial(used: 36000, exhausted: true));

        Assert.Equal("Your trial allowance has been used. Upgrade to Premium to continue using Voice-Translater.", vm.TrialUpgradeMessage);
        Assert.Equal("0s remaining", vm.TrialRemainingDisplay);
    }

    [Fact]
    public async Task EndedTrial_ShowsEndedWording_AndUpgradeMessage()
    {
        var vm = await LoadAsync(UsageWithTrial(used: 1200, ended: true));

        Assert.Equal("Trial ended Mar 15, 2026", vm.TrialEndDisplay);
        Assert.Equal("Your trial has ended. Upgrade to Premium to continue using Voice-Translater.", vm.TrialUpgradeMessage);
    }

    [Fact]
    public async Task NonTrialCustomer_KeepsTheExistingMonthlyUsageDisplay()
    {
        var fake = new FakeAutraxisApiClient();
        fake.SubscriptionResponses.Enqueue(() => new SubscriptionDto("Active", Guid.NewGuid(), Start, Start.AddDays(30), false));
        fake.EntitlementsResponses.Enqueue(() => new EntitlementsDto("Active", new Dictionary<string, string> { ["UsageLimitSecondsPerPeriod"] = "36000" }));
        fake.UsageResponses.Enqueue(() => new UsageSummaryDto("2026-03", 3600, 3700));   // no trial object at all
        var vm = new AccountViewModel(fake, new RecordingDiagnosticLogger());

        await vm.LoadAsync();

        Assert.False(vm.HasTrial);
        Assert.Contains("used this period", vm.UsageDisplay);
        Assert.Contains("of", vm.UsageDisplay);
    }

    // ---- denial messages ----

    [Theory]
    [InlineData("trial_expired", "Your trial has ended. Upgrade to Premium to continue using Voice-Translater.")]
    [InlineData("trial_usage_exhausted", "Your trial allowance has been used. Upgrade to Premium to continue using Voice-Translater.")]
    public void SessionDenial_WithTrialCode_ShowsTheUpgradeMessage(string code, string expected)
    {
        var ex = new AutraxisApiException(ApiErrorCategory.EntitlementDenied, "entitlement_denied", inner: null, backendCode: code);

        Assert.Equal(expected, MainViewModel.MapApiErrorToMessage(ex));
    }

    [Fact]
    public void SessionDenial_WithoutATrialCode_KeepsTheExistingMyAccountMessage()
    {
        var message = MainViewModel.MapApiErrorToMessage(new AutraxisApiException(ApiErrorCategory.EntitlementDenied, "entitlement_denied"));

        Assert.Contains("My Account", message);
    }

    // ---- wire contract with the backend ----

    private static AutraxisApiClient CreateClient(FakeHttpMessageHandler handler)
    {
        var tokens = new FakeTokenProvider();
        tokens.SilentTokens.Enqueue("valid-token");
        return new AutraxisApiClient(new HttpClient(handler), tokens,
            new AuthenticationOptions("https://example-test-tenant.example/", "test-client", "api://test/access", "https://api.example.test/"));
    }

    [Theory]
    [InlineData("""{"status":"usage_limit_exceeded","code":"trial_usage_exhausted"}""", "trial_usage_exhausted")]
    [InlineData("""{"status":"entitlement_denied","code":"trial_expired"}""", "trial_expired")]
    public async Task StartSession_403WithCode_MapsToEntitlementDenied_AndCarriesTheCode(string body, string expectedCode)
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.Forbidden, body);

        var ex = await Assert.ThrowsAsync<AutraxisApiException>(() =>
            CreateClient(handler).StartTranslationSessionAsync(Guid.NewGuid(), null, "en-US:de-DE"));

        Assert.Equal(ApiErrorCategory.EntitlementDenied, ex.Category);
        Assert.Equal(expectedCode, ex.BackendCode);
    }

    [Fact]
    public async Task GetUsage_MapsTheTrialObject_WhenPresent()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK,
            """{"periodBucket":"2026-03","serverDerivedSeconds":9000,"clientReportedSeconds":0,"trial":{"status":"Trial","periodStart":"2026-03-01T09:00:00+00:00","periodEnd":"2026-03-15T09:00:00+00:00","usedSeconds":9000,"limitSeconds":36000,"remainingSeconds":27000,"ended":false,"exhausted":false}}""");

        var dto = await CreateClient(handler).GetUsageAsync();

        Assert.NotNull(dto.Trial);
        Assert.Equal(36000, dto.Trial!.LimitSeconds);
        Assert.Equal(27000, dto.Trial.RemainingSeconds);
        Assert.False(dto.Trial.Ended);
    }

    [Fact]
    public async Task GetUsage_WithNullTrial_MapsToNull()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, """{"periodBucket":"2026-03","serverDerivedSeconds":1,"clientReportedSeconds":0,"trial":null}""");

        var dto = await CreateClient(handler).GetUsageAsync();

        Assert.Null(dto.Trial);
    }
}
