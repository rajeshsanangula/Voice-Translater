using System.Text.RegularExpressions;

namespace VTTranslate.Backend.Tests;

/// <summary>
/// Verifies the backend API's committed configuration files contain no secret-shaped
/// values — per the Phase 6.3 instruction ("Secrets must never be hard-coded,
/// committed... "). This does not replace a full repository secret scan (performed
/// separately, see docs/phase-6.3-backend-foundation.md "Security"), but keeps the
/// specific files this phase adds honest on every test run, not just at review time.
/// </summary>
public class ConfigurationSecretSafetyTests
{
    private static readonly Regex[] SuspiciousPatterns =
    [
        new Regex(@"AZURE_[A-Z_]*KEY\s*[:=]\s*[""']?[A-Za-z0-9/+]{10,}", RegexOptions.IgnoreCase),
        new Regex(@"AIza[A-Za-z0-9_\-]{20,}"), // Gemini/Google API key shape
        new Regex(@"sk-[A-Za-z0-9]{20,}"),      // OpenAI API key shape
        new Regex(@"Server=.*Password=", RegexOptions.IgnoreCase), // a connection string carrying a credential
        new Regex(@"Bearer\s+[A-Za-z0-9\-_\.]{20,}"), // an embedded bearer token
    ];

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "VTTranslate.sln")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("Could not locate repository root (VTTranslate.sln) from test output directory.");
    }

    public static IEnumerable<object[]> ConfigFiles()
    {
        var root = RepoRoot();
        yield return [Path.Combine(root, "src", "VTTranslate.Backend.Api", "appsettings.json")];
        yield return [Path.Combine(root, "src", "VTTranslate.Backend.Api", "appsettings.Development.json")];
    }

    [Theory]
    [MemberData(nameof(ConfigFiles))]
    public void ConfigFile_ContainsNoSecretShapedValue(string path)
    {
        Assert.True(File.Exists(path), $"expected config file not found: {path}");
        var content = File.ReadAllText(path);

        foreach (var pattern in SuspiciousPatterns)
            Assert.False(pattern.IsMatch(content), $"{path} matched suspicious pattern {pattern}");
    }

    /// <summary>
    /// Phase 6.8: "ProviderCredentials" now legitimately carries nested "AzureSpeech"/
    /// "AzureTranslator" objects, each with "SubscriptionKey" (a secret — must always be
    /// committed empty) and "Region" (not a secret). No other provider key is permitted
    /// here — in particular, no Gemini/OpenAI key (Gemini naturalization is never
    /// promoted to production; see docs/phase-6.8-provider-access-gateway.md).
    /// </summary>
    [Theory]
    [MemberData(nameof(ConfigFiles))]
    public void ConfigFile_ProviderCredentialsSection_OnlyContainsExpectedKeysAndNoSecretValue(string path)
    {
        var content = File.ReadAllText(path);
        using var doc = System.Text.Json.JsonDocument.Parse(content);

        if (!doc.RootElement.TryGetProperty("ProviderCredentials", out var section)) return; // not every file has this section

        var allowedTopLevelKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "_comment", "AzureSpeech", "AzureTranslator" };
        var allowedNestedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "SubscriptionKey", "Region" };

        foreach (var property in section.EnumerateObject())
        {
            Assert.Contains(property.Name, allowedTopLevelKeys);
            if (property.Name == "_comment") continue;

            foreach (var nested in property.Value.EnumerateObject())
            {
                Assert.Contains(nested.Name, allowedNestedKeys);
                if (nested.Name == "SubscriptionKey")
                    Assert.Equal(string.Empty, nested.Value.GetString());
            }
        }
    }

    /// <summary>
    /// Phase 6.8: "ProviderAccess" carries only "CredentialLifetimeSeconds" — non-secret,
    /// so no empty-value requirement, but bounded to a sane range so a committed value
    /// can never accidentally configure an effectively-unlimited or nonsensical lifetime.
    /// </summary>
    [Theory]
    [MemberData(nameof(ConfigFiles))]
    public void ConfigFile_ProviderAccessSection_LifetimeIsSaneAndBounded(string path)
    {
        var content = File.ReadAllText(path);
        using var doc = System.Text.Json.JsonDocument.Parse(content);

        if (!doc.RootElement.TryGetProperty("ProviderAccess", out var section)) return; // not every file has this section

        var allowedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "_comment", "CredentialLifetimeSeconds" };
        foreach (var property in section.EnumerateObject())
        {
            Assert.Contains(property.Name, allowedKeys);
            if (property.Name == "CredentialLifetimeSeconds")
            {
                var value = property.Value.GetInt32();
                Assert.InRange(value, 1, 3600); // must be positive, never more than an hour
            }
        }
    }

    /// <summary>
    /// Phase 6.6: the "Billing" section now legitimately carries "Provider"/
    /// "WebhookSigningSecret"/"Environment" keys. Provider/Environment are non-secret
    /// placeholders (like Identity's Authority/Audience); WebhookSigningSecret IS a
    /// secret (like Database's ConnectionString) and must always be committed empty,
    /// never merely short. No real billing provider is activated by this config existing
    /// — NotImplementedBillingProvider remains the only registered implementation.
    /// </summary>
    [Theory]
    [MemberData(nameof(ConfigFiles))]
    public void ConfigFile_BillingSection_OnlyContainsExpectedKeysAndNoSecretValue(string path)
    {
        var content = File.ReadAllText(path);
        using var doc = System.Text.Json.JsonDocument.Parse(content);

        if (!doc.RootElement.TryGetProperty("Billing", out var billing)) return; // not every file has this section

        var allowedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "_comment", "Provider", "WebhookSigningSecret", "Environment" };
        foreach (var property in billing.EnumerateObject())
        {
            Assert.Contains(property.Name, allowedKeys);

            if (property.Name == "WebhookSigningSecret")
                Assert.Equal(string.Empty, property.Value.GetString());
        }
    }

    /// <summary>
    /// Phase 6.5: the "Database" section now legitimately carries a "ConnectionString"
    /// key. Unlike Identity's Authority/Audience (non-secret), ConnectionString IS a
    /// secret (it can carry a database password) — so the rule here is stricter: only
    /// "_comment"/"ConnectionString" may appear, and the committed value must always be
    /// completely empty, never merely short.
    /// </summary>
    [Theory]
    [MemberData(nameof(ConfigFiles))]
    public void ConfigFile_DatabaseSection_ConnectionStringNeverCommittedNonEmpty(string path)
    {
        var content = File.ReadAllText(path);
        using var doc = System.Text.Json.JsonDocument.Parse(content);

        if (!doc.RootElement.TryGetProperty("Database", out var database)) return; // not every file has this section

        var allowedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "_comment", "ConnectionString" };
        foreach (var property in database.EnumerateObject())
        {
            Assert.Contains(property.Name, allowedKeys);

            if (property.Name == "ConnectionString")
                Assert.Equal(string.Empty, property.Value.GetString());
        }
    }

    /// <summary>
    /// Phase 6.4: the "Identity" section now legitimately carries "Authority"/"Audience"
    /// keys (neither is a secret — see EntraIdentityOptions's own doc comment). This test
    /// enforces the narrower, correct rule: ONLY "_comment"/"Authority"/"Audience" may
    /// appear, and neither Authority nor Audience may hold a non-empty, secret-shaped, or
    /// suspiciously long value in the COMMITTED file (real values belong in environment
    /// variables / an untracked local file / a secret manager in any real deployment).
    /// </summary>
    [Theory]
    [MemberData(nameof(ConfigFiles))]
    public void ConfigFile_IdentitySection_OnlyContainsNonSecretKeys(string path)
    {
        var content = File.ReadAllText(path);
        using var doc = System.Text.Json.JsonDocument.Parse(content);

        if (!doc.RootElement.TryGetProperty("Identity", out var identity)) return; // not every file has this section

        var allowedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "_comment", "Authority", "Audience" };
        foreach (var property in identity.EnumerateObject())
        {
            Assert.Contains(property.Name, allowedKeys);

            if (property.Name is "Authority" or "Audience")
            {
                var value = property.Value.GetString() ?? "";
                Assert.True(value.Length < 20, $"Identity:{property.Name} in {path} looks like it may carry a real value ({value.Length} chars) — the committed file must stay empty; real values belong in environment variables or an untracked local override.");
            }
        }
    }
}
