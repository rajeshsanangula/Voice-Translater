namespace VTTranslate.Backend.Application.Entitlements;

/// <summary>
/// The server-side entitlement gate (Phase 6.1 §5, Phase 6.2B §11) — the single question
/// this whole boundary exists to answer: "can this account/device start a German↔English
/// real-time translation session right now?" This phase implements the DECISION LOGIC
/// only; it does not implement the actual provider-token issuance that would follow a
/// positive decision (Phase 6.6+ — the "real-time translation backend" and "provider API
/// gateway" work is explicitly out of scope for Phase 6.3).
/// </summary>
public interface IEntitlementService
{
    Task<EntitlementDecision> CanStartTranslationSessionAsync(Guid accountId, Guid deviceId, CancellationToken ct);
}

/// <summary><see cref="Reason"/> is always populated (even when <see cref="Allowed"/> is true, e.g. "ok") so a denial is never a silent/ambiguous false.</summary>
public sealed record EntitlementDecision(bool Allowed, string Reason, string? Code = null)
{
    public static EntitlementDecision Allow(string reason) => new(true, reason);
    public static EntitlementDecision Deny(string reason, string? code = null) => new(false, reason, code);
}
