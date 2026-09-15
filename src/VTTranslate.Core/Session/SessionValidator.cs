using VTTranslate.Core.Config;

namespace VTTranslate.Core.Session;

public enum ValidationSeverity { Error, Warning }

/// <summary>One configuration problem. Errors block Start; Warnings are shown but don't.</summary>
public sealed record ValidationIssue(ValidationSeverity Severity, string Message);

/// <summary>
/// Pure, dependency-free validation of a device/config selection before a session
/// starts. Takes the currently-enumerated device ID lists explicitly (rather than
/// re-enumerating itself) so it stays synchronous, testable without real hardware, and
/// unambiguous about what "available" meant at the moment Start was clicked.
/// </summary>
public static class SessionValidator
{
    public static IReadOnlyList<ValidationIssue> Validate(
        AppSettings settings,
        IReadOnlyCollection<string> availableInputDeviceIds,
        IReadOnlyCollection<string> availableOutputDeviceIds)
    {
        var issues = new List<ValidationIssue>();

        // Phase 8C: the production authenticated path (MainViewModel.CreateAuthenticatedProviderAsync
        // -> RequestProviderAccessAsync -> AzureSpeechTranslationProvider.FromAuthorizationToken)
        // obtains its Azure Speech credential from the AUTRAXIS backend via /provider-access
        // (Phase 6.8) — it never reads Settings.AzureSpeechKey/AzureSpeechRegion. Requiring
        // those local environment variables here was a stale, pre-Phase-7.1 check left over
        // from before the customer app was migrated off direct provider credentials; it no
        // longer reflects anything the session-start path actually consumes, so it must not
        // block Start. AppSettings.AzureSpeechKey/AzureSpeechRegion/IsProviderConfigured
        // themselves are left untouched — still used by non-production experimental/live-test
        // code (VTTranslate.Core.Tests, tools/VTTranslate.LiveTest, Core/Streaming) that
        // legitimately talks to Azure directly outside the authenticated customer path.

        // MVP simple mode: only the microphone and its German-translation output are
        // always required — a basic "speak into the mic, hear the translation" session
        // needs nothing else and must not be blocked by unset meeting-mode fields.
        CheckDevice(issues, settings.MicrophoneDeviceId, "microphone", availableInputDeviceIds);
        CheckDevice(issues, settings.GermanOutputDeviceId, "German output", availableOutputDeviceIds);

        // Remote/meeting mode (the DE->EN loopback direction) is OPTIONAL — activated
        // only when the user has explicitly configured a remote audio input (loopback
        // source), e.g. for VB-CABLE/meeting routing. When it's off, DirectionPipeline
        // for that direction is never constructed (MainViewModel.StartAsync), so an
        // unset English output is correctly irrelevant, not an error.
        var remoteModeRequested = !string.IsNullOrWhiteSpace(settings.RemoteAudioInputDeviceId);
        if (remoteModeRequested)
        {
            CheckDevice(issues, settings.RemoteAudioInputDeviceId, "remote audio input (loopback source)", availableOutputDeviceIds);
            CheckDevice(issues, settings.EnglishOutputDeviceId, "English output", availableOutputDeviceIds);
        }

        if (HasAllFourDevices(settings))
        {
            if (Same(settings.RemoteAudioInputDeviceId, settings.GermanOutputDeviceId))
                issues.Add(new ValidationIssue(ValidationSeverity.Error,
                    "Feedback loop risk: the remote audio input (loopback) is the same device as the German output. " +
                    "Route German output to a different device (e.g. a virtual microphone), not the device being loopback-captured."));

            if (Same(settings.RemoteAudioInputDeviceId, settings.EnglishOutputDeviceId))
                issues.Add(new ValidationIssue(ValidationSeverity.Error,
                    "Feedback loop risk: the remote audio input (loopback) is the same device as the English output. " +
                    "Your own English TTS would be captured by the DE→EN loopback and could be misrecognized as new speech."));

            if (Same(settings.EnglishOutputDeviceId, settings.GermanOutputDeviceId))
                issues.Add(new ValidationIssue(ValidationSeverity.Warning,
                    "English output and German output are set to the same device. Not unsafe, but likely not what you intend " +
                    "— you won't be able to tell the two languages apart by which device they play on."));

            if (Same(settings.MicrophoneDeviceId, settings.RemoteAudioInputDeviceId))
                issues.Add(new ValidationIssue(ValidationSeverity.Warning,
                    "Microphone and remote audio input reference the same device ID. This shouldn't normally happen since " +
                    "they're chosen from separate capture/render device lists — double-check your selection."));
        }

        return issues;
    }

    public static bool HasErrors(IReadOnlyList<ValidationIssue> issues) =>
        issues.Any(i => i.Severity == ValidationSeverity.Error);

    private static bool Same(string? a, string? b) =>
        a != null && b != null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static bool HasAllFourDevices(AppSettings s) =>
        !string.IsNullOrWhiteSpace(s.MicrophoneDeviceId) &&
        !string.IsNullOrWhiteSpace(s.RemoteAudioInputDeviceId) &&
        !string.IsNullOrWhiteSpace(s.EnglishOutputDeviceId) &&
        !string.IsNullOrWhiteSpace(s.GermanOutputDeviceId);

    private static void CheckDevice(List<ValidationIssue> issues, string? deviceId, string label, IReadOnlyCollection<string> available)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            issues.Add(new ValidationIssue(ValidationSeverity.Error, $"No {label} selected."));
            return;
        }

        if (!available.Contains(deviceId, StringComparer.OrdinalIgnoreCase))
        {
            issues.Add(new ValidationIssue(ValidationSeverity.Error,
                $"The configured {label} device is no longer available — it may have been unplugged, or its Bluetooth/USB " +
                "connection dropped. Reconnect it and click Refresh Devices, or select a different device."));
        }
    }
}
