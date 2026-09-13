using VTTranslate.Backend.Domain.Abstractions;
using VTTranslate.Backend.Domain.Entities;

namespace VTTranslate.Backend.Application.Billing;

/// <summary>
/// Phase 6.6 — the SOLE writer of <see cref="Subscription.Status"/>,
/// <see cref="Subscription.LastBillingEventAt"/>,
/// <see cref="Subscription.LastBillingEventPrecedence"/>,
/// <see cref="Subscription.LocallyCancelledAt"/>, and
/// <see cref="Subscription.CancelAtPeriodEnd"/>. No endpoint, no other Application
/// service, and no repository call outside this service's implementation may write any
/// of these fields directly. Exposes exactly three entry points with non-overlapping
/// responsibility — see each method's own doc comment.
/// </summary>
public interface ISubscriptionLifecycleService
{
    /// <summary>
    /// Applies one already-authenticity-verified billing event (from
    /// <see cref="IBillingWebhookProcessor"/> only — never from any HTTP-authenticated
    /// customer request path). Resolves the target Subscription itself (by
    /// <see cref="Subscription.BillingProviderSubscriptionId"/>), runs the
    /// <see cref="Subscription.LocallyCancelledAt"/> terminal guard, then the
    /// timestamp/precedence ordering algorithm, then the state transition — never
    /// touches <see cref="Subscription.LocallyCancelledAt"/> or
    /// <see cref="Subscription.CancelAtPeriodEnd"/>.
    /// </summary>
    Task<BillingEventApplicationResult> ApplyBillingEventAsync(NormalizedBillingEvent normalizedEvent, CancellationToken ct);

    /// <summary>
    /// Applies an authenticated customer's cancellation request. This is the ONLY place
    /// <see cref="Subscription.LocallyCancelledAt"/> is ever written. Never touches
    /// <see cref="Subscription.LastBillingEventAt"/>/<see cref="Subscription.LastBillingEventPrecedence"/>
    /// — a customer action is not a billing-provider event and must never be recorded on
    /// the provider-event ordering cursor.
    /// </summary>
    Task<Subscription> ApplyCustomerCancellationAsync(Subscription subscription, bool immediate, DateTimeOffset now, CancellationToken ct);

    /// <summary>
    /// Applies ONLY the three time-based transitions (PastDue-to-GracePeriod,
    /// PastDue/GracePeriod-to-Expired, CancelAtPeriodEnd-triggered-to-Cancelled) — purely
    /// a function of the current Status/CurrentPeriodEnd/CancelAtPeriodEnd and the
    /// current clock. Never reads or writes LastBillingEventAt/LastBillingEventPrecedence/
    /// LocallyCancelledAt — categorically not a billing-event-ordering operation. Called
    /// at the start of every subscription read (EntitlementService, GET /subscription) so
    /// no caller ever observes a stale time-crossed status. Idempotent: if no transition
    /// applies, returns immediately with no write.
    /// </summary>
    Task<Subscription> ReconcileTimeBasedTransitionsAsync(Subscription subscription, DateTimeOffset now, CancellationToken ct);
}

public enum BillingEventApplicationOutcome
{
    Applied,
    Superseded,
    RejectedUnknownCorrelation,
    RejectedInvalidTransition,
}

public sealed record BillingEventApplicationResult(BillingEventApplicationOutcome Outcome, Subscription? Subscription, string? Reason);
