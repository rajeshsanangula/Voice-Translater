namespace VTTranslate.Core.Streaming;

/// <summary>
/// EXPERIMENTAL, SHADOW-ONLY — Step 5.6a. Dedicated configuration for the standalone
/// Azure Translator Text API experiment (<see cref="AzureTranslatorTextProvider"/>).
/// Deliberately separate from the production Speech configuration
/// (<c>VTTranslate.Core.Config.AppSettings.AzureSpeechKey</c>/<c>AzureSpeechRegion</c>,
/// used exclusively by <c>AzureSpeechTranslationProvider</c>) — the two are different
/// Azure resource types with different credentials, and this experiment must never
/// silently fall back from one to the other (see
/// docs/design-notes/incremental-translation-provider-feasibility.md §16 for why: Step
/// 5.6's 401s were caused by exactly this kind of resource/credential mismatch).
/// </summary>
public sealed record TranslatorCredentialConfig(string Key, string Region, string? Endpoint);

public static class TranslatorCredentialLoader
{
    public const string KeyVariableName = "AZURE_TRANSLATOR_KEY";
    public const string RegionVariableName = "AZURE_TRANSLATOR_REGION";
    public const string EndpointVariableName = "AZURE_TRANSLATOR_ENDPOINT";

    /// <summary>
    /// Reads <see cref="KeyVariableName"/>/<see cref="RegionVariableName"/>/<see cref="EndpointVariableName"/>
    /// (process, then user, then machine scope — matching this project's existing
    /// AZURE_SPEECH_* lookup convention). Returns null if the required key or region is
    /// missing/blank — callers must not substitute Speech credentials in that case (see
    /// <see cref="LoadOrThrow"/> for the fail-clearly path). Endpoint is optional: Azure
    /// Translator's public global endpoint is used unless a specific endpoint (e.g. a
    /// regional or custom one) is configured.
    /// </summary>
    public static TranslatorCredentialConfig? TryLoad() =>
        FromValues(GetEnv(KeyVariableName), GetEnv(RegionVariableName), GetEnv(EndpointVariableName));

    /// <summary>
    /// Pure, environment-independent construction from explicit values — used by
    /// <see cref="TryLoad"/> and directly by unit tests, so tests never need to read or
    /// mutate real environment variables (which, on a machine where Translator
    /// credentials are genuinely configured at User/Machine scope, cannot be reliably
    /// "unset" for the duration of a single test process). Returns null if the required
    /// key or region is missing/blank.
    /// </summary>
    public static TranslatorCredentialConfig? FromValues(string? key, string? region, string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(region))
            return null;

        return new TranslatorCredentialConfig(key, region, string.IsNullOrWhiteSpace(endpoint) ? null : endpoint);
    }

    /// <summary>
    /// Same as <see cref="TryLoad"/>, but throws a clear, actionable
    /// <see cref="InvalidOperationException"/> (naming the exact missing variable(s), never
    /// any secret value) instead of returning null. Use this at the entry point of any
    /// code path that requires the standalone Translator experiment to actually run — it
    /// must never silently substitute <c>AZURE_SPEECH_KEY</c>/<c>AZURE_SPEECH_REGION</c>.
    /// </summary>
    public static TranslatorCredentialConfig LoadOrThrow()
    {
        var config = TryLoad();
        if (config != null) return config;

        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(GetEnv(KeyVariableName))) missing.Add(KeyVariableName);
        if (string.IsNullOrWhiteSpace(GetEnv(RegionVariableName))) missing.Add(RegionVariableName);

        throw new InvalidOperationException(
            $"Azure Translator credentials are not configured. Missing: {string.Join(", ", missing)}. " +
            $"Set {KeyVariableName} and {RegionVariableName} (User scope) and restart the shell — " +
            "these are dedicated Translator-resource credentials, separate from AZURE_SPEECH_KEY/AZURE_SPEECH_REGION, " +
            "and are NOT substituted from the Speech credentials under any circumstance.");
    }

    /// <summary>Safe-to-print summary for diagnostics — never the key value, only its length and other non-secret metadata.</summary>
    public static string DescribeSafely(TranslatorCredentialConfig? config) =>
        config == null
            ? "not configured"
            : $"configured (keyLength={config.Key.Length}, region={config.Region}, " +
              $"endpoint={(config.Endpoint != null ? new Uri(config.Endpoint).Host : "default")})";

    private static string? GetEnv(string name) =>
        Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Process)
        ?? Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User)
        ?? Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Machine);
}
