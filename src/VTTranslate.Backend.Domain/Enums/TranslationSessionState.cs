namespace VTTranslate.Backend.Domain.Enums;

/// <summary>
/// Phase 6.9 — the smallest practical lifecycle for a server-authoritative translation
/// session (docs/phase-6.9-usage-metering-and-session-accounting.md §7). Ended/Expired/
/// Aborted are all terminal — none can transition to any other state, including back to
/// Active. There is no separate "Starting" state: a session is created directly as
/// Active, since admission is already fully decided (device+entitlement+usage checks)
/// before the row is ever written.
/// </summary>
public enum TranslationSessionState
{
    Active,
    Ended,
    Expired,
    Aborted,
}
