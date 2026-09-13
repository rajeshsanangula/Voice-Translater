using VTTranslate.Backend.Domain.Enums;

namespace VTTranslate.Backend.Domain.Entities;

/// <summary>One account's subscription to a plan — see Phase 6.1 §4/§8, unchanged through Phase 6.2B. <see cref="BillingProviderSubscriptionId"/> is an OPAQUE handle so the billing provider (Paddle or otherwise) is swappable without a schema change (Phase 6.2B §9).</summary>
public sealed class Subscription
{
    public required Guid Id { get; init; }
    public required Guid AccountId { get; init; }
    public required Guid PlanId { get; set; }
    public required SubscriptionStatus Status { get; set; }

    public DateTimeOffset CurrentPeriodStart { get; set; }
    public DateTimeOffset CurrentPeriodEnd { get; set; }

    /// <summary>Opaque reference into whichever <see cref="Abstractions.IBillingProvider"/> is configured. Null for Trial subscriptions (no billing provider involved yet).</summary>
    public string? BillingProviderSubscriptionId { get; set; }

    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; set; }

    // ---- Phase 6.6 additions ----

    /// <summary>Provider-event ordering cursor — written ONLY by ISubscriptionLifecycleService.ApplyBillingEventAsync, never by any other code path (never by customer actions, never by time-based reconciliation). Null until the first billing event is ever applied.</summary>
    public DateTimeOffset? LastBillingEventAt { get; set; }

    /// <summary>The fixed precedence rank (see ISubscriptionLifecycleService's ordering algorithm) of the last-applied billing event — written only alongside <see cref="LastBillingEventAt"/>, in the same operation, never independently. Used to break exact-timestamp ties deterministically.</summary>
    public int? LastBillingEventPrecedence { get; set; }

    /// <summary>Set ONLY by the authenticated customer-cancellation path (ISubscriptionLifecycleService.ApplyCustomerCancellationAsync, immediate=true). Once set, this subscription is terminal — no billing event, regardless of timestamp or precedence, may ever change its Status again. Independent of the billing-event ordering cursor above by design (a customer action is not a billing-provider event).</summary>
    public DateTimeOffset? LocallyCancelledAt { get; set; }

    /// <summary>Customer-intent flag, independent of Status and of the billing-event cursor. Set by ApplyCustomerCancellationAsync(immediate:false); cleared only by an explicit customer "un-cancel" action (not built in Phase 6.6). A provider-driven renewal must never clear this flag.</summary>
    public bool CancelAtPeriodEnd { get; set; }
}
