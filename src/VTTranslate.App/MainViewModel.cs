using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
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

    public string AzureConfigStatus =>
        Settings.IsProviderConfigured
            ? $"Loaded from environment: region '{Settings.AzureSpeechRegion}', key ****{Tail(Settings.AzureSpeechKey)}"
            : "NOT CONFIGURED — set AZURE_SPEECH_KEY and AZURE_SPEECH_REGION environment variables, then restart the app.";

    private static string Tail(string? s) => string.IsNullOrEmpty(s) ? "" : s[^Math.Min(4, s.Length)..];

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

    public MainViewModel()
    {
        Settings = AppSettings.Load();

        StartCommand = new RelayCommand(async () => await StartAsync(), () => !IsRunning);
        StopCommand = new RelayCommand(async () => await StopAsync(), () => IsRunning);
        SaveSettingsCommand = new RelayCommand(() => Settings.Save(), () => true);
        RefreshDevicesCommand = new RelayCommand(RefreshDevices, () => true);
        ToggleMuteCommand = new RelayCommand(() => IsMicrophoneMuted = !IsMicrophoneMuted, () => IsRunning);

        RefreshDevices();
    }

    private void RaiseCommands()
    {
        (StartCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (StopCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ToggleMuteCommand as RelayCommand)?.RaiseCanExecuteChanged();
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

        Settings.Save();
        _cts = new CancellationTokenSource();
        LastError = null;
        ReconnectStatus = "";
        _isMicrophoneMuted = false; // each new session starts Unmuted regardless of prior session's state
        Raise(nameof(IsMicrophoneMuted));
        Status = "Starting...";

        try
        {
            var micToGermanSession = new Core.Session.TranslationSession
            {
                Direction = SessionDirection.EnglishMicToGerman,
                SourceLanguage = "en-US",
                TargetLanguage = "de-DE"
            };
            _micToGerman = new DirectionPipeline(
                micToGermanSession,
                new AudioCaptureSource(Settings.MicrophoneDeviceId!, CaptureKind.Microphone),
                new AudioPlaybackSink(Settings.GermanOutputDeviceId!),
                new AzureSpeechTranslationProvider(Settings.AzureSpeechKey!, Settings.AzureSpeechRegion!, "de-DE-KatjaNeural", _diagnosticLogger));

            var remoteToEnglishSession = new Core.Session.TranslationSession
            {
                Direction = SessionDirection.GermanRemoteToEnglish,
                SourceLanguage = "de-DE",
                TargetLanguage = "en-US"
            };
            _remoteToEnglish = new DirectionPipeline(
                remoteToEnglishSession,
                new AudioCaptureSource(Settings.RemoteAudioInputDeviceId!, CaptureKind.SystemLoopback),
                new AudioPlaybackSink(Settings.EnglishOutputDeviceId!),
                new AzureSpeechTranslationProvider(Settings.AzureSpeechKey!, Settings.AzureSpeechRegion!, "en-US-JennyNeural", _diagnosticLogger));

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
        }
        catch (Exception ex)
        {
            LastError = $"Failed to start: {ex.Message}";
            Status = "Error";
            await StopAsync();
        }
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
        if (_micToGerman != null) await _micToGerman.DisposeAsync();
        if (_remoteToEnglish != null) await _remoteToEnglish.DisposeAsync();
        _micToGerman = null;
        _remoteToEnglish = null;
        IsRunning = false;
        Status = "Stopped";
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
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
