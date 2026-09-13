using VTTranslate.Core.Config;

namespace VTTranslate.Core.Tests;

public class AppSettingsTests
{
    [Fact]
    public void IsProviderConfigured_False_WhenKeyOrRegionMissing()
    {
        using var _1 = EnvVarTestHelper.SetTemporarily("AZURE_SPEECH_KEY", "");
        using var _2 = EnvVarTestHelper.SetTemporarily("AZURE_SPEECH_REGION", "");
        Assert.False(new AppSettings().IsProviderConfigured);

        using var _3 = EnvVarTestHelper.SetTemporarily("AZURE_SPEECH_KEY", "k");
        Assert.False(new AppSettings().IsProviderConfigured); // region still empty
    }

    [Fact]
    public void IsProviderConfigured_True_WhenBothPresent()
    {
        using var _1 = EnvVarTestHelper.SetTemporarily("AZURE_SPEECH_KEY", "k");
        using var _2 = EnvVarTestHelper.SetTemporarily("AZURE_SPEECH_REGION", "eastus");

        Assert.True(new AppSettings().IsProviderConfigured);
    }

    [Fact]
    public void AzureSpeechKey_IsNeverPersistedToDisk()
    {
        // The key must come only from the environment, never round-trip through
        // settings.json — that's the whole point of not storing it there.
        using var _1 = EnvVarTestHelper.SetTemporarily("AZURE_SPEECH_KEY", "should-not-be-serialized");

        var settings = new AppSettings();
        var json = System.Text.Json.JsonSerializer.Serialize(settings);

        Assert.DoesNotContain("should-not-be-serialized", json);
    }

    [Fact]
    public void SaveAndLoad_RoundTripsNonSecretValues_UsingRealSettingsPath()
    {
        // AppSettings persists non-secret fields to %AppData%\VTTranslate\settings.json;
        // round-trip against that real path but restore whatever was there before.
        var original = AppSettings.Load();
        try
        {
            var settings = new AppSettings { MicrophoneDeviceId = "mic-abc" };
            settings.Save();

            var reloaded = AppSettings.Load();

            Assert.Equal("mic-abc", reloaded.MicrophoneDeviceId);
        }
        finally
        {
            original.Save();
        }
    }
}
