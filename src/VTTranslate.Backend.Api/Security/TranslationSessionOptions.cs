namespace VTTranslate.Backend.Api.Security;

/// <summary>
/// Phase 6.9 — non-secret. <see cref="LeaseSeconds"/> is the server-side lease/expiry
/// window: an Active session with no heartbeat/end within this window since its last
/// observed activity is lazily reconciled to Expired the next time anything touches it
/// (see docs/phase-6.9-usage-metering-and-session-accounting.md §14). Defaults
/// conservatively (60s) if unset or non-positive — never "no expiry".
/// </summary>
public sealed class TranslationSessionOptions
{
    public const string SectionName = "TranslationSessions";

    public int LeaseSeconds { get; set; } = 60;
}
