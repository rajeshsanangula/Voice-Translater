namespace VTTranslate.Backend.Domain.Enums;

/// <summary>
/// Backend-authoritative account usability state (Phase 6.4; extended Phase 7.0 — see
/// docs/phase-7.0-production-identity-and-account-lifecycle.md §8). Distinct from
/// <see cref="Entities.Account.DeletionRequestedAt"/>-derived soft-delete — a
/// <see cref="Suspended"/> account is a deliberate administrative action (e.g. abuse,
/// fraud, chargeback), not a customer-initiated deletion. Never inferred from an Entra
/// claim, never client-settable, never inferred from Subscription status — only ever
/// changed by <c>IAccountLifecycleService</c> (Phase 7.0) or the gated provisioning
/// flow (<c>IAccountProvisioningService</c>). Only <see cref="Active"/> is usable
/// (<see cref="Entities.Account.IsUsable"/>) — every other value denies access at the
/// same account-resolution boundary, uniformly, so a client cannot distinguish which
/// non-usable state applies (anti-enumeration).
/// </summary>
public enum AccountStatus
{
    /// <summary>Provisioned but not yet usable (e.g. the gated JIT provisioning policy did not grant immediate Active status). No code path produces this today unless a future provisioning-policy configuration explicitly enables a "pending" outcome — reserved, not dead, mirroring the existing <c>DeviceStatus.Pending</c> precedent.</summary>
    Pending,

    Active,
    Suspended,

    /// <summary>Longer-term deactivation short of terminal closure (e.g. a sustained policy violation) — distinct from <see cref="Suspended"/>'s typically-temporary administrative-hold connotation. Both deny identically at the resolution boundary; the distinction exists for administrative/audit clarity, not for different customer-visible behavior.</summary>
    Disabled,

    /// <summary>Terminal. No transition out of this state is valid (see <c>IAccountLifecycleService</c>). Never deletes prior <c>UsageRecord</c>/<c>BillingEvent</c>/<c>AuditEvent</c> history — closure is additive, never destructive.</summary>
    Closed,
}
