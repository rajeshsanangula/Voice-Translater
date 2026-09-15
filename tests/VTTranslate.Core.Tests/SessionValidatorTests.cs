using VTTranslate.Core.Config;
using VTTranslate.Core.Session;

namespace VTTranslate.Core.Tests;

public class SessionValidatorTests
{
    private static readonly string[] InputIds = { "mic-1", "mic-2" };
    private static readonly string[] OutputIds = { "loopback-1", "speaker-1", "virtual-mic-1", "headphones-1" };

    private static AppSettings ValidSettings() => new()
    {
        MicrophoneDeviceId = "mic-1",
        RemoteAudioInputDeviceId = "loopback-1",
        EnglishOutputDeviceId = "headphones-1",
        GermanOutputDeviceId = "virtual-mic-1"
    };

    private static IReadOnlyList<ValidationIssue> Validate(AppSettings s) =>
        SessionValidator.Validate(s, InputIds, OutputIds);

    [Fact]
    public void Validate_ReturnsNoIssues_WhenFullyConfiguredAndNoConflicts()
    {
        Assert.Empty(Validate(ValidSettings()));
    }

    [Fact]
    public void Validate_DoesNotFail_WhenAzureSpeechKeyAndRegionAreAbsent()
    {
        // Phase 8C: the production authenticated path (MainViewModel.CreateAuthenticatedProviderAsync
        // -> RequestProviderAccessAsync -> AzureSpeechTranslationProvider.FromAuthorizationToken)
        // obtains its Azure Speech credential from the AUTRAXIS backend via /provider-access —
        // it never reads AppSettings.AzureSpeechKey/AzureSpeechRegion. Session-start validation
        // must not require those local environment variables merely to start an authenticated
        // session; requiring them was a stale, pre-Phase-7.1 check.
        using var _1 = EnvVarTestHelper.SetTemporarily("AZURE_SPEECH_KEY", "");
        using var _2 = EnvVarTestHelper.SetTemporarily("AZURE_SPEECH_REGION", "");

        var issues = Validate(ValidSettings());

        Assert.Empty(issues);
        Assert.DoesNotContain(issues, i => i.Message.Contains("Azure Speech"));
    }

    [Theory]
    [InlineData(nameof(AppSettings.MicrophoneDeviceId))]
    [InlineData(nameof(AppSettings.GermanOutputDeviceId))]
    public void Validate_ReturnsError_WhenAnAlwaysRequiredDeviceIsMissing(string propertyName)
    {
        // MVP simple mode: microphone and German output (the basic mic-to-speaker
        // translation direction) are always required — unlike the meeting-mode fields
        // below, there is no way to run any session at all without these two. Other
        // required validation (device presence/availability) must still fail correctly
        // even though the obsolete Azure Speech key/region check is gone.
        var s = ValidSettings();
        typeof(AppSettings).GetProperty(propertyName)!.SetValue(s, null);

        var issues = Validate(s);

        Assert.True(SessionValidator.HasErrors(issues));
        Assert.Contains(issues, i => i.Severity == ValidationSeverity.Error && i.Message.Contains("No "));
    }

    [Fact]
    public void Validate_ReturnsNoIssues_WhenRemoteModeFieldsAreUnset_BasicMicOnlySessionIsValid()
    {
        // The core MVP fix: a first-run user with only a microphone and German output
        // configured (Windows-default-populated, never touching VB-CABLE/meeting
        // concepts) must be able to start a session — Remote/System Audio and English
        // Output are never required for basic mode.
        var s = ValidSettings();
        s.RemoteAudioInputDeviceId = null;
        s.EnglishOutputDeviceId = null;

        var issues = Validate(s);

        Assert.Empty(issues);
    }

    [Fact]
    public void Validate_ReturnsError_WhenRemoteModeIsActiveButEnglishOutputIsMissing()
    {
        // Once the user opts INTO remote/meeting mode (sets a loopback source), the
        // English output it needs somewhere to play to becomes required again — this
        // is the one meeting-mode field that stays conditionally mandatory.
        var s = ValidSettings();
        s.EnglishOutputDeviceId = null; // RemoteAudioInputDeviceId stays set from ValidSettings()

        var issues = Validate(s);

        Assert.True(SessionValidator.HasErrors(issues));
        Assert.Contains(issues, i => i.Severity == ValidationSeverity.Error && i.Message.Contains("English output"));
    }

    [Fact]
    public void Validate_RemoteModeIsDefinedSolelyByRemoteAudioInput_UnsetMeansOffRegardlessOfEnglishOutput()
    {
        // Remote mode is defined entirely by RemoteAudioInputDeviceId — leaving it unset
        // means remote mode is off, and therefore requires nothing else (English Output
        // included), even if English Output happens to be set to something. This is the
        // "remote mode requires remote/system audio" requirement restated as: without
        // it, nothing about remote mode is required at all.
        var s = ValidSettings();
        s.RemoteAudioInputDeviceId = null; // English Output stays set from ValidSettings()

        var issues = Validate(s);

        Assert.Empty(issues);
    }

    [Fact]
    public void Validate_ReturnsError_WhenRemoteModeIsActiveButTheLoopbackDeviceIsUnavailable()
    {
        // Remote mode being "on" (RemoteAudioInputDeviceId set) still requires that
        // device to actually be present, same as before this change.
        var s = ValidSettings();
        s.RemoteAudioInputDeviceId = "loopback-does-not-exist";

        var issues = Validate(s);

        Assert.True(SessionValidator.HasErrors(issues));
        Assert.Contains(issues, i => i.Severity == ValidationSeverity.Error && i.Message.Contains("no longer available"));
    }

    [Theory]
    [InlineData(nameof(AppSettings.MicrophoneDeviceId), "mic-does-not-exist")]
    [InlineData(nameof(AppSettings.RemoteAudioInputDeviceId), "loopback-does-not-exist")]
    [InlineData(nameof(AppSettings.EnglishOutputDeviceId), "headphones-does-not-exist")]
    [InlineData(nameof(AppSettings.GermanOutputDeviceId), "virtual-mic-does-not-exist")]
    public void Validate_ReturnsError_WhenSelectedDeviceIsNoLongerAvailable(string propertyName, string staleId)
    {
        // Simulates a device that was selected but has since been unplugged/disconnected
        // — its ID is set, but it's absent from the currently-enumerated device lists.
        var s = ValidSettings();
        typeof(AppSettings).GetProperty(propertyName)!.SetValue(s, staleId);

        var issues = Validate(s);

        Assert.True(SessionValidator.HasErrors(issues));
        Assert.Contains(issues, i => i.Severity == ValidationSeverity.Error && i.Message.Contains("no longer available"));
    }

    [Fact]
    public void Validate_ReturnsFeedbackLoopError_WhenGermanOutputEqualsLoopbackSource()
    {
        var s = ValidSettings();
        s.GermanOutputDeviceId = s.RemoteAudioInputDeviceId;

        var issues = Validate(s);

        Assert.True(SessionValidator.HasErrors(issues));
        Assert.Contains(issues, i => i.Severity == ValidationSeverity.Error && i.Message.Contains("German output"));
    }

    [Fact]
    public void Validate_ReturnsFeedbackLoopError_WhenEnglishOutputEqualsLoopbackSource()
    {
        // The exact scenario physically observed during UAT: Remote/System Audio and
        // English Output both set to the same device — must remain a hard error.
        var s = ValidSettings();
        s.EnglishOutputDeviceId = s.RemoteAudioInputDeviceId;

        var issues = Validate(s);

        Assert.True(SessionValidator.HasErrors(issues));
        Assert.Contains(issues, i => i.Severity == ValidationSeverity.Error && i.Message.Contains("English output"));
    }

    [Fact]
    public void Validate_ReturnsWarning_WhenEnglishAndGermanOutputAreTheSameDevice()
    {
        // Not a feedback loop (nothing loopback-captures from either output), but almost
        // certainly a misconfiguration — should warn, not block.
        var s = ValidSettings();
        s.EnglishOutputDeviceId = s.GermanOutputDeviceId;

        var issues = Validate(s);

        Assert.False(SessionValidator.HasErrors(issues));
        Assert.Contains(issues, i => i.Severity == ValidationSeverity.Warning);
    }

    [Fact]
    public void Validate_DeviceComparison_IsCaseInsensitive()
    {
        var s = ValidSettings();
        s.RemoteAudioInputDeviceId = "LOOPBACK-1";
        s.GermanOutputDeviceId = "loopback-1";

        var issues = Validate(s);

        Assert.Contains(issues, i => i.Message.Contains("German output"));
    }

    [Fact]
    public void HasErrors_IsFalse_WhenOnlyWarningsPresent()
    {
        var issues = new[] { new ValidationIssue(ValidationSeverity.Warning, "just a warning") };
        Assert.False(SessionValidator.HasErrors(issues));
    }

    [Fact]
    public void HasErrors_IsTrue_WhenAnyErrorPresent()
    {
        var issues = new[]
        {
            new ValidationIssue(ValidationSeverity.Warning, "a warning"),
            new ValidationIssue(ValidationSeverity.Error, "an error")
        };
        Assert.True(SessionValidator.HasErrors(issues));
    }
}
