namespace VTTranslate.Backend.Domain.Enums;

/// <summary>
/// Phase 6.6 — the lifecycle of one received billing webhook event, as recorded in
/// <see cref="Entities.BillingEvent"/>. See that entity's own doc comment and
/// docs/phase-6.6-billing-subscription.md for the exact transaction boundaries that
/// produce each value.
/// </summary>
public enum BillingEventProcessingStatus
{
    /// <summary>Durably persisted immediately after signature verification succeeds, before any attempt to apply it. The only state a redelivered event can be re-attempted from (along with <see cref="Failed"/>).</summary>
    Received,

    /// <summary>Applied successfully — a corresponding Subscription write committed in the same transaction as this status.</summary>
    Processed,

    /// <summary>Authentic but never applied: malformed content, unknown event type, or unknown account/subscription correlation. Terminal — never retried.</summary>
    Rejected,

    /// <summary>Authentic, resolvable, but stale or terminally guarded (see the ordering algorithm and the LocallyCancelledAt guard) — a corresponding, more-authoritative fact already applies. Terminal — never retried.</summary>
    Superseded,

    /// <summary>Transient infrastructure failure during the application attempt (never a content/authenticity/ordering problem, which have their own terminal statuses above). Retryable — a redelivery resumes processing from this state.</summary>
    Failed,
}
