using System.Text.Json;
using System.Text.Json.Serialization;

namespace VTTranslate.Core.Config;

/// <summary>
/// Non-secret settings persist to a local JSON file. The Azure Speech key is
/// deliberately NEVER written to that file — it is only ever read from the
/// AZURE_SPEECH_KEY / AZURE_SPEECH_REGION environment variables (checked at
/// process and user scope), so it never sits in plaintext in AppData.
/// </summary>
public sealed class AppSettings
{
    /// <summary>
    /// TEST/DEVELOPMENT-ONLY — the long-lived Azure Speech master subscription key.
    /// Phase 7.1 retires this from the production customer application path entirely:
    /// the authenticated customer application obtains a short-lived, backend-issued
    /// Azure STS token via <c>POST /provider-access</c> (Phase 6.8) and constructs
    /// <c>AzureSpeechTranslationProvider.FromAuthorizationToken(...)</c> instead — see
    /// <c>VTTranslate.App.MainViewModel</c> and
    /// docs/phase-7.1-customer-authentication-client-and-entra-integration.md §18/§23.
    /// This property remains only for pre-commercial-MVP developer/manual-test
    /// harnesses that never run through the authenticated customer application code
    /// path; it must never be read by that path.
    /// </summary>
    [JsonIgnore]
    public string? AzureSpeechKey => GetEnv("AZURE_SPEECH_KEY");

    [JsonIgnore]
    public string? AzureSpeechRegion => GetEnv("AZURE_SPEECH_REGION");

    public string? MicrophoneDeviceId { get; set; }
    public string? RemoteAudioInputDeviceId { get; set; } // loopback source for the German side
    public string? EnglishOutputDeviceId { get; set; }     // where translated English is played
    public string? GermanOutputDeviceId { get; set; }      // where translated German is played (e.g. a virtual mic)

    [JsonIgnore]
    public bool IsProviderConfigured =>
        !string.IsNullOrWhiteSpace(AzureSpeechKey) && !string.IsNullOrWhiteSpace(AzureSpeechRegion);

    private static string? GetEnv(string name) =>
        Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Process)
        ?? Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User)
        ?? Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Machine);

    private static string SettingsPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VTTranslate", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return new AppSettings();

            var json = File.ReadAllText(SettingsPath);
            return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch
        {
            // Corrupt or unreadable settings must never crash startup.
            return new AppSettings();
        }
    }

    public void Save()
    {
        var dir = Path.GetDirectoryName(SettingsPath)!;
        Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(SettingsPath, json);
    }
}
