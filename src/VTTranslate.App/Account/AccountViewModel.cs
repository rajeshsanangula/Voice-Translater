using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using VTTranslate.App.Api;
using VTTranslate.Core.Diagnostics;

namespace VTTranslate.App.Account;

/// <summary>
/// Phase 7.3 — "My Account" surface: subscription status, plan, current period,
/// entitlements-derived usage limit, current-period usage, and self-service
/// cancellation. See docs/phase-7.3-customer-subscription-and-usage-visibility.md.
///
/// Deliberately independent of <see cref="MainViewModel"/> — owns no translation
/// session, audio, provider credential, device registration, or authentication-token
/// state, and is never consulted by any of those systems. This view-model is
/// read-mostly and never authoritative: every value is fetched fresh from the
/// backend on <see cref="LoadAsync"/>, nothing is cached beyond this instance's own
/// in-memory fields, and a cancellation always re-fetches the server's authoritative
/// post-cancel state rather than assuming the requested outcome occurred (docs §13).
///
/// Plan name limitation (docs §11): the actual `GET /subscription`/`GET /entitlements`
/// response bodies (verified directly against Program.cs before this class was
/// written) carry only `PlanId` (a GUID) — no customer-facing plan name field exists
/// in either contract, and no other endpoint accessible to this client resolves one.
/// Rather than fabricate a name or add a new backend endpoint (explicitly out of
/// scope), <see cref="PlanDisplay"/> shows the raw plan identifier and this is called
/// out as a known contract limitation in the implementation report.
///
/// Lifecycle: <see cref="MainViewModel"/> owns this instance's lifetime — it is
/// created once an authenticated <see cref="IAutraxisApiClient"/> exists and is reset
/// (via <see cref="Reset"/>) on sign-out, so no state from one signed-in account can
/// ever be displayed after that account signs out or a different account signs in
/// (docs §16/§17).
/// </summary>
public sealed class AccountViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private readonly IAutraxisApiClient _apiClient;
    private readonly IDiagnosticLogger _logger;
    private CancellationTokenSource? _loadCts;

    private SubscriptionDto? _subscription;
    private EntitlementsDto? _entitlements;
    private UsageSummaryDto? _usage;

    public AccountViewModel(IAutraxisApiClient apiClient, IDiagnosticLogger? logger = null)
    {
        _apiClient = apiClient;
        _logger = logger ?? NullDiagnosticLogger.Instance;

        LoadCommand = new RelayCommand(async () => await LoadAsync(), () => true);
        RequestCancelCommand = new RelayCommand(() => { ShowCancelConfirmation = true; }, () => CanCancel && !ShowCancelConfirmation);
        DismissCancelConfirmationCommand = new RelayCommand(() => { ShowCancelConfirmation = false; }, () => ShowCancelConfirmation);
        ConfirmCancelAtPeriodEndCommand = new RelayCommand(async () => await CancelAsync(immediate: false), () => ShowCancelConfirmation && !IsCancelling && !CancelAtPeriodEnd);
        ConfirmCancelImmediatelyCommand = new RelayCommand(async () => await CancelAsync(immediate: true), () => ShowCancelConfirmation && !IsCancelling);
    }

    public ICommand LoadCommand { get; }
    public ICommand RequestCancelCommand { get; }
    public ICommand DismissCancelConfirmationCommand { get; }
    public ICommand ConfirmCancelAtPeriodEndCommand { get; }
    public ICommand ConfirmCancelImmediatelyCommand { get; }

    // ---- Subscription section ----
    private bool _isLoadingSubscription;
    public bool IsLoadingSubscription { get => _isLoadingSubscription; private set { _isLoadingSubscription = value; Raise(); Raise(nameof(NoSubscriptionMessageVisible)); } }

    private bool _hasSubscription;
    /// <summary>True once a subscription row exists for this account. False is a valid, expected state (docs §17/§18) — never treated as an error.</summary>
    public bool HasSubscription { get => _hasSubscription; private set { _hasSubscription = value; Raise(); Raise(nameof(CanCancel)); Raise(nameof(NoSubscriptionMessageVisible)); } }

    private string? _subscriptionLoadError;
    public string? SubscriptionLoadError { get => _subscriptionLoadError; private set { _subscriptionLoadError = value; Raise(); Raise(nameof(NoSubscriptionMessageVisible)); } }

    /// <summary>True only once loading has actually completed with no subscription found and no error — never shown while still loading or while an error is displayed instead (avoids a misleading flash of "no subscription" before the real result arrives).</summary>
    public bool NoSubscriptionMessageVisible => !IsLoadingSubscription && !HasSubscription && SubscriptionLoadError is null;

    public string SubscriptionStatusDisplay => _subscription is null ? "" : FriendlyStatus(_subscription.Status);

    /// <summary>See this class's own doc comment — the backend contract carries only a GUID, never a customer-facing name.</summary>
    public string PlanDisplay => _subscription is null ? "" : $"Plan {_subscription.PlanId}";

    public string CurrentPeriodDisplay => _subscription is null
        ? ""
        : $"{_subscription.CurrentPeriodStart:MMM d, yyyy} – {_subscription.CurrentPeriodEnd:MMM d, yyyy}";

    public bool CancelAtPeriodEnd => _subscription?.CancelAtPeriodEnd ?? false;

    /// <summary>Non-terminal, cancellable states only — matches the actual backend <c>SubscriptionStatus</c> enum (Trial/Active/GracePeriod/Expired/Cancelled/PastDue/Refunded). A terminal state (Expired/Cancelled/Refunded) has nothing left to cancel.</summary>
    public bool CanCancel => HasSubscription && _subscription is { } s &&
        s.Status is "Trial" or "Active" or "PastDue" or "GracePeriod";

    // ---- Entitlements section ----
    private bool _isLoadingEntitlements;
    public bool IsLoadingEntitlements { get => _isLoadingEntitlements; private set { _isLoadingEntitlements = value; Raise(); } }

    private string? _entitlementsLoadError;
    public string? EntitlementsLoadError { get => _entitlementsLoadError; private set { _entitlementsLoadError = value; Raise(); } }

    /// <summary>Entitlements are deliberately an open-ended key/value set (Entitlement.cs's own doc comment) — this only ever reads the one well-known key it understands, never enumerates or assumes a closed set.</summary>
    public bool HasUsageLimit => _entitlements?.Entitlements.ContainsKey("UsageLimitSecondsPerPeriod") == true
        && double.TryParse(_entitlements!.Entitlements["UsageLimitSecondsPerPeriod"], out _);

    private double? UsageLimitSeconds =>
        _entitlements is not null
        && _entitlements.Entitlements.TryGetValue("UsageLimitSecondsPerPeriod", out var raw)
        && double.TryParse(raw, out var seconds)
            ? seconds
            : null;

    // ---- Usage section ----
    private bool _isLoadingUsage;
    public bool IsLoadingUsage { get => _isLoadingUsage; private set { _isLoadingUsage = value; Raise(); } }

    private string? _usageLoadError;
    public string? UsageLoadError { get => _usageLoadError; private set { _usageLoadError = value; Raise(); } }

    /// <summary><see cref="UsageSummaryDto.ServerDerivedSeconds"/> only — the sole authoritative value (docs §12). <see cref="UsageSummaryDto.ClientReportedSeconds"/> is never shown as if it were authoritative.</summary>
    public string UsageDisplay
    {
        get
        {
            if (_usage is null) return "";
            // Phase 25B: a trial customer sees the trial allowance below — never the monthly figure, and never the paid
            // UsageLimitSecondsPerPeriod presented as if it were the trial allowance.
            if (_usage.Trial is not null) return "";
            var used = FormatDuration(_usage.ServerDerivedSeconds);
            return UsageLimitSeconds is { } limit ? $"{used} of {FormatDuration(limit)} used this period" : $"{used} used this period";
        }
    }

    // ---- Trial (Phase 25B) ----
    // Shown only when GET /usage reports a trial. The allowance is the trial-lifetime figure computed by the server from
    // the plan's TrialUsageLimitSeconds — this view model never reads UsageLimitSecondsPerPeriod for a trial.
    private TrialUsageDto? Trial => _usage?.Trial;

    public bool HasTrial => Trial is not null;

    public string TrialHeadline => Trial is null ? "" : $"Trial · {Math.Round((Trial.PeriodEnd - Trial.PeriodStart).TotalDays):F0}-day period";

    public string TrialUsageDisplay => Trial is null
        ? ""
        : Trial.LimitSeconds is { } limit
            ? $"{FormatTrialAmount(Trial.UsedSeconds)} / {FormatTrialAmount(limit)} used"
            : $"{FormatTrialAmount(Trial.UsedSeconds)} used";

    public string TrialRemainingDisplay => Trial?.RemainingSeconds is { } remaining ? $"{FormatDuration(remaining)} remaining" : "";

    public string TrialEndDisplay => Trial is null
        ? ""
        : Trial.Ended ? $"Trial ended {Trial.PeriodEnd:MMM d, yyyy}" : $"Trial ends {Trial.PeriodEnd:MMM d, yyyy}";

    /// <summary>Upgrade wording only — no payment is implemented and no upgrade link exists yet.</summary>
    public string TrialUpgradeMessage => Trial is null
        ? ""
        : Trial.Ended ? TrialMessages.Ended : Trial.Exhausted ? TrialMessages.AllowanceUsed : "";

    private void RaiseTrial()
    {
        Raise(nameof(HasTrial));
        Raise(nameof(TrialHeadline));
        Raise(nameof(TrialUsageDisplay));
        Raise(nameof(TrialRemainingDisplay));
        Raise(nameof(TrialEndDisplay));
        Raise(nameof(TrialUpgradeMessage));
    }

    private static string FormatTrialAmount(double seconds) =>
        seconds >= 3600 && Math.Abs(seconds % 3600) < 0.5
            ? (seconds / 3600 == 1 ? "1 hour" : $"{seconds / 3600:F0} hours")
            : FormatDuration(seconds);

    // ---- Cancellation ----
    private bool _isCancelling;
    public bool IsCancelling { get => _isCancelling; private set { _isCancelling = value; Raise(); } }

    private bool _showCancelConfirmation;
    public bool ShowCancelConfirmation { get => _showCancelConfirmation; private set { _showCancelConfirmation = value; Raise(); } }

    private string? _cancelError;
    public string? CancelError { get => _cancelError; private set { _cancelError = value; Raise(); } }

    /// <summary>
    /// Fetches subscription, entitlements, and usage concurrently (docs §9). Each
    /// section owns its own try/catch — a failure in one never discards data already
    /// successfully loaded by another (docs §9 "preserve independently successful
    /// sections"). A newer call always supersedes an older, still-in-flight one via
    /// linked cancellation — never a background poll, never a timer (docs §9/§27).
    /// </summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        _loadCts?.Cancel();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _loadCts = cts;
        var token = cts.Token;

        await Task.WhenAll(
            LoadSubscriptionAsync(token),
            LoadEntitlementsAsync(token),
            LoadUsageAsync(token));
    }

    private async Task LoadSubscriptionAsync(CancellationToken ct)
    {
        IsLoadingSubscription = true;
        SubscriptionLoadError = null;
        try
        {
            _subscription = await _apiClient.GetSubscriptionAsync(ct);
            HasSubscription = true;
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer LoadAsync call — not a failure, leave prior state untouched.
        }
        catch (AutraxisApiException ex) when (ex.Category == ApiErrorCategory.NoSubscription)
        {
            _subscription = null;
            HasSubscription = false; // valid, expected state per the backend's own contract (docs §18) — never an error
        }
        catch (AutraxisApiException ex)
        {
            _logger.Log("Account", "SubscriptionLoadFailed", $"category={ex.Category}");
            SubscriptionLoadError = MapErrorToMessage(ex.Category);
        }
        finally
        {
            IsLoadingSubscription = false;
            RaiseSubscriptionDisplays();
        }
    }

    private async Task LoadEntitlementsAsync(CancellationToken ct)
    {
        IsLoadingEntitlements = true;
        EntitlementsLoadError = null;
        try
        {
            _entitlements = await _apiClient.GetEntitlementsAsync(ct);
        }
        catch (OperationCanceledException)
        {
        }
        catch (AutraxisApiException ex) when (ex.Category == ApiErrorCategory.NoSubscription)
        {
            _entitlements = null; // same guaranteed contract as subscription — valid, not an error
        }
        catch (AutraxisApiException ex)
        {
            _logger.Log("Account", "EntitlementsLoadFailed", $"category={ex.Category}");
            EntitlementsLoadError = MapErrorToMessage(ex.Category);
        }
        finally
        {
            IsLoadingEntitlements = false;
            Raise(nameof(HasUsageLimit));
            Raise(nameof(UsageDisplay)); RaiseTrial();
        }
    }

    private async Task LoadUsageAsync(CancellationToken ct)
    {
        IsLoadingUsage = true;
        UsageLoadError = null;
        try
        {
            // Never 404s for "no subscription" — the backend computes a usage summary
            // unconditionally (UsageService.GetSummaryAsync, verified directly) — so no
            // NoSubscription special case applies here.
            _usage = await _apiClient.GetUsageAsync(ct);
        }
        catch (OperationCanceledException)
        {
        }
        catch (AutraxisApiException ex)
        {
            _logger.Log("Account", "UsageLoadFailed", $"category={ex.Category}");
            UsageLoadError = MapErrorToMessage(ex.Category);
        }
        finally
        {
            IsLoadingUsage = false;
            Raise(nameof(UsageDisplay)); RaiseTrial();
        }
    }

    /// <summary>
    /// Preserves the existing backend cancellation contract exactly
    /// (<c>CancelSubscriptionRequest(bool Immediate)</c>) — never invents a third
    /// policy. Always waits for the server response and re-fetches authoritative
    /// subscription state afterward, whether the call succeeded or failed (docs §13)
    /// — the client never optimistically assumes the resulting state.
    /// </summary>
    private async Task CancelAsync(bool immediate)
    {
        if (IsCancelling) return;
        IsCancelling = true;
        CancelError = null;
        try
        {
            var result = await _apiClient.CancelSubscriptionAsync(immediate, CancellationToken.None);
            _logger.Log("Account", "SubscriptionCancelSucceeded", $"immediate={immediate} resultStatus={result.Status}");
        }
        catch (AutraxisApiException ex)
        {
            _logger.Log("Account", "SubscriptionCancelFailed", $"immediate={immediate} category={ex.Category}");
            CancelError = MapErrorToMessage(ex.Category);
        }
        finally
        {
            IsCancelling = false;
            ShowCancelConfirmation = false;
            // Authoritative refresh regardless of outcome — never trust the client's own
            // optimistic view of the post-cancel state (docs §13).
            await LoadSubscriptionAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// Discards all loaded account state. Called by <see cref="MainViewModel"/> on
    /// sign-out and must be called before any different account's data is ever
    /// fetched — no state from a prior account may survive into a new sign-in
    /// (docs §16/§17).
    /// </summary>
    public void Reset()
    {
        _loadCts?.Cancel();
        _loadCts = null;

        _subscription = null;
        _entitlements = null;
        _usage = null;

        HasSubscription = false;
        SubscriptionLoadError = null;
        EntitlementsLoadError = null;
        UsageLoadError = null;
        CancelError = null;
        ShowCancelConfirmation = false;
        IsCancelling = false;
        IsLoadingSubscription = false;
        IsLoadingEntitlements = false;
        IsLoadingUsage = false;

        RaiseSubscriptionDisplays();
        Raise(nameof(HasUsageLimit));
        Raise(nameof(UsageDisplay)); RaiseTrial();
    }

    private void RaiseSubscriptionDisplays()
    {
        Raise(nameof(SubscriptionStatusDisplay));
        Raise(nameof(PlanDisplay));
        Raise(nameof(CurrentPeriodDisplay));
        Raise(nameof(CancelAtPeriodEnd));
        Raise(nameof(CanCancel));
    }

    /// <summary>Customer-friendly wording only — never implies payment-provider behavior the backend does not guarantee, and never invents a state outside the actual <c>SubscriptionStatus</c> enum (docs §10).</summary>
    private static string FriendlyStatus(string status) => status switch
    {
        "Trial" => "Trial",
        "Active" => "Active",
        "PastDue" => "Payment past due",
        "GracePeriod" => "Grace period",
        "Expired" => "Expired",
        "Cancelled" => "Cancelled",
        "Refunded" => "Refunded",
        _ => status, // forward-compatible: an unrecognized future status is shown as-is, never hidden or guessed at
    };

    private static string FormatDuration(double seconds)
    {
        if (seconds < 60) return $"{seconds:F0}s";
        var span = TimeSpan.FromSeconds(seconds);
        return span.Hours > 0 ? $"{span.Hours}h {span.Minutes}m" : $"{span.Minutes}m {span.Seconds}s";
    }

    /// <summary>Never claims certainty the backend error doesn't provide (docs §14) — a generic category maps to a generic message, not a guessed specific cause.</summary>
    private static string MapErrorToMessage(ApiErrorCategory category) => category switch
    {
        ApiErrorCategory.AuthenticationRequired => "Please sign in again.",
        ApiErrorCategory.AccountNotUsable => "Your account isn't available right now.",
        ApiErrorCategory.NetworkUnavailable or ApiErrorCategory.ServiceUnavailable => "Cannot reach the AUTRAXIS service right now.",
        _ => "Could not load this information right now.",
    };
}
