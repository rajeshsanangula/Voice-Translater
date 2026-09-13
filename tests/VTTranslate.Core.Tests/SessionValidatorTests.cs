using VTTranslate.Core.Config;
using VTTranslate.Core.Session;

namespace VTTranslate.Core.Tests;

public class SessionValidatorTests
{
    private static readonly string[] InputIds = { "mic-1", "mic-2" };
    private static readonly string[] OutputIds = { "loopback-1", "speaker-1", "virtual-mic-1", "headphones-1" };

    private static IDisposable ConfigureValidProvider() =>
        new CompositeDisposable(
            EnvVarTestHelper.SetTemporarily("AZURE_SPEECH_KEY", "key"),
            EnvVarTestHelper.SetTemporarily("AZURE_SPEECH_REGION", "eastus"));

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
        using var _ = ConfigureValidProvider();
        Assert.Empty(Validate(ValidSettings()));
    }

    [Fact]
    public void Validate_ReturnsError_WhenProviderNotConfigured()
    {
        using var _1 = EnvVarTestHelper.SetTemporarily("AZURE_SPEECH_KEY", "");
        using var _2 = EnvVarTestHelper.SetTemporarily("AZURE_SPEECH_REGION", "");

        var issues = Validate(ValidSettings());

        Assert.Contains(issues, i => i.Severity == ValidationSeverity.Error && i.Message.Contains("Azure Speech"));
    }

    [Theory]
    [InlineData(nameof(AppSettings.MicrophoneDeviceId))]
    [InlineData(nameof(AppSettings.RemoteAudioInputDeviceId))]
    [InlineData(nameof(AppSettings.EnglishOutputDeviceId))]
    [InlineData(nameof(AppSettings.GermanOutputDeviceId))]
    public void Validate_ReturnsError_WhenAnyDeviceMissing(string propertyName)
    {
        using var _ = ConfigureValidProvider();
        var s = ValidSettings();
        typeof(AppSettings).GetProperty(propertyName)!.SetValue(s, null);

        var issues = Validate(s);

        Assert.True(SessionValidator.HasErrors(issues));
        Assert.Contains(issues, i => i.Severity == ValidationSeverity.Error && i.Message.Contains("No "));
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
        using var _ = ConfigureValidProvider();
        var s = ValidSettings();
        typeof(AppSettings).GetProperty(propertyName)!.SetValue(s, staleId);

        var issues = Validate(s);

        Assert.True(SessionValidator.HasErrors(issues));
        Assert.Contains(issues, i => i.Severity == ValidationSeverity.Error && i.Message.Contains("no longer available"));
    }

    [Fact]
    public void Validate_ReturnsFeedbackLoopError_WhenGermanOutputEqualsLoopbackSource()
    {
        using var _ = ConfigureValidProvider();
        var s = ValidSettings();
        s.GermanOutputDeviceId = s.RemoteAudioInputDeviceId;

        var issues = Validate(s);

        Assert.True(SessionValidator.HasErrors(issues));
        Assert.Contains(issues, i => i.Severity == ValidationSeverity.Error && i.Message.Contains("German output"));
    }

    [Fact]
    public void Validate_ReturnsFeedbackLoopError_WhenEnglishOutputEqualsLoopbackSource()
    {
        // The gap found live during pre-flight validation: our own English TTS could be
        // picked up by the DE->EN loopback capture if they share a device.
        using var _ = ConfigureValidProvider();
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
        using var _ = ConfigureValidProvider();
        var s = ValidSettings();
        s.EnglishOutputDeviceId = s.GermanOutputDeviceId;

        var issues = Validate(s);

        Assert.False(SessionValidator.HasErrors(issues));
        Assert.Contains(issues, i => i.Severity == ValidationSeverity.Warning);
    }

    [Fact]
    public void Validate_DeviceComparison_IsCaseInsensitive()
    {
        using var _ = ConfigureValidProvider();
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

    private sealed class CompositeDisposable : IDisposable
    {
        private readonly IDisposable[] _items;
        public CompositeDisposable(params IDisposable[] items) => _items = items;
        public void Dispose() { foreach (var i in _items) i.Dispose(); }
    }
}
