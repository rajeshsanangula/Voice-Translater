namespace VTTranslate.Backend.Domain.Entities;

/// <summary>
/// One (device, refresh-token-family) login session — see Phase 6.1 §8/§10. This is the
/// AUTHENTICATION session record (sign-out-this-device / sign-out-everywhere), NOT a
/// real-time translation session — that concept (an issued, short-lived provider token)
/// is a Phase 6.6+ concern and is intentionally not modeled as its own entity yet; see
/// docs/phase-6.3-backend-foundation.md "Usage boundary" for why the two must not be
/// conflated.
/// </summary>
public sealed class Session
{
    public required Guid Id { get; init; }
    public required Guid AccountId { get; init; }
    public required Guid DeviceId { get; init; }

    /// <summary>Identifies the refresh-token rotation chain for replay detection (Phase 6.1 §10) — not the token value itself, which is never stored server-side in plaintext by this domain.</summary>
    public required Guid RefreshTokenFamilyId { get; init; }

    public DateTimeOffset IssuedAt { get; init; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>For audit only (Phase 6.1 §10) — never used for any authorization decision.</summary>
    public string? IpAddress { get; init; }

    public bool IsActive => RevokedAt is null && ExpiresAt > DateTimeOffset.UtcNow;
}
