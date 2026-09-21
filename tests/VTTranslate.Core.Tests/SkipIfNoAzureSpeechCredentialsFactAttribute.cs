namespace VTTranslate.Core.Tests;

/// <summary>
/// Phase 7.2 — a <see cref="FactAttribute"/> that self-skips (never fails the whole
/// suite) when no real Azure Speech credentials are configured in this environment.
/// Mirrors <c>SkipIfNoDockerFactAttribute</c> (VTTranslate.Backend.Tests) exactly:
/// skipping, not silently passing and not failing red, is the honest representation
/// of "this test genuinely could not run here." Never invents/fabricates a result —
/// see docs/phase-7.2-long-running-translation-session-continuity.md, "REAL AZURE
/// VALIDATION."
/// </summary>
public sealed class SkipIfNoAzureSpeechCredentialsFactAttribute : FactAttribute
{
    /// <param name="requiredRepoRelativeFixture">
    /// Optional repo-relative path of a generated, git-ignored input fixture (e.g.
    /// <c>test-results/input-audio/*.wav</c>, produced by <c>VTTranslate.LiveTest gen-test-audio</c>).
    /// When set and the file is absent, the test self-skips with an explicit reason instead of failing
    /// on a clean checkout — the same "input file missing, run gen-test-audio first" handling
    /// <c>VTTranslate.LiveTest</c> already uses. Credentials are still required as before.
    /// </param>
    public SkipIfNoAzureSpeechCredentialsFactAttribute(string? requiredRepoRelativeFixture = null)
    {
        var key = Environment.GetEnvironmentVariable("AZURE_SPEECH_KEY");
        var region = Environment.GetEnvironmentVariable("AZURE_SPEECH_REGION");
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(region))
        {
            Skip = "AZURE_SPEECH_KEY/AZURE_SPEECH_REGION are not configured in this environment — real-Azure provider credential renewal validation requires them.";
            return;
        }

        if (requiredRepoRelativeFixture is not null)
        {
            var dir = AppContext.BaseDirectory;
            while (dir is not null && !File.Exists(Path.Combine(dir, "VTTranslate.sln")))
                dir = Path.GetDirectoryName(dir);
            var fixturePath = dir is null ? null : Path.Combine(dir, requiredRepoRelativeFixture);
            if (fixturePath is null || !File.Exists(fixturePath))
                Skip = $"Required generated input fixture '{requiredRepoRelativeFixture}' is missing (git-ignored, not part of a clean checkout). " +
                       "Generate it with: dotnet run --project tools/VTTranslate.LiveTest -- gen-test-audio (requires Azure Speech credentials).";
        }
    }
}
