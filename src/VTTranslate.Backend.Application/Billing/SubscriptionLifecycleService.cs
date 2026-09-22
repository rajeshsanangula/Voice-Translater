using VTTranslate.Backend.Domain.Abstractions;
using VTTranslate.Backend.Domain.Entities;
using VTTranslate.Backend.Domain.Enums;

namespace VTTranslate.Backend.Application.Billing;

public sealed class SubscriptionLifecycleService(
    ISubscriptionRepository subscriptions,
    IPlanRepository plans,
    IAuditEventRepository audit,
    IClock clock) : ISubscriptionLifecycleService
{
    // Fixed precedence table (Phase 6.6 approved design) — used ONLY to break an exact
    // OccurredAt tie between two distinct events for the same subscription. No default
    // rank exists; every recognized BillingEventType must appear here.
    private static readonly IReadOnlyDictionary<BillingEventType, int> Precedence = new Dictionary<BillingEventType, int>
    {
        [BillingEventType.Refunded] = 4,
        [BillingEventType.ChargebackReceived] = 4,
        [BillingEventType.CancellationRequested] = 3,
        [BillingEventType.PaymentFailed] = 2,
        [BillingEventType.PaymentSucceeded] = 1,
        [BillingEventType.TrialConverted] = 1,
        [BillingEventType.SubscriptionCreated] = 1,
    };

    private static readonly SubscriptionStatus[] TerminalStatuses = [SubscriptionStatus.Refunded, SubscriptionStatus.Expired];

    public async Task<BillingEventApplicationResult> ApplyBillingEventAsync(NormalizedBillingEvent normalizedEvent, CancellationToken ct)
    {
        // Correlation: Phase 6.6 correlates a webhook to an AUTRAXIS subscription ONLY
        // via the opaque BillingProviderSubscriptionId already linked to an existing row
        // — there is no Account<->billing-provider-customer mapping in this phase's
        // schema, so a "SubscriptionCreated" event for a brand-new provider subscription
        // cannot be correlated here and is rejected as an unknown correlation (a
        // documented, flagged limitation — see docs/phase-6.6-billing-subscription.md
        // "Deferred Work" — never silently invented).
        var subscription = normalizedEvent.BillingProviderSubscriptionId is null
            ? null
            : await subscriptions.FindByBillingProviderSubscriptionIdAsync(normalizedEvent.BillingProviderSubscriptionId, ct);

        if (subscription is null)
        {
            await AuditAsync(null, null, "WebhookUnknownAccountOrSubscription", $"eventType={normalizedEvent.EventType}", ct);
            return new BillingEventApplicationResult(BillingEventApplicationOutcome.RejectedUnknownCorrelation, null, "unknown subscription correlation");
        }

        // Unconditional terminal guards (Phase 6.6 design) — evaluated BEFORE ordering:
        // a customer's own explicit immediate cancellation always wins, regardless of
        // any provider timestamp claim; likewise a subscription already in a terminal
        // provider-driven state (Refunded/Expired) never re-opens via a later event.
        if (subscription.LocallyCancelledAt is not null)
        {
            await AuditAsync(subscription.AccountId, subscription.Id, "WebhookSupersededByCustomerCancellation", $"eventType={normalizedEvent.EventType}", ct);
            return new BillingEventApplicationResult(BillingEventApplicationOutcome.Superseded, subscription, "subscription was locally cancelled by the customer");
        }

        if (TerminalStatuses.Contains(subscription.Status))
        {
            await AuditAsync(subscription.AccountId, subscription.Id, "WebhookRejectedTerminalSubscription", $"eventType={normalizedEvent.EventType}, status={subscription.Status}", ct);
            return new BillingEventApplicationResult(BillingEventApplicationOutcome.RejectedInvalidTransition, subscription, $"subscription is already in a terminal state ({subscription.Status})");
        }

        // Deterministic ordering: strictly-newer OccurredAt applies; strictly-older is
        // superseded; an exact tie is broken by the fixed precedence table.
        var eventPrecedence = Precedence[normalizedEvent.EventType];
        var isNewer = subscription.LastBillingEventAt is null || normalizedEvent.OccurredAt > subscription.LastBillingEventAt;
        var isTieBrokenInFavor = !isNewer
            && normalizedEvent.OccurredAt == subscription.LastBillingEventAt
            && eventPrecedence > (subscription.LastBillingEventPrecedence ?? 0);

        if (!isNewer && !isTieBrokenInFavor)
        {
            await AuditAsync(subscription.AccountId, subscription.Id, "WebhookSuperseded", $"eventType={normalizedEvent.EventType}", ct);
            return new BillingEventApplicationResult(BillingEventApplicationOutcome.Superseded, subscription, "event is older than, or not higher-precedence than, the last-applied event");
        }

        var newStatus = normalizedEvent.EventType switch
        {
            BillingEventType.TrialConverted or BillingEventType.PaymentSucceeded => SubscriptionStatus.Active,
            BillingEventType.PaymentFailed => SubscriptionStatus.PastDue,
            BillingEventType.CancellationRequested => SubscriptionStatus.Cancelled,
            BillingEventType.Refunded or BillingEventType.ChargebackReceived => SubscriptionStatus.Refunded,
            BillingEventType.SubscriptionCreated => subscription.Status, // no-op; creation is handled at correlation time, not here — see comment above
            _ => subscription.Status,
        };

        subscription.Status = newStatus;
        subscription.LastBillingEventAt = normalizedEvent.OccurredAt;
        subscription.LastBillingEventPrecedence = eventPrecedence;
        subscription.UpdatedAt = clock.UtcNow;
        await subscriptions.SaveAsync(subscription, ct);

        await AuditAsync(subscription.AccountId, subscription.Id, $"Subscription{newStatus}", $"eventType={normalizedEvent.EventType}", ct);
        return new BillingEventApplicationResult(BillingEventApplicationOutcome.Applied, subscription, null);
    }

    public async Task<Subscription> ApplyCustomerCancellationAsync(Subscription subscription, bool immediate, DateTimeOffset now, CancellationToken ct)
    {
        if (immediate)
        {
            subscription.Status = SubscriptionStatus.Cancelled;
            subscription.LocallyCancelledAt = now;
            subscription.UpdatedAt = now;
            await AuditAsync(subscription.AccountId, subscription.Id, "SubscriptionCancelledImmediatelyByCustomer", null, ct);
        }
        else
        {
            subscription.CancelAtPeriodEnd = true;
            subscription.UpdatedAt = now;
            await AuditAsync(subscription.AccountId, subscription.Id, "SubscriptionCancelAtPeriodEndRequestedByCustomer", null, ct);
        }

        await subscriptions.SaveAsync(subscription, ct);
        return subscription;
    }

    public async Task<Subscription> ReconcileTimeBasedTransitionsAsync(Subscription subscription, DateTimeOffset now, CancellationToken ct)
    {
        // Purely time-based — never reads/writes LastBillingEventAt/
        // LastBillingEventPrecedence/LocallyCancelledAt. Idempotent: re-evaluating an
        // already-reconciled subscription is always a no-op.
        if (TerminalStatuses.Contains(subscription.Status) || subscription.Status == SubscriptionStatus.Cancelled)
            return subscription; // nothing to reconcile once terminal

        var entitlements = await plans.GetEntitlementsAsync(subscription.PlanId, ct);
        var pastDueGraceDays = ReadInt(entitlements, EntitlementKeys.PastDueGraceDays);
        var gracePeriodDays = ReadInt(entitlements, EntitlementKeys.GracePeriodDays);

        var expiredBound = gracePeriodDays is not null ? subscription.CurrentPeriodEnd.AddDays(gracePeriodDays.Value) : (DateTimeOffset?)null;
        var pastDueToGraceBound = pastDueGraceDays is not null ? subscription.CurrentPeriodEnd.AddDays(pastDueGraceDays.Value) : (DateTimeOffset?)null;

        var changed = false;

        // Evaluate the Expired predicate FIRST — guarantees a subscription that has
        // already crossed both bounds lands on Expired directly, never transiently on
        // GracePeriod (see docs/phase-6.6-billing-subscription.md "PastDue -> GracePeriod").
        // Phase 25B: an unpaid Trial has no grace period — once its period has ended it is Expired (deterministic,
        // time-based; no background job: this runs whenever the subscription is read/reconciled, and EntitlementService
        // independently denies an ended Trial even before this transition is persisted).
        if (subscription.Status == SubscriptionStatus.Trial && now > subscription.CurrentPeriodEnd)
        {
            subscription.Status = SubscriptionStatus.Expired;
            changed = true;
            await AuditAsync(subscription.AccountId, subscription.Id, "TrialExpiredByTimeReconciliation", null, ct);
        }
        else if ((subscription.Status == SubscriptionStatus.PastDue || subscription.Status == SubscriptionStatus.GracePeriod)
            && expiredBound is not null && now > expiredBound.Value)
        {
            subscription.Status = SubscriptionStatus.Expired;
            changed = true;
            await AuditAsync(subscription.AccountId, subscription.Id, "SubscriptionExpiredByTimeReconciliation", null, ct);
        }
        else if (subscription.Status == SubscriptionStatus.PastDue && pastDueToGraceBound is not null && now > pastDueToGraceBound.Value)
        {
            subscription.Status = SubscriptionStatus.GracePeriod;
            changed = true;
            await AuditAsync(subscription.AccountId, subscription.Id, "SubscriptionEnteredGracePeriod", null, ct);
        }
        else if (subscription.Status == SubscriptionStatus.Active && subscription.CancelAtPeriodEnd && now >= subscription.CurrentPeriodEnd)
        {
            subscription.Status = SubscriptionStatus.Cancelled;
            changed = true;
            await AuditAsync(subscription.AccountId, subscription.Id, "SubscriptionCancelledAtPeriodEnd", null, ct);
        }

        if (!changed) return subscription;

        subscription.UpdatedAt = now;
        try
        {
            await subscriptions.SaveAsync(subscription, ct);
        }
        catch (Domain.ConcurrentUpdateException) // see the method's own doc comment
        {
            // Idempotent-safe: re-read and re-evaluate rather than blindly retrying the
            // write. Every predicate above is a pure function of time/status, so if
            // another caller already applied the same transition, re-evaluating finds
            // nothing left to do; if a different transition is now valid, this recursive
            // call applies it instead.
            var fresh = await subscriptions.FindByIdAsync(subscription.Id, ct) ?? subscription;
            return await ReconcileTimeBasedTransitionsAsync(fresh, now, ct);
        }

        return subscription;
    }

    private async Task AuditAsync(Guid? accountId, Guid? subscriptionId, string eventType, string? metadata, CancellationToken ct) =>
        await audit.AddAsync(new AuditEvent
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            EventType = eventType,
            Metadata = subscriptionId is null ? metadata : $"subscriptionId={subscriptionId};{metadata}",
            OccurredAt = clock.UtcNow,
        }, ct);

    private static int? ReadInt(IReadOnlyList<Entitlement> entitlements, string key)
    {
        var value = entitlements.FirstOrDefault(e => e.Key == key)?.Value;
        return value is not null && int.TryParse(value, out var parsed) ? parsed : null;
    }
}
