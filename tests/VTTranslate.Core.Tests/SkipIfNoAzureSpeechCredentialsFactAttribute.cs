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
    public SkipIfNoAzureSpeechCredentialsFactAttribute()
    {
        var key = Environment.GetEnvironmentVariable("AZURE_SPEECH_KEY");
        var region = Environment.GetEnvironmentVariable("AZURE_SPEECH_REGION");
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(region))
            Skip = "AZURE_SPEECH_KEY/AZURE_SPEECH_REGION are not configured in this environment — real-Azure provider credential renewal validation requires them.";
    }
}
