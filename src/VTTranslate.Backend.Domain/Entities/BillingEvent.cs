using VTTranslate.Backend.Domain.Enums;

namespace VTTranslate.Backend.Domain.Entities;

/// <summary>
/// Phase 6.6 — the durable idempotency ledger for received billing-provider webhooks.
/// <c>(Provider, ProviderEventId)</c> carries a database-level unique constraint (see
/// the EF configuration) — this is the entire idempotency mechanism: a duplicate
/// delivery is caught as a constraint violation on insert, never as an application-level
/// "have I seen this before" query. Never stores the raw webhook payload (only a hash) —
/// no payment/card data or secrets are ever retained here.
/// </summary>
public sealed class BillingEvent
{
    public required Guid Id { get; init; }

    /// <summary>Which IBillingProvider implementation this event came from (e.g. "Paddle") — diagnostics/audit only, mirrors Account.ExternalIdentityProvider's pattern.</summary>
    public required string Provider { get; init; }

    /// <summary>The provider's own event/notification identifier — half of the idempotency key.</summary>
    public required string ProviderEventId { get; init; }

    /// <summary>Normalized, vendor-neutral event type (e.g. "PaymentSucceeded") — never a raw provider-specific event name.</summary>
    public required string EventType { get; init; }

    /// <summary>Resolved AFTER verification, set once correlation is attempted; null if the event could not be correlated to a known account.</summary>
    public Guid? AccountId { get; set; }

    /// <summary>Resolved AFTER verification, set once correlation is attempted; null if the event could not be correlated to a known subscription.</summary>
    public Guid? SubscriptionId { get; set; }

    public required DateTimeOffset ReceivedAt { get; init; }

    /// <summary>Set only when ProcessingStatus transitions to a terminal or Received-successor value.</summary>
    public DateTimeOffset? ProcessedAt { get; set; }

    public required BillingEventProcessingStatus ProcessingStatus { get; set; }

    /// <summary>Safe-to-log reason only (e.g. "unknown event type", "stale event") — never provider exception text, never payload content.</summary>
    public string? RejectionReason { get; set; }

    /// <summary>A hash of the raw payload — for correlation/debugging only. The raw payload itself is NEVER persisted.</summary>
    public required string RawPayloadHash { get; init; }
}
