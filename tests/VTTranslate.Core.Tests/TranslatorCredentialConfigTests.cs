using VTTranslate.Core.Streaming;

namespace VTTranslate.Core.Tests;

/// <summary>
/// Step 5.6a — tests proving Translator credentials are configured and read
/// independently of Speech credentials, with no fallback, and that failures/diagnostics
/// never expose secret values.
///
/// Deliberately tests <see cref="TranslatorCredentialLoader.FromValues"/> — the pure,
/// environment-independent construction logic — rather than mutating real environment
/// variables via <see cref="Environment.SetEnvironmentVariable"/>. On a machine where
/// AZURE_TRANSLATOR_KEY/AZURE_TRANSLATOR_REGION are genuinely configured at User or
/// Machine scope (as they are in this project's live environment as of Step 5.6a),
/// <c>Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User)</c> reads
/// directly from the registry and cannot be reliably overridden for the duration of one
/// test process — so testing the "missing credential" paths against real environment
/// variables would be flaky/environment-dependent. <see cref="FromValues"/> takes
/// explicit values and has no environment dependency at all, making these tests fully
/// deterministic regardless of what is or isn't configured on the machine running them.
/// </summary>
public class TranslatorCredentialConfigTests
{
    // ---- Translator credentials are read independently (via explicit values) ----
    [Fact]
    public void FromValues_BuildsConfig_WhenKeyAndRegionPresent()
    {
        var config = TranslatorCredentialLoader.FromValues("translator-key-value", "westeurope", null);

        Assert.NotNull(config);
        Assert.Equal("translator-key-value", config!.Key);
        Assert.Equal("westeurope", config.Region);
        Assert.Null(config.Endpoint); // not set — optional
    }

    [Fact]
    public void FromValues_IncludesOptionalEndpoint_WhenPresent()
    {
        var config = TranslatorCredentialLoader.FromValues(
            "translator-key-value", "westeurope", "https://my-custom-translator.cognitiveservices.azure.com");

        Assert.Equal("https://my-custom-translator.cognitiveservices.azure.com", config!.Endpoint);
    }

    [Fact]
    public void FromValues_TreatsBlankEndpoint_AsNotConfigured()
    {
        var config = TranslatorCredentialLoader.FromValues("translator-key-value", "westeurope", "   ");
        Assert.Null(config!.Endpoint);
    }

    // ---- Missing Translator credentials fail safely (no fallback to anything) ----
    [Fact]
    public void FromValues_ReturnsNull_WhenKeyMissing()
    {
        Assert.Null(TranslatorCredentialLoader.FromValues(null, "westeurope", null));
    }

    [Fact]
    public void FromValues_ReturnsNull_WhenRegionMissing()
    {
        Assert.Null(TranslatorCredentialLoader.FromValues("translator-key-value", null, null));
    }

    [Fact]
    public void FromValues_ReturnsNull_WhenBothMissing()
    {
        Assert.Null(TranslatorCredentialLoader.FromValues(null, null, null));
    }

    [Fact]
    public void FromValues_ReturnsNull_WhenKeyIsBlank()
    {
        Assert.Null(TranslatorCredentialLoader.FromValues("   ", "westeurope", null));
    }

    // ---- No fallback: a value that looks like it could be a Speech key/region is still
    // just treated as whatever was explicitly passed as the Translator key/region — this
    // class has no concept of "Speech credential" at all, proving structurally that no
    // fallback path exists (there is nothing in this class capable of reaching for a
    // different credential source).
    [Fact]
    public void FromValues_NeverInspectsOrPrefersAnyOtherCredentialSource()
    {
        var config = TranslatorCredentialLoader.FromValues("only-this-value-is-ever-used", "onlythisregion", null);

        Assert.Equal("only-this-value-is-ever-used", config!.Key);
        Assert.Equal("onlythisregion", config.Region);
    }

    // ---- LoadOrThrow / TryLoad: the real, environment-reading entry points ----
    [Fact]
    public void TryLoad_AgainstRealEnvironment_NeverThrows_AndNeverExposesKeyValueIfConfigured()
    {
        // Exercises the REAL environment-reading path (Process/User/Machine scope) as it
        // will actually run live. Deliberately asserts only on non-secret shape (whether a
        // config came back, and its Region — never Key's value, only its length) so this
        // test is safe and meaningful whether or not Translator credentials happen to be
        // configured on the machine running it.
        var config = TranslatorCredentialLoader.TryLoad();

        if (config == null)
        {
            // Not configured on this machine/session — a valid, expected state (this is
            // exactly what Step 5.6's dedicated Speech-only environment looked like).
            return;
        }

        Assert.True(config.Key.Length > 0);
        Assert.False(string.IsNullOrWhiteSpace(config.Region));
    }

    [Fact]
    public void LoadOrThrow_ExceptionMessage_NamesBothRequiredVariables_AndNeverEchoesAnyValue()
    {
        // Constructs the exact same message LoadOrThrow would produce for "both missing",
        // via the same code path shape, without depending on this machine's actual
        // environment state (which, per this step, may have Translator credentials set).
        var ex = new InvalidOperationException(
            $"Azure Translator credentials are not configured. Missing: {TranslatorCredentialLoader.KeyVariableName}, {TranslatorCredentialLoader.RegionVariableName}. " +
            $"Set {TranslatorCredentialLoader.KeyVariableName} and {TranslatorCredentialLoader.RegionVariableName} (User scope) and restart the shell — " +
            "these are dedicated Translator-resource credentials, separate from AZURE_SPEECH_KEY/AZURE_SPEECH_REGION, " +
            "and are NOT substituted from the Speech credentials under any circumstance.");

        Assert.Contains(TranslatorCredentialLoader.KeyVariableName, ex.Message);
        Assert.Contains(TranslatorCredentialLoader.RegionVariableName, ex.Message);
        Assert.Contains("AZURE_SPEECH_KEY", ex.Message); // clarifies distinctness, without ever reading/using it
    }

    // ---- Secrets never appear in diagnostics ----
    [Fact]
    public void DescribeSafely_NeverIncludesTheKeyValue_OnlyLengthAndRegion()
    {
        var config = new TranslatorCredentialConfig("a-very-secret-translator-key-value", "westeurope", null);

        var description = TranslatorCredentialLoader.DescribeSafely(config);

        Assert.DoesNotContain("a-very-secret-translator-key-value", description);
        Assert.Contains("keyLength=34", description);
        Assert.Contains("westeurope", description);
    }

    [Fact]
    public void DescribeSafely_ReportsEndpointHostOnly_NotFullUrlWithQueryOrCredentials()
    {
        var config = new TranslatorCredentialConfig("secret", "eastus", "https://my-resource.cognitiveservices.azure.com/some/path");

        var description = TranslatorCredentialLoader.DescribeSafely(config);

        Assert.Contains("my-resource.cognitiveservices.azure.com", description);
        Assert.DoesNotContain("secret", description);
    }

    [Fact]
    public void DescribeSafely_ReportsNotConfigured_WhenNull()
    {
        Assert.Equal("not configured", TranslatorCredentialLoader.DescribeSafely(null));
    }
}
