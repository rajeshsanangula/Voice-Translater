using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using VTTranslate.App.Api;
using VTTranslate.App.Authentication;
using VTTranslate.App.Devices;
using VTTranslate.Core.Audio;
using VTTranslate.Core.Config;
using VTTranslate.Core.Diagnostics;
using VTTranslate.Core.Providers;
using VTTranslate.Core.Session;

namespace VTTranslate.App;

/// <summary>
/// UI-presentation-only classification of the existing <see cref="MainViewModel.Status"/>/
/// <see cref="MainViewModel.IsMicrophoneMuted"/>/<see cref="MainViewModel.ReconnectStatus"/>
/// state into a single consistent value for status-pill styling. Derived entirely from
/// state the pipeline already exposes — no new connection state is invented, and nothing
/// about session start/stop/mute/reconnect BEHAVIOR changes; this only affects how the
/// existing state is displayed.
/// </summary>
public enum AppStatusKind { Idle, Starting, Running, Muted, Reconnecting, ConfigurationError, Error, Stopped }

public sealed class MainViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public AppSettings Settings { get; }

    public ObservableCollection<AudioDeviceInfo> InputDevices { get; } = new();
    public ObservableCollection<AudioDeviceInfo> OutputDevices { get; } = new();
    public ObservableCollection<string> TranscriptLines { get; } = new();

    private DirectionPipeline? _micToGerman;
    private DirectionPipeline? _remoteToEnglish;
    private CancellationTokenSource? _cts;
    private readonly IDiagnosticLogger _diagnosticLogger = new FileDiagnosticLogger();

    // ---- Phase 7.1: customer authentication / authenticated backend integration ----
    // See docs/phase-7.1-customer-authentication-client-and-entra-integration.md.
    // Null when AuthenticationOptions.FromEnvironment() is not configured — the
    // customer application then fails closed to a clear configuration message rather
    // than silently falling back to any unauthenticated/direct-provider-key path.
    private readonly AuthenticationOptions _authOptions;
    private IAuthenticationService? _authService;
    private ITokenProvider? _tokenProvider;
    private IAutraxisApiClient? _apiClient;
    private readonly HttpClient _httpClient = new();
    private IDeviceRegistrationCoordinator? _deviceCoordinator;
    private Guid? _activeSessionId;
    private CancellationTokenSource? _heartbeatCts;

    private string _status = "Idle";
    public string Status { get => _status; set { _status = value; Raise(); Raise(nameof(StatusKind)); } }

    private bool _isRunning;
    public bool IsRunning { get => _isRunning; set { _isRunning = value; Raise(); RaiseCommands(); Raise(nameof(StatusKind)); } }

    private string _latencyText = "-- ms";
    public string LatencyText { get => _latencyText; set { _latencyText = value; Raise(); } }

    // Structured latency fields — purely a presentation split of the SAME values already
    // computed by LatencyLoop() from Core's own LatencyBreakdown; no new instrumentation.
    private string _latencyTotalText = "—";
    public string LatencyTotalText { get => _latencyTotalText; set { _latencyTotalText = value; Raise(); } }
    private string _latencyCaptureText = "—";
    public string LatencyCaptureText { get => _latencyCaptureText; set { _latencyCaptureText = value; Raise(); } }
    private string _latencyAsrText = "—";
    public string LatencyAsrText { get => _latencyAsrText; set { _latencyAsrText = value; Raise(); } }
    private string _latencyTtsText = "—";
    public string LatencyTtsText { get => _latencyTtsText; set { _latencyTtsText = value; Raise(); } }
    private string _latencyQueueText = "—";
    public string LatencyQueueText { get => _latencyQueueText; set { _latencyQueueText = value; Raise(); } }

    private string? _lastError;
    public string? LastError { get => _lastError; set { _lastError = value; Raise(); Raise(nameof(StatusKind)); } }

    private string _reconnectStatus = "";
    public string ReconnectStatus { get => _reconnectStatus; set { _reconnectStatus = value; Raise(); Raise(nameof(StatusKind)); } }

    /// <summary>Presentation-only classification — see <see cref="AppStatusKind"/>. Never invents a state the pipeline doesn't already expose.</summary>
    public AppStatusKind StatusKind
    {
        get
        {
            if (Status == "Configuration error") return AppStatusKind.ConfigurationError;
            if (Status == "Error") return AppStatusKind.Error;
            if (Status == "Starting...") return AppStatusKind.Starting;
            if (!IsRunning) return Status == "Stopped" ? AppStatusKind.Stopped : AppStatusKind.Idle;
            if (IsMicrophoneMuted) return AppStatusKind.Muted;
            // ReconnectStatus also carries the normal "Connected" message (see
            // AzureSpeechTranslationProvider.StatusChanged) — only the reconnect-attempt
            // text itself should surface as the Reconnecting state.
            if (ReconnectStatus.Contains("Reconnecting", StringComparison.OrdinalIgnoreCase)) return AppStatusKind.Reconnecting;
            return AppStatusKind.Running;
        }
    }

    private string _configWarnings = "";
    public string ConfigWarnings { get => _configWarnings; set { _configWarnings = value; Raise(); } }

    // ---- Phase 7.1: authentication state surfaced to the UI ----
    // Never displays a token, key, claim, or internal backend authorization detail —
    // only a generic sign-in/account-status message (docs §16/§28: anti-enumeration,
    // and never logging/displaying token content).
    private AuthenticationState _authState = AuthenticationState.SignedOut;
    public AuthenticationState AuthState
    {
        get => _authState;
        private set { _authState = value; Raise(); Raise(nameof(IsSignedIn)); Raise(nameof(IsSignedOut)); Raise(nameof(AccountStatusMessage)); RaiseCommands(); }
    }
    public bool IsSignedIn => AuthState == AuthenticationState.SignedIn;
    public bool IsSignedOut => !IsSignedIn;

    private string _accountStatusMessage = "";
    /// <summary>Generic, safe message only — never a specific account-status value (Pending/Suspended/Disabled/Closed are deliberately indistinguishable at the API boundary, docs §16/§19).</summary>
    public string AccountStatusMessage
    {
        get => _accountStatusMessage.Length > 0 ? _accountStatusMessage : AuthState switch
        {
            AuthenticationState.SignedIn => "Signed in",
            AuthenticationState.Authenticating => "Signing in…",
            AuthenticationState.AuthenticationFailed => "Sign-in failed",
            _ => "Signed out",
        };
        set { _accountStatusMessage = value; Raise(); }
    }

    public AudioDeviceInfo? SelectedMicrophone
    {
        get => InputDevices.FirstOrDefault(d => d.Id == Settings.MicrophoneDeviceId);
        set { Settings.MicrophoneDeviceId = value?.Id; Raise(); }
    }

    public AudioDeviceInfo? SelectedRemoteInput
    {
        get => OutputDevices.FirstOrDefault(d => d.Id == Settings.RemoteAudioInputDeviceId);
        set { Settings.RemoteAudioInputDeviceId = value?.Id; Raise(); }
    }

    public AudioDeviceInfo? SelectedEnglishOutput
    {
        get => OutputDevices.FirstOrDefault(d => d.Id == Settings.EnglishOutputDeviceId);
        set { Settings.EnglishOutputDeviceId = value?.Id; Raise(); }
    }

    public AudioDeviceInfo? SelectedGermanOutput
    {
        get => OutputDevices.FirstOrDefault(d => d.Id == Settings.GermanOutputDeviceId);
        set { Settings.GermanOutputDeviceId = value?.Id; Raise(); }
    }

    // Layer 2 — explicit user-intent mute for the EN->DE (microphone) direction only.
    // Not exposed for DE->EN yet, per the approved design. Not speaker identification.
    private bool _isMicrophoneMuted;
    public bool IsMicrophoneMuted
    {
        get => _isMicrophoneMuted;
        set
        {
            _isMicrophoneMuted = value;
            _micToGerman?.SetMuted(value);
            Raise();
            Raise(nameof(StatusKind));
        }
    }

    public ICommand StartCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand SaveSettingsCommand { get; }
    public ICommand RefreshDevicesCommand { get; }
    public ICommand ToggleMuteCommand { get; }
    public ICommand SignInCommand { get; }
    public ICommand SignOutCommand { get; }

    public MainViewModel()
    {
        Settings = AppSettings.Load();
        _authOptions = AuthenticationOptions.FromEnvironment();

        StartCommand = new RelayCommand(async () => await StartAsync(), () => !IsRunning && IsSignedIn);
        StopCommand = new RelayCommand(async () => await StopAsync(), () => IsRunning);
        SaveSettingsCommand = new RelayCommand(() => Settings.Save(), () => true);
        RefreshDevicesCommand = new RelayCommand(RefreshDevices, () => true);
        ToggleMuteCommand = new RelayCommand(() => IsMicrophoneMuted = !IsMicrophoneMuted, () => IsRunning);
        SignInCommand = new RelayCommand(async () => await SignInAsync(), () => !IsSignedIn);
        SignOutCommand = new RelayCommand(async () => await SignOutAsync(), () => IsSignedIn);

        RefreshDevices();
    }

    /// <summary>
    /// Called once after the window loads (docs §21 startup lifecycle) — attempts a
    /// SILENT-ONLY sign-in against any cached account (never launches a browser
    /// unprompted, per the explicit startup rule) and, if that succeeds, verifies
    /// current backend account usability via a lightweight authenticated call
    /// (docs §16: token-renewal success alone is never treated as evidence of account
    /// usability). Never blocks the UI thread and never throws to its caller.
    /// </summary>
    public async Task InitializeAsync()
    {
        if (!_authOptions.IsConfigured)
        {
            AccountStatusMessage = "Sign-in is not configured for this build.";
            return;
        }

        try
        {
            var cacheStore = new DpapiTokenCacheStore();
            var msalProvider = await MsalTokenProvider.CreateAsync(_authOptions, cacheStore);
            _tokenProvider = msalProvider;
            _apiClient = new AutraxisApiClient(_httpClient, msalProvider, _authOptions);
            // Corrective patch (Phase 7.1 runtime-risk audit, Risk 2): persists and
            // reuses the server-issued Device.Id across process restarts instead of
            // registering a new one every launch — see DeviceRegistrationCoordinator's
            // own doc comment.
            _deviceCoordinator = new DeviceRegistrationCoordinator(_apiClient, msalProvider, new LocalFileDeviceIdentityStore(), "Windows", Environment.MachineName);

            var authService = new AuthenticationService(msalProvider);
            authService.StateChanged += (_, state) => Application.Current.Dispatcher.Invoke(() => AuthState = state);
            _authService = authService;

            await authService.TrySilentSignInAsync();
            AuthState = authService.State;

            if (AuthState == AuthenticationState.SignedIn)
                await VerifyAccountUsableAsync();
        }
        catch (Exception ex)
        {
            // Configuration/initialization failure must never crash startup or
            // silently enable an unauthenticated path — surfaced as a status message.
            AccountStatusMessage = $"Sign-in unavailable: {ex.Message}";
        }
    }

    private async Task SignInAsync()
    {
        if (_authService is null)
        {
            AccountStatusMessage = "Sign-in is not configured for this build.";
            return;
        }

        try
        {
            await _authService.SignInAsync();
            AccountStatusMessage = "";
            await VerifyAccountUsableAsync();
        }
        catch (AuthenticationRequiredException)
        {
            AccountStatusMessage = "Sign-in was cancelled or could not complete.";
        }
        catch (OperationCanceledException)
        {
            AccountStatusMessage = "Sign-in was cancelled.";
        }
    }

    private async Task SignOutAsync()
    {
        if (IsRunning) await StopAsync();
        if (_authService is not null) await _authService.SignOutAsync();
        // Corrective patch (Phase 7.1 runtime-risk audit, Risk 2): device identity is
        // deliberately NOT cleared here. Signing out is an authentication/session
        // concern only — it must never consume another pooled device slot on the next
        // sign-in. The persisted device id remains associated with this local
        // installation (keyed by MSAL's own per-identity HomeAccountId, see
        // MsalTokenProvider.GetAccountKeyAsync) and is reused automatically the next
        // time the SAME account signs in; signing in as a genuinely DIFFERENT account
        // naturally looks up under a different key and is never handed the wrong
        // account's device id (see DeviceRegistrationCoordinator/IDeviceIdentityStore).
        AccountStatusMessage = "";
    }

    /// <summary>
    /// Docs §16: a successful (silent or interactive) token acquisition proves only
    /// that Entra still recognizes the identity — it says nothing about whether the
    /// AUTRAXIS Account is currently Active. This makes exactly one real, fresh,
    /// authenticated backend call (GET /profile — already the lightest existing probe)
    /// and reacts to ITS response, never to token-acquisition success alone.
    /// </summary>
    private async Task VerifyAccountUsableAsync()
    {
        if (_apiClient is null) return;

        try
        {
            await _apiClient.GetProfileAsync();
            AccountStatusMessage = "";
        }
        catch (AutraxisApiException ex) when (ex.Category is ApiErrorCategory.AccountNotUsable)
        {
            // Deliberately generic (docs §16/§19) — Pending/Suspended/Disabled/Closed
            // and a denied provisioning attempt are all indistinguishable here, by
            // the backend's own anti-enumeration design; this UI must not guess.
            AccountStatusMessage = "Your account isn't available right now.";
        }
        catch (AutraxisApiException ex) when (ex.Category is ApiErrorCategory.NetworkUnavailable or ApiErrorCategory.ServiceUnavailable)
        {
            AccountStatusMessage = "Cannot reach the AUTRAXIS service right now.";
        }
    }

    private void RaiseCommands()
    {
        (StartCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (StopCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ToggleMuteCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (SignInCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (SignOutCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private void RefreshDevices()
    {
        InputDevices.Clear();
        foreach (var d in AudioDeviceCatalog.GetInputDevices()) InputDevices.Add(d);

        OutputDevices.Clear();
        foreach (var d in AudioDeviceCatalog.GetOutputDevices()) OutputDevices.Add(d);

        Raise(nameof(SelectedMicrophone));
        Raise(nameof(SelectedRemoteInput));
        Raise(nameof(SelectedEnglishOutput));
        Raise(nameof(SelectedGermanOutput));
    }

    /// <summary>
    /// Phase 7.1 — THE production customer-application provider-access path,
    /// replacing the retired direct <c>AZURE_SPEECH_KEY</c>/subscription-key
    /// construction entirely (docs §18/§23). Obtains a short-lived Azure STS
    /// credential from the AUTRAXIS backend (Phase 6.8, unchanged) and builds the
    /// provider via <see cref="AzureSpeechTranslationProvider.FromAuthorizationToken"/>
    /// — the long-lived master key never reaches this process. The returned
    /// credential is held only in the local variables of this call (and the resulting
    /// provider's own short-lived internal use) — never persisted, never logged.
    /// </summary>
    private async Task<AzureSpeechTranslationProvider> CreateAuthenticatedProviderAsync(Guid deviceId, string voiceName, CancellationToken ct)
    {
        var grant = await _apiClient!.RequestProviderAccessAsync(deviceId, "AzureSpeech", "SpeechRecognition", ct);
        return AzureSpeechTranslationProvider.FromAuthorizationToken(grant.AccessToken, grant.Region, voiceName, _diagnosticLogger);
    }

    private async Task StartAsync()
    {
        var issues = SessionValidator.Validate(
            Settings,
            InputDevices.Select(d => d.Id).ToList(),
            OutputDevices.Select(d => d.Id).ToList());

        var warnings = issues.Where(i => i.Severity == ValidationSeverity.Warning).ToList();
        ConfigWarnings = warnings.Count > 0
            ? string.Join(" | ", warnings.Select(w => w.Message))
            : "";

        if (SessionValidator.HasErrors(issues))
        {
            LastError = string.Join(" | ", issues.Where(i => i.Severity == ValidationSeverity.Error).Select(i => i.Message));
            Status = "Configuration error";
            return;
        }

        if (!IsSignedIn || _apiClient is null || _deviceCoordinator is null)
        {
            LastError = "Please sign in before starting a session.";
            Status = "Configuration error";
            return;
        }

        Settings.Save();
        _cts = new CancellationTokenSource();
        LastError = null;
        ReconnectStatus = "";
        _isMicrophoneMuted = false; // each new session starts Unmuted regardless of prior session's state
        Raise(nameof(IsMicrophoneMuted));
        Status = "Starting...";

        try
        {
            // Corrective patch (Phase 7.1 runtime-risk audit, Risk 2): reuses the
            // persisted server-issued Device.Id across process restarts (no POST
            // /devices call at all if one is already on record for this identity) and
            // applies the bounded, at-most-one-replacement recovery
            // (DeviceRegistrationCoordinator) to every device-scoped call below — a
            // revoked/rejected device is replaced exactly once, never retried in a
            // loop.
            var deviceId = await _deviceCoordinator.EnsureDeviceRegisteredAsync(_cts.Token);

            // One AUTRAXIS translation session represents this bidirectional
            // customer session (Phase 6.9, unchanged) — duration/usage are computed
            // entirely server-side; this client never submits a duration or amount.
            var (session, deviceIdAfterSessionStart) = await _deviceCoordinator.ExecuteWithDeviceRecoveryAsync(
                deviceId, id => _apiClient.StartTranslationSessionAsync(id, clientSessionId: null, direction: "en-US:de-DE", _cts.Token), _cts.Token);
            deviceId = deviceIdAfterSessionStart;
            _activeSessionId = session.SessionId;

            var micToGermanSession = new Core.Session.TranslationSession
            {
                Direction = SessionDirection.EnglishMicToGerman,
                SourceLanguage = "en-US",
                TargetLanguage = "de-DE"
            };
            var (micProvider, deviceIdAfterMic) = await _deviceCoordinator.ExecuteWithDeviceRecoveryAsync(
                deviceId, id => CreateAuthenticatedProviderAsync(id, "de-DE-KatjaNeural", _cts.Token), _cts.Token);
            deviceId = deviceIdAfterMic;
            _micToGerman = new DirectionPipeline(
                micToGermanSession,
                new AudioCaptureSource(Settings.MicrophoneDeviceId!, CaptureKind.Microphone),
                new AudioPlaybackSink(Settings.GermanOutputDeviceId!),
                micProvider);

            var remoteToEnglishSession = new Core.Session.TranslationSession
            {
                Direction = SessionDirection.GermanRemoteToEnglish,
                SourceLanguage = "de-DE",
                TargetLanguage = "en-US"
            };
            var (remoteProvider, _) = await _deviceCoordinator.ExecuteWithDeviceRecoveryAsync(
                deviceId, id => CreateAuthenticatedProviderAsync(id, "en-US-JennyNeural", _cts.Token), _cts.Token);
            _remoteToEnglish = new DirectionPipeline(
                remoteToEnglishSession,
                new AudioCaptureSource(Settings.RemoteAudioInputDeviceId!, CaptureKind.SystemLoopback),
                new AudioPlaybackSink(Settings.EnglishOutputDeviceId!),
                remoteProvider);

            HookTranscript(_micToGerman, "EN→DE");
            HookTranscript(_remoteToEnglish, "DE→EN");
            HookErrors(_micToGerman);
            HookErrors(_remoteToEnglish);
            HookStatus(_micToGerman);
            HookStatus(_remoteToEnglish);

            await _micToGerman.StartAsync(_cts.Token);
            await _remoteToEnglish.StartAsync(_cts.Token);

            IsRunning = true;
            Status = "Running";
            _ = LatencyLoop(_cts.Token);

            _heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            _ = HeartbeatLoop(_activeSessionId.Value, _heartbeatCts.Token);
        }
        catch (AutraxisApiException ex)
        {
            LastError = MapApiErrorToMessage(ex);
            Status = "Error";
            await StopAsync();
        }
        catch (Exception ex)
        {
            LastError = $"Failed to start: {ex.Message}";
            Status = "Error";
            await StopAsync();
        }
    }

    private static string MapApiErrorToMessage(AutraxisApiException ex) => ex.Category switch
    {
        ApiErrorCategory.AuthenticationRequired => "Please sign in again.",
        ApiErrorCategory.AccountNotUsable => "Your account isn't available right now.",
        ApiErrorCategory.DeviceNotAuthorized => "This device is not authorized. Check your device list.",
        ApiErrorCategory.EntitlementDenied => "Your plan does not currently allow this.",
        ApiErrorCategory.ProviderAccessDenied => "Translation service access was denied.",
        ApiErrorCategory.NetworkUnavailable or ApiErrorCategory.ServiceUnavailable => "Cannot reach the AUTRAXIS service right now.",
        _ => "Could not start the session.",
    };

    /// <summary>
    /// Docs §19/§12: heartbeats keep the server-side lease alive; a 401 is handled
    /// transparently by the shared bounded renew-and-retry policy inside
    /// <see cref="IAutraxisApiClient"/> (heartbeat is already idempotent server-side,
    /// Phase 6.9 unchanged, so a renew-and-retry can never double-count usage). This
    /// loop never surfaces a heartbeat failure as a hard error — a transient miss is
    /// safely absorbed by Phase 6.9's own lease window; only StopAsync/session end is
    /// authoritative for terminating client-visible state.
    /// </summary>
    private async Task HeartbeatLoop(Guid sessionId, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(20), ct);
                if (ct.IsCancellationRequested) break;
                try { await _apiClient!.HeartbeatTranslationSessionAsync(sessionId, ct); }
                catch (AutraxisApiException) { /* absorbed — see doc comment above */ }
            }
        }
        catch (OperationCanceledException) { }
    }

    private void HookTranscript(DirectionPipeline pipeline, string label)
    {
        pipeline.Transcript += (_, r) =>
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                var tag = r.IsFinal ? "" : " (partial)";
                TranscriptLines.Add($"[{label}]{tag} {r.SourceText}  →  {r.TranslatedText}");
                while (TranscriptLines.Count > 200) TranscriptLines.RemoveAt(0);
            });
        };
    }

    private void HookErrors(DirectionPipeline pipeline)
    {
        pipeline.Error += (_, e) =>
        {
            var wasFatal = e.IsFatal;
            Application.Current.Dispatcher.Invoke(() =>
            {
                // Previously unlabeled — you could not tell from this text alone which
                // direction's recognizer produced it. Now uses the same DirectionLabel
                // helper as HookStatus, so both are labeled consistently.
                LastError = DirectionLabel.Format(pipeline.Session.Direction, e.Message);
                if (wasFatal) Status = "Error";

                // Device hot-plug resilience: a fatal capture/playback error (e.g. a
                // Bluetooth/USB device disappearing) leaves this direction's audio
                // streams unusable. Rather than leave the session in an ambiguous
                // half-running state, tear the whole session down cleanly — this is
                // what guarantees no duplicate streams can ever be created later by a
                // stray Start on top of a not-fully-stopped session. The user sees the
                // error message above and must explicitly Start again (after
                // reconnecting the device and clicking Refresh Devices), rather than
                // the app silently guessing at auto-recovery. Called from inside this
                // Dispatcher.Invoke callback (not outside it) so StopAsync's internal
                // `await` continuations correctly capture the UI thread's
                // SynchronizationContext, since this event can otherwise arrive on an
                // Azure SDK worker thread.
                if (wasFatal && IsRunning) _ = StopAsync();
            });
        };
    }

    private void HookStatus(DirectionPipeline pipeline)
    {
        pipeline.StatusChanged += (_, s) =>
        {
            Application.Current.Dispatcher.Invoke(() => ReconnectStatus = DirectionLabel.Format(pipeline.Session.Direction, s));
        };
    }

    private async Task LatencyLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var a = _micToGerman?.Latency;
                var b = _remoteToEnglish?.Latency;
                var capture = Math.Max(a?.CaptureToFirstPartial.Snapshot().P90Ms ?? 0, b?.CaptureToFirstPartial.Snapshot().P90Ms ?? 0);
                var asr = Math.Max(a?.RecognitionAndTranslation.Snapshot().P90Ms ?? 0, b?.RecognitionAndTranslation.Snapshot().P90Ms ?? 0);
                var tts = Math.Max(a?.Synthesis.Snapshot().P90Ms ?? 0, b?.Synthesis.Snapshot().P90Ms ?? 0);
                var enqueue = Math.Max(a?.SynthesisToPlaybackEnqueue.Snapshot().P90Ms ?? 0, b?.SynthesisToPlaybackEnqueue.Snapshot().P90Ms ?? 0);
                var total = Math.Max(a?.EndToEnd.Snapshot().P90Ms ?? 0, b?.EndToEnd.Snapshot().P90Ms ?? 0);
                Application.Current.Dispatcher.Invoke(() =>
                {
                    LatencyText = total > 0
                        // "Pipeline total" is capture-to-playback-enqueue latency only — it does
                        // NOT include mic hardware buffering, network transit, Azure's own
                        // internal processing before it emits events to us, WASAPI output
                        // buffering, or acoustic delay to the ear. See LatencyBreakdown's doc
                        // comment. This is not the latency the user actually perceives.
                        ? $"Pipeline total {total:F0}ms P90 (internal only, not human-perceived)  |  capture→partial {capture:F0}ms  |  ASR+MT {asr:F0}ms  |  →TTS {tts:F0}ms  |  TTS→queue {enqueue:F0}ms"
                        : "-- ms";

                    // Same underlying numbers as above, split into individually-bindable
                    // fields for the structured diagnostics panel (presentation only).
                    LatencyTotalText = total > 0 ? $"{total:F0} ms" : "—";
                    LatencyCaptureText = total > 0 ? $"{capture:F0} ms" : "—";
                    LatencyAsrText = total > 0 ? $"{asr:F0} ms" : "—";
                    LatencyTtsText = total > 0 ? $"{tts:F0} ms" : "—";
                    LatencyQueueText = total > 0 ? $"{enqueue:F0} ms" : "—";
                });
            }
            catch (TaskCanceledException) { }

            await Task.Delay(1000, ct).ContinueWith(_ => { });
        }
    }

    private async Task StopAsync()
    {
        _cts?.Cancel();
        _heartbeatCts?.Cancel();
        if (_micToGerman != null) await _micToGerman.DisposeAsync();
        if (_remoteToEnglish != null) await _remoteToEnglish.DisposeAsync();
        _micToGerman = null;
        _remoteToEnglish = null;

        if (_activeSessionId is { } sessionId && _apiClient is not null)
        {
            try { await _apiClient.EndTranslationSessionAsync(sessionId, CancellationToken.None); }
            catch (AutraxisApiException) { /* best-effort — Phase 6.9's lease/expiry reconciliation closes this out even if this call fails, see docs §19 */ }
        }
        _activeSessionId = null;

        IsRunning = false;
        Status = "Stopped";
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _httpClient.Dispose();
    }
}

public sealed class RelayCommand : ICommand
{
    private readonly Func<Task>? _executeAsync;
    private readonly Action? _execute;
    private readonly Func<bool> _canExecute;

    public RelayCommand(Func<Task> executeAsync, Func<bool> canExecute)
    {
        _executeAsync = executeAsync;
        _canExecute = canExecute;
    }

    public RelayCommand(Action execute, Func<bool> canExecute)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => _canExecute();

    public async void Execute(object? parameter)
    {
        if (_executeAsync != null) await _executeAsync();
        else _execute?.Invoke();
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
