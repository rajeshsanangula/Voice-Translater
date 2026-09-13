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

        if (!settings.IsProviderConfigured)
            issues.Add(new ValidationIssue(ValidationSeverity.Error,
                "Azure Speech key/region not configured. Set AZURE_SPEECH_KEY and AZURE_SPEECH_REGION environment variables, then restart."));

        CheckDevice(issues, settings.MicrophoneDeviceId, "microphone", availableInputDeviceIds);
        CheckDevice(issues, settings.RemoteAudioInputDeviceId, "remote audio input (loopback source)", availableOutputDeviceIds);
        CheckDevice(issues, settings.EnglishOutputDeviceId, "English output", availableOutputDeviceIds);
        CheckDevice(issues, settings.GermanOutputDeviceId, "German output", availableOutputDeviceIds);

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
