namespace VTTranslate.Backend.Api.Security;

/// <summary>
/// Phase 7.0 — non-secret gated-JIT provisioning policy configuration (see
/// docs/phase-7.0-production-identity-and-account-lifecycle.md §7/§14/§15/§16). All
/// values are safe, documented defaults per the master-prompt instruction ("use sensible
/// safe defaults only where technically required, clearly document them as defaults, and
/// keep them configurable") — none are invented business/product policy values; the
/// product owner may change any of these without a source-code change.
/// </summary>
public sealed class AccountProvisioningOptions
{
    public const string SectionName = "AccountProvisioning";

    /// <summary>Master on/off switch for automatic provisioning. Default true (Option D approved) — setting false reverts to Phase 6.4's original "no auto-provisioning" behavior without any code change.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Default true — matches §16's explicit instruction to fail closed for immediate Active provisioning unless the identity's email is Entra-verified.</summary>
    public bool RequireEmailVerified { get; set; } = true;

    /// <summary>Maximum provisioning attempts per identity within <see cref="RateLimitWindowSeconds"/>. Default 5 — a safe, conservative default; not a product-approved specific threshold (see the architecture document §26, item 2, still open).</summary>
    public int RateLimitMaxAttempts { get; set; } = 5;

    /// <summary>Default 3600 (1 hour).</summary>
    public int RateLimitWindowSeconds { get; set; } = 3600;
}
