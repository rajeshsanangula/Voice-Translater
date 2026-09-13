namespace VTTranslate.Backend.Application.Identity;

/// <summary>
/// Phase 7.0 — server-controlled provisioning-rate-limiting abstraction (see
/// docs/phase-7.0-production-identity-and-account-lifecycle.md §7/§15). Application
/// depends only on this interface; the concrete storage mechanism (in-memory,
/// distributed cache, etc.) lives entirely in Infrastructure. Callers key attempts by a
/// caller-chosen string that must never itself be raw PII persisted beyond the window
/// this limiter tracks — Phase 7.0 keys on the external identity's own
/// (provider, subject) pair, never an IP address, keeping the key itself
/// non-enumerable and consistent with the account's own identity mapping.
/// </summary>
public interface IProvisioningRateLimiter
{
    /// <summary>
    /// Returns true iff a new attempt under <paramref name="key"/> is permitted right
    /// now, given at most <paramref name="maxAttempts"/> attempts per
    /// <paramref name="window"/>, and records this attempt if permitted (so the caller
    /// must call this at most once per real attempt — it is not a read-only check).
    /// Fails closed (returns false) for non-positive <paramref name="maxAttempts"/> or a
    /// non-positive <paramref name="window"/> — invalid configuration must never be
    /// silently treated as "unlimited."
    /// </summary>
    Task<bool> TryAcquireAsync(string key, int maxAttempts, TimeSpan window, CancellationToken ct);
}
