namespace VTTranslate.Backend.Domain.Enums;

/// <summary>Device authorization state — see docs/phase-6.2b-resolved-architecture-decisions.md §6 (account-based, server-authorized devices; explicitly NOT hardware-fingerprint-primary).</summary>
public enum DeviceStatus
{
    /// <summary>
    /// RESERVED, UNUSED — Phase 6.7 decision (docs/phase-6.7-device-licensing-policy.md
    /// §2): no manual device-approval workflow is designed or built. No code path may
    /// ever produce this value; device registration always creates a device directly as
    /// <see cref="Authorized"/>. Retained only in case a future, separately-approved
    /// phase designs an approval flow — deliberately not removed, at zero cost, per that
    /// decision.
    /// </summary>
    Pending,
    Authorized,
    Revoked,
}

/// <summary>Platform a <see cref="Domain.Entities.Device"/> was registered from. Android/iOS are named now (per Phase 6.1 §7) even though no platform-specific code exists yet — this only labels which platform a device row belongs to.</summary>
public enum DevicePlatform
{
    Windows,
    Android,
    iOS,
}
