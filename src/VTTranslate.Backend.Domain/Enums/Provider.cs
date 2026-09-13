namespace VTTranslate.Backend.Domain.Enums;

/// <summary>
/// Phase 6.8 — the closed set of speech/translation providers this backend can broker
/// access to. Deliberately does NOT include Gemini — Gemini naturalization remains an
/// isolated R&amp;D track (Step 5.14 series) never promoted to production, and Gemini has
/// no safe short-lived delegated-credential mechanism to broker in the first place (see
/// docs/phase-6.8-provider-access-gateway.md).
/// </summary>
public enum Provider
{
    AzureSpeech,
    AzureTranslator,
}

/// <summary>
/// The specific operation a brokered provider credential is scoped to. A credential
/// issued for one capability is not implied to be valid for another, even against the
/// same <see cref="Provider"/>.
/// </summary>
public enum ProviderCapability
{
    SpeechRecognition,
    SpeechSynthesis,
    TextTranslation,
}
