using System.Reflection;
using VTTranslate.Core.Providers;

namespace VTTranslate.App.Tests;

/// <summary>
/// Phase 7.1 §33/§5 mandatory regression coverage: proves the production customer
/// application path no longer depends on the direct Azure Speech master
/// subscription key. Mirrors the repository's existing text-scan test pattern
/// (backend's <c>ConfigurationSecretSafetyTests</c>) applied to the WPF client.
/// </summary>
public class ProviderRetirementTests
{
    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "VTTranslate.sln")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("Could not locate repository root (VTTranslate.sln) from test output directory.");
    }

    [Fact]
    public void AzureSpeechTranslationProvider_ExposesTokenBasedFactory_ForTheAuthenticatedCustomerPath()
    {
        var method = typeof(AzureSpeechTranslationProvider).GetMethod("FromAuthorizationToken", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(method);
        Assert.True(method!.IsStatic);
    }

    [Fact]
    public void FromAuthorizationToken_NeverRequiresASubscriptionKey()
    {
        // Structural proof: the factory's own parameter list has no subscriptionKey
        // parameter at all — a caller physically cannot supply the master key through
        // this path.
        var method = typeof(AzureSpeechTranslationProvider).GetMethod("FromAuthorizationToken", BindingFlags.Public | BindingFlags.Static)!;
        var parameterNames = method.GetParameters().Select(p => p.Name);
        Assert.DoesNotContain("subscriptionKey", parameterNames);
        Assert.Contains("authorizationToken", parameterNames);
    }

    [Fact]
    public void MainViewModel_ProductionStartPath_NoLongerReferencesTheDirectSubscriptionKey()
    {
        var path = Path.Combine(RepoRoot(), "src", "VTTranslate.App", "MainViewModel.cs");
        Assert.True(File.Exists(path), $"expected file not found: {path}");
        var content = File.ReadAllText(path);

        // The retired call shape — constructing the provider directly from the raw
        // master key — must not appear anywhere in this file any longer.
        Assert.DoesNotContain("Settings.AzureSpeechKey", content);
        Assert.DoesNotContain("new AzureSpeechTranslationProvider(", content);

        // The replacement path — the authenticated, short-lived-credential factory —
        // must be present and actually used to construct the providers this
        // ViewModel starts a session with.
        Assert.Contains("AzureSpeechTranslationProvider.FromAuthorizationToken", content);
        Assert.Contains("RequestProviderAccessAsync", content);
    }

    [Fact]
    public void AppSettings_AzureSpeechKey_IsDocumentedAsTestDevelopmentOnly()
    {
        var path = Path.Combine(RepoRoot(), "src", "VTTranslate.Core", "Config", "AppSettings.cs");
        Assert.True(File.Exists(path), $"expected file not found: {path}");
        var content = File.ReadAllText(path);

        Assert.Contains("TEST/DEVELOPMENT-ONLY", content);
    }
}
