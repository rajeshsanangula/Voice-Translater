namespace VTTranslate.Backend.Domain.Enums;

/// <summary>Device authorization state — see docs/phase-6.2b-resolved-architecture-decisions.md §6 (account-based, server-authorized devices; explicitly NOT hardware-fingerprint-primary).</summary>
public enum DeviceStatus
{
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
