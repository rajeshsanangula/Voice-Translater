using VTTranslate.App.Account;
using VTTranslate.App.Api;

namespace VTTranslate.App.Tests;

/// <summary>
/// Phase 7.3 — deterministic tests for <see cref="AccountViewModel"/> against
/// <see cref="FakeAutraxisApiClient"/>. No real backend dependency (docs §20).
/// </summary>
public class AccountViewModelTests
{
    private static SubscriptionDto ActiveSubscription(bool cancelAtPeriodEnd = false) => new(
        "Active", Guid.Parse("22222222-2222-2222-2222-222222222222"),
        new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero),
        cancelAtPeriodEnd);

    private static EntitlementsDto EntitlementsWithLimit() => new(
        "Active", new Dictionary<string, string> { ["UsageLimitSecondsPerPeriod"] = "36000" });

    private static EntitlementsDto EntitlementsWithoutLimit() => new(
        "Active", new Dictionary<string, string> { ["MaxActiveDevices"] = "3" });

    private static UsageSummaryDto Usage(double serverDerived = 3600, double clientReported = 3700) => new(
        "2026-01", serverDerived, clientReported);

    // ---- 1/2/3/4: successful load, subscription/entitlement/usage data displayed ----
    [Fact]
    public async Task LoadAsync_Success_DisplaysSubscriptionEntitlementAndUsageData()
    {
        var fake = new FakeAutraxisApiClient();
        fake.SubscriptionResponses.Enqueue(() => ActiveSubscription());
        fake.EntitlementsResponses.Enqueue(EntitlementsWithLimit);
        fake.UsageResponses.Enqueue(() => Usage(3600));

        var vm = new AccountViewModel(fake, new RecordingDiagnosticLogger());
        await vm.LoadAsync();

        Assert.True(vm.HasSubscription);
        Assert.Equal("Active", vm.SubscriptionStatusDisplay);
        Assert.Contains("22222222-2222-2222-2222-222222222222", vm.PlanDisplay);
        Assert.False(vm.CancelAtPeriodEnd);
        Assert.False(vm.IsLoadingSubscription);
        Assert.False(vm.IsLoadingEntitlements);
        Assert.False(vm.IsLoadingUsage);
        Assert.Null(vm.SubscriptionLoadError);
        Assert.Null(vm.EntitlementsLoadError);
        Assert.Null(vm.UsageLoadError);
    }

    // ---- 5: usage limit present ----
    [Fact]
    public async Task LoadAsync_UsageLimitPresent_UsageDisplayShowsUsedOfLimit()
    {
        var fake = new FakeAutraxisApiClient();
        fake.SubscriptionResponses.Enqueue(() => ActiveSubscription());
        fake.EntitlementsResponses.Enqueue(EntitlementsWithLimit);
        fake.UsageResponses.Enqueue(() => Usage(3600));

        var vm = new AccountViewModel(fake, new RecordingDiagnosticLogger());
        await vm.LoadAsync();

        Assert.True(vm.HasUsageLimit);
        Assert.Contains("of", vm.UsageDisplay); // "X of Y used this period" shape
        Assert.Contains("used this period", vm.UsageDisplay);
    }

    // ---- 6: usage limit absent ----
    [Fact]
    public async Task LoadAsync_UsageLimitAbsent_UsageDisplayShowsUsedOnly_NeverInventsALimit()
    {
        var fake = new FakeAutraxisApiClient();
        fake.SubscriptionResponses.Enqueue(() => ActiveSubscription());
        fake.EntitlementsResponses.Enqueue(EntitlementsWithoutLimit);
        fake.UsageResponses.Enqueue(() => Usage(3600));

        var vm = new AccountViewModel(fake, new RecordingDiagnosticLogger());
        await vm.LoadAsync();

        Assert.False(vm.HasUsageLimit);
        Assert.DoesNotContain(" of ", vm.UsageDisplay);
        Assert.Contains("used this period", vm.UsageDisplay);
    }

    // ---- 7: no-subscription state ----
    [Fact]
    public async Task LoadAsync_NoSubscription_IsAValidState_NotAnError()
    {
        var fake = new FakeAutraxisApiClient();
        fake.SubscriptionResponses.Enqueue(() => throw new AutraxisApiException(ApiErrorCategory.NoSubscription, "no_subscription"));
        fake.EntitlementsResponses.Enqueue(() => throw new AutraxisApiException(ApiErrorCategory.NoSubscription, "no_subscription"));
        fake.UsageResponses.Enqueue(() => Usage(0, 0)); // usage never 404s for no-subscription — always returns a summary

        var vm = new AccountViewModel(fake, new RecordingDiagnosticLogger());
        await vm.LoadAsync();

        Assert.False(vm.HasSubscription);
        Assert.Null(vm.SubscriptionLoadError); // valid state, not an error
        Assert.Null(vm.EntitlementsLoadError);
        Assert.True(vm.NoSubscriptionMessageVisible);
        Assert.False(vm.CanCancel);
    }

    // ---- 8: network failure ----
    [Fact]
    public async Task LoadAsync_NetworkFailure_SurfacesAsLoadError()
    {
        var fake = new FakeAutraxisApiClient();
        fake.SubscriptionResponses.Enqueue(() => throw new AutraxisApiException(ApiErrorCategory.NetworkUnavailable));
        fake.EntitlementsResponses.Enqueue(EntitlementsWithLimit);
        fake.UsageResponses.Enqueue(() => Usage());

        var vm = new AccountViewModel(fake, new RecordingDiagnosticLogger());
        await vm.LoadAsync();

        Assert.NotNull(vm.SubscriptionLoadError);
        Assert.False(vm.HasSubscription);
        Assert.False(vm.NoSubscriptionMessageVisible); // an error, not a "confirmed no subscription" state
    }

    // ---- 9: service failure ----
    [Fact]
    public async Task LoadAsync_ServiceUnavailable_SurfacesAsLoadError()
    {
        var fake = new FakeAutraxisApiClient();
        fake.SubscriptionResponses.Enqueue(() => ActiveSubscription());
        fake.EntitlementsResponses.Enqueue(EntitlementsWithLimit);
        fake.UsageResponses.Enqueue(() => throw new AutraxisApiException(ApiErrorCategory.ServiceUnavailable));

        var vm = new AccountViewModel(fake, new RecordingDiagnosticLogger());
        await vm.LoadAsync();

        Assert.NotNull(vm.UsageLoadError);
        // Independent failure: subscription/entitlement data already loaded successfully
        // must survive the usage section's failure (docs §9).
        Assert.True(vm.HasSubscription);
        Assert.Equal("Active", vm.SubscriptionStatusDisplay);
    }

    // ---- Independent loading failures: one section's failure never destroys another's success ----
    [Fact]
    public async Task LoadAsync_OneSectionFails_OtherSuccessfulSectionsArePreserved()
    {
        var fake = new FakeAutraxisApiClient();
        fake.SubscriptionResponses.Enqueue(() => ActiveSubscription());
        fake.EntitlementsResponses.Enqueue(() => throw new AutraxisApiException(ApiErrorCategory.ServiceUnavailable));
        fake.UsageResponses.Enqueue(() => Usage(1800));

        var vm = new AccountViewModel(fake, new RecordingDiagnosticLogger());
        await vm.LoadAsync();

        Assert.True(vm.HasSubscription); // subscription succeeded independently
        Assert.NotNull(vm.EntitlementsLoadError); // entitlements failed
        Assert.Contains("used this period", vm.UsageDisplay); // usage succeeded independently
    }

    // ---- 10: retry ----
    [Fact]
    public async Task Retry_AfterFailure_CanSucceed()
    {
        var fake = new FakeAutraxisApiClient();
        fake.SubscriptionResponses.Enqueue(() => throw new AutraxisApiException(ApiErrorCategory.ServiceUnavailable));
        fake.EntitlementsResponses.Enqueue(EntitlementsWithLimit);
        fake.UsageResponses.Enqueue(() => Usage());

        var vm = new AccountViewModel(fake, new RecordingDiagnosticLogger());
        await vm.LoadAsync();
        Assert.NotNull(vm.SubscriptionLoadError);

        fake.SubscriptionResponses.Enqueue(() => ActiveSubscription());
        fake.EntitlementsResponses.Enqueue(EntitlementsWithLimit);
        fake.UsageResponses.Enqueue(() => Usage());
        await vm.LoadAsync(); // simulates the Retry button re-invoking LoadCommand

        Assert.Null(vm.SubscriptionLoadError);
        Assert.True(vm.HasSubscription);
    }

    // ---- 11: successful cancellation ----
    [Fact]
    public async Task ConfirmCancelImmediately_Success_CallsCancelWithImmediateTrue_ThenRefreshes()
    {
        var fake = new FakeAutraxisApiClient();
        fake.SubscriptionResponses.Enqueue(() => ActiveSubscription());
        fake.EntitlementsResponses.Enqueue(EntitlementsWithLimit);
        fake.UsageResponses.Enqueue(() => Usage());
        var vm = new AccountViewModel(fake, new RecordingDiagnosticLogger());
        await vm.LoadAsync();

        fake.CancelSubscriptionResponses.Enqueue(() => new CancelSubscriptionResultDto("Cancelled", false));
        fake.SubscriptionResponses.Enqueue(() => new SubscriptionDto("Cancelled", ActiveSubscription().PlanId, ActiveSubscription().CurrentPeriodStart, ActiveSubscription().CurrentPeriodEnd, false));

        vm.ConfirmCancelImmediatelyCommand.Execute(null);
        await WaitUntil(() => !vm.IsCancelling && fake.SubscriptionResponses.Count == 0);

        Assert.Equal(1, fake.CancelSubscriptionCallCount);
        Assert.True(fake.CancelSubscriptionCalls[0]); // immediate=true
        Assert.Null(vm.CancelError);
        Assert.False(vm.ShowCancelConfirmation);
        Assert.Equal("Cancelled", vm.SubscriptionStatusDisplay); // authoritative post-cancel refresh (docs §13)
    }

    // ---- 12: failed cancellation ----
    [Fact]
    public async Task ConfirmCancelImmediately_Failure_NeverClaimsSuccess_StillRefreshes()
    {
        var fake = new FakeAutraxisApiClient();
        fake.SubscriptionResponses.Enqueue(() => ActiveSubscription());
        fake.EntitlementsResponses.Enqueue(EntitlementsWithLimit);
        fake.UsageResponses.Enqueue(() => Usage());
        var vm = new AccountViewModel(fake, new RecordingDiagnosticLogger());
        await vm.LoadAsync();

        fake.CancelSubscriptionResponses.Enqueue(() => throw new AutraxisApiException(ApiErrorCategory.ServiceUnavailable));
        fake.SubscriptionResponses.Enqueue(() => ActiveSubscription()); // still Active — cancel never took effect

        vm.ConfirmCancelImmediatelyCommand.Execute(null);
        await WaitUntil(() => !vm.IsCancelling && fake.SubscriptionResponses.Count == 0);

        Assert.NotNull(vm.CancelError);
        Assert.Equal("Active", vm.SubscriptionStatusDisplay); // never optimistically shown as cancelled
    }

    // ---- 13: authoritative post-cancel refresh already covered above (11/12) ----

    // ---- 14: cancellation confirmation behavior ----
    [Fact]
    public async Task RequestCancel_ShowsConfirmation_DismissHidesIt_NeverCancelsSilently()
    {
        var fake = new FakeAutraxisApiClient();
        fake.SubscriptionResponses.Enqueue(() => ActiveSubscription());
        fake.EntitlementsResponses.Enqueue(EntitlementsWithLimit);
        fake.UsageResponses.Enqueue(() => Usage());
        var vm = new AccountViewModel(fake, new RecordingDiagnosticLogger());
        await vm.LoadAsync();

        Assert.False(vm.ShowCancelConfirmation);
        vm.RequestCancelCommand.Execute(null);
        Assert.True(vm.ShowCancelConfirmation);
        vm.DismissCancelConfirmationCommand.Execute(null);
        Assert.False(vm.ShowCancelConfirmation);
        Assert.Equal(0, fake.CancelSubscriptionCallCount); // dismiss never calls the backend
    }

    // ---- 15: loading state ----
    [Fact]
    public async Task LoadAsync_SetsLoadingFlagsDuringFetch()
    {
        var gate = new TaskCompletionSource();
        var fake = new FakeAutraxisApiClient();
        fake.SubscriptionResponseGate = () => gate.Task; // awaited, never blocked-on — see FakeAutraxisApiClient's own doc comment
        fake.SubscriptionResponses.Enqueue(() => ActiveSubscription());
        fake.EntitlementsResponses.Enqueue(EntitlementsWithLimit);
        fake.UsageResponses.Enqueue(() => Usage());

        var vm = new AccountViewModel(fake, new RecordingDiagnosticLogger());
        var loadTask = vm.LoadAsync();

        // Give the subscription fetch a moment to enter its "loading" state before releasing it.
        await Task.Delay(20);
        Assert.True(vm.IsLoadingSubscription);

        gate.SetResult();
        await loadTask;
        Assert.False(vm.IsLoadingSubscription);
    }

    // ---- 16: cancellation state (IsCancelling) ----
    [Fact]
    public async Task ConfirmCancel_SetsIsCancellingDuringTheCall()
    {
        var gate = new TaskCompletionSource();
        var fake = new FakeAutraxisApiClient();
        fake.SubscriptionResponses.Enqueue(() => ActiveSubscription());
        fake.EntitlementsResponses.Enqueue(EntitlementsWithLimit);
        fake.UsageResponses.Enqueue(() => Usage());
        var vm = new AccountViewModel(fake, new RecordingDiagnosticLogger());
        await vm.LoadAsync();

        fake.CancelSubscriptionResponseGate = () => gate.Task;
        fake.CancelSubscriptionResponses.Enqueue(() => new CancelSubscriptionResultDto("Cancelled", false));
        fake.SubscriptionResponses.Enqueue(() => ActiveSubscription());

        vm.ConfirmCancelImmediatelyCommand.Execute(null);
        await Task.Delay(20);
        Assert.True(vm.IsCancelling);

        gate.SetResult();
        await WaitUntil(() => !vm.IsCancelling);
        Assert.False(vm.IsCancelling);
    }

    // ---- 17: cancellation while already canceled ----
    [Fact]
    public async Task CanCancel_False_WhenSubscriptionIsAlreadyTerminal()
    {
        var fake = new FakeAutraxisApiClient();
        fake.SubscriptionResponses.Enqueue(() => new SubscriptionDto("Cancelled", Guid.NewGuid(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, false));
        fake.EntitlementsResponses.Enqueue(EntitlementsWithLimit);
        fake.UsageResponses.Enqueue(() => Usage());

        var vm = new AccountViewModel(fake, new RecordingDiagnosticLogger());
        await vm.LoadAsync();

        Assert.False(vm.CanCancel);
        Assert.False(vm.RequestCancelCommand.CanExecute(null));
    }

    // ---- 18: stale account state cannot survive account switch/sign-out ----
    [Fact]
    public async Task Reset_ClearsAllLoadedState_NoStaleDataSurvives()
    {
        var fake = new FakeAutraxisApiClient();
        fake.SubscriptionResponses.Enqueue(() => ActiveSubscription());
        fake.EntitlementsResponses.Enqueue(EntitlementsWithLimit);
        fake.UsageResponses.Enqueue(() => Usage(3600));
        var vm = new AccountViewModel(fake, new RecordingDiagnosticLogger());
        await vm.LoadAsync();
        Assert.True(vm.HasSubscription);

        vm.Reset();

        Assert.False(vm.HasSubscription);
        Assert.Equal("", vm.SubscriptionStatusDisplay);
        Assert.Equal("", vm.PlanDisplay);
        Assert.Equal("", vm.CurrentPeriodDisplay);
        Assert.Equal("", vm.UsageDisplay);
        Assert.False(vm.HasUsageLimit);
        Assert.False(vm.ShowCancelConfirmation);
        Assert.Null(vm.SubscriptionLoadError);
        Assert.Null(vm.CancelError);
    }

    // ---- EntitlementDenied message points to My Account ----
    [Fact]
    public void EntitlementDeniedMessage_MentionsMyAccount()
    {
        // Calls the actual production mapping (MainViewModel.MapApiErrorToMessage),
        // never a duplicated copy of the string, so this test fails if the real
        // message ever drifts from what it asserts.
        var message = MainViewModel.MapApiErrorToMessage(new AutraxisApiException(ApiErrorCategory.EntitlementDenied, "usage_denied"));
        Assert.Contains("My Account", message);
    }

    // ---- No provider token exposure ----
    [Fact]
    public async Task Load_And_Cancel_NeverLogProviderOrBearerTokenValues()
    {
        var logger = new RecordingDiagnosticLogger();
        var fake = new FakeAutraxisApiClient();
        fake.SubscriptionResponses.Enqueue(() => ActiveSubscription());
        fake.EntitlementsResponses.Enqueue(EntitlementsWithLimit);
        fake.UsageResponses.Enqueue(() => Usage());
        var vm = new AccountViewModel(fake, logger);
        await vm.LoadAsync();

        fake.CancelSubscriptionResponses.Enqueue(() => throw new AutraxisApiException(ApiErrorCategory.ServiceUnavailable));
        fake.SubscriptionResponses.Enqueue(() => ActiveSubscription());
        vm.ConfirmCancelImmediatelyCommand.Execute(null);
        await WaitUntil(() => !vm.IsCancelling);

        foreach (var (_, _, details) in logger.Events)
        {
            Assert.DoesNotContain("Bearer", details);
            Assert.DoesNotContain("token", details, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ---- Cancellation never changes client state before the server responds ----
    [Fact]
    public async Task Cancel_NeverChangesDisplayedStatusBeforeServerResponds()
    {
        var gate = new TaskCompletionSource();
        var fake = new FakeAutraxisApiClient();
        fake.SubscriptionResponses.Enqueue(() => ActiveSubscription());
        fake.EntitlementsResponses.Enqueue(EntitlementsWithLimit);
        fake.UsageResponses.Enqueue(() => Usage());
        var vm = new AccountViewModel(fake, new RecordingDiagnosticLogger());
        await vm.LoadAsync();

        fake.CancelSubscriptionResponseGate = () => gate.Task;
        fake.CancelSubscriptionResponses.Enqueue(() => new CancelSubscriptionResultDto("Cancelled", false));
        fake.SubscriptionResponses.Enqueue(() => new SubscriptionDto("Cancelled", Guid.NewGuid(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, false));

        vm.ConfirmCancelImmediatelyCommand.Execute(null);
        await Task.Delay(20);
        // The server hasn't responded yet — the client must still show the pre-cancel state.
        Assert.Equal("Active", vm.SubscriptionStatusDisplay);

        gate.SetResult();
        await WaitUntil(() => !vm.IsCancelling);
        Assert.Equal("Cancelled", vm.SubscriptionStatusDisplay);
    }

    private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 2000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition not met within timeout.");
            await Task.Delay(5);
        }
    }

}
