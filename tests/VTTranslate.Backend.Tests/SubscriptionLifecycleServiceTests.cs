using VTTranslate.Backend.Application.Billing;
using VTTranslate.Backend.Domain.Abstractions;
using VTTranslate.Backend.Domain.Entities;
using VTTranslate.Backend.Domain.Enums;
using VTTranslate.Backend.Infrastructure.Persistence;

namespace VTTranslate.Backend.Tests;

public class SubscriptionLifecycleServiceTests
{
    private readonly InMemorySubscriptionRepository _subscriptions = new();
    private readonly InMemoryPlanRepository _plans = new();
    private readonly InMemoryAuditEventRepository _audit = new();
    private readonly FakeClock _clock = new();
    private readonly SubscriptionLifecycleService _service;

    public SubscriptionLifecycleServiceTests()
    {
        _service = new SubscriptionLifecycleService(_subscriptions, _plans, _audit, _clock);
    }

    private async Task<Subscription> SeedAsync(SubscriptionStatus status, DateTimeOffset periodEnd, string billingProviderSubscriptionId = "sub-1", IEnumerable<Entitlement>? entitlements = null)
    {
        var planId = Guid.NewGuid();
        _plans.Seed(new Plan { Id = planId, Name = "Test", IsPubliclyPurchasable = false }, entitlements ?? []);

        var subscription = new Subscription
        {
            Id = Guid.NewGuid(),
            AccountId = Guid.NewGuid(),
            PlanId = planId,
            Status = status,
            CurrentPeriodStart = _clock.UtcNow.AddDays(-30),
            CurrentPeriodEnd = periodEnd,
            BillingProviderSubscriptionId = billingProviderSubscriptionId,
            CreatedAt = _clock.UtcNow,
            UpdatedAt = _clock.UtcNow,
        };
        await _subscriptions.SaveAsync(subscription, CancellationToken.None);
        return subscription;
    }

    private static NormalizedBillingEvent Event(BillingEventType type, DateTimeOffset occurredAt, string billingProviderSubscriptionId = "sub-1") =>
        new("Paddle", Guid.NewGuid().ToString(), type, occurredAt, billingProviderSubscriptionId, "cust-1", "hash");

    // ---- Ordering: newer/older/equal-timestamp precedence ----

    [Fact]
    public async Task NewerEvent_Applies()
    {
        var subscription = await SeedAsync(SubscriptionStatus.Active, _clock.UtcNow.AddDays(30));
        var result = await _service.ApplyBillingEventAsync(Event(BillingEventType.PaymentFailed, _clock.UtcNow), CancellationToken.None);

        Assert.Equal(BillingEventApplicationOutcome.Applied, result.Outcome);
        Assert.Equal(SubscriptionStatus.PastDue, result.Subscription!.Status);
    }

    [Fact]
    public async Task OlderEvent_IsSuperseded_NeverApplied()
    {
        var subscription = await SeedAsync(SubscriptionStatus.Active, _clock.UtcNow.AddDays(30));
        await _service.ApplyBillingEventAsync(Event(BillingEventType.PaymentFailed, _clock.UtcNow), CancellationToken.None);

        // An older event (payment succeeded, but dated BEFORE the failure already applied) must never revert the more-recent fact.
        var stale = await _service.ApplyBillingEventAsync(Event(BillingEventType.PaymentSucceeded, _clock.UtcNow.AddMinutes(-5), "sub-1"), CancellationToken.None);

        Assert.Equal(BillingEventApplicationOutcome.Superseded, stale.Outcome);
        Assert.Equal(SubscriptionStatus.PastDue, stale.Subscription!.Status);
    }

    [Fact]
    public async Task EqualTimestamp_HigherPrecedenceEvent_Applies()
    {
        var now = _clock.UtcNow;
        await SeedAsync(SubscriptionStatus.Active, now.AddDays(30));

        // PaymentFailed (precedence 2) applied first.
        var first = await _service.ApplyBillingEventAsync(Event(BillingEventType.PaymentFailed, now), CancellationToken.None);
        Assert.Equal(BillingEventApplicationOutcome.Applied, first.Outcome);

        // Refunded (precedence 4) at the EXACT same timestamp must win the tie.
        var second = await _service.ApplyBillingEventAsync(Event(BillingEventType.Refunded, now), CancellationToken.None);

        Assert.Equal(BillingEventApplicationOutcome.Applied, second.Outcome);
        Assert.Equal(SubscriptionStatus.Refunded, second.Subscription!.Status);
    }

    [Fact]
    public async Task EqualTimestamp_LowerOrEqualPrecedenceEvent_IsSuperseded()
    {
        var now = _clock.UtcNow;
        await SeedAsync(SubscriptionStatus.Active, now.AddDays(30));

        var first = await _service.ApplyBillingEventAsync(Event(BillingEventType.PaymentFailed, now), CancellationToken.None);
        Assert.Equal(BillingEventApplicationOutcome.Applied, first.Outcome);

        // PaymentSucceeded (precedence 1) at the same timestamp as the already-applied
        // PaymentFailed (precedence 2) must NOT win the tie.
        var second = await _service.ApplyBillingEventAsync(Event(BillingEventType.PaymentSucceeded, now), CancellationToken.None);

        Assert.Equal(BillingEventApplicationOutcome.Superseded, second.Outcome);
        Assert.Equal(SubscriptionStatus.PastDue, second.Subscription!.Status);
    }

    // ---- LocallyCancelledAt: unconditional terminal guard ----

    [Fact]
    public async Task LocallyCancelledSubscription_SupersedesAnySubsequentEvent_RegardlessOfTimestampOrPrecedence()
    {
        var subscription = await SeedAsync(SubscriptionStatus.Active, _clock.UtcNow.AddDays(30));
        await _service.ApplyCustomerCancellationAsync(subscription, immediate: true, _clock.UtcNow, CancellationToken.None);

        // A "later", high-precedence Refunded event must still be superseded.
        var result = await _service.ApplyBillingEventAsync(
            Event(BillingEventType.Refunded, _clock.UtcNow.AddDays(1)), CancellationToken.None);

        Assert.Equal(BillingEventApplicationOutcome.Superseded, result.Outcome);
        Assert.Equal(SubscriptionStatus.Cancelled, result.Subscription!.Status);
    }

    [Fact]
    public async Task ApplyCustomerCancellationAsync_IsTheOnlyPathThatSetsLocallyCancelledAt()
    {
        var subscription = await SeedAsync(SubscriptionStatus.Active, _clock.UtcNow.AddDays(30));
        Assert.Null(subscription.LocallyCancelledAt);

        await _service.ApplyBillingEventAsync(Event(BillingEventType.PaymentFailed, _clock.UtcNow), CancellationToken.None);
        var afterWebhook = await _subscriptions.FindByAccountAsync(subscription.AccountId, CancellationToken.None);
        Assert.Null(afterWebhook!.LocallyCancelledAt); // webhook path never sets it

        await _service.ApplyCustomerCancellationAsync(afterWebhook, immediate: true, _clock.UtcNow, CancellationToken.None);
        var afterCancellation = await _subscriptions.FindByIdAsync(subscription.Id, CancellationToken.None);
        Assert.NotNull(afterCancellation!.LocallyCancelledAt);
    }

    [Fact]
    public async Task ApplyBillingEventAsync_NeverTouchesLocallyCancelledAtOrCancelAtPeriodEnd()
    {
        var subscription = await SeedAsync(SubscriptionStatus.Active, _clock.UtcNow.AddDays(30));
        subscription.CancelAtPeriodEnd = true;
        await _subscriptions.SaveAsync(subscription, CancellationToken.None);

        await _service.ApplyBillingEventAsync(Event(BillingEventType.PaymentSucceeded, _clock.UtcNow), CancellationToken.None);

        var reloaded = await _subscriptions.FindByIdAsync(subscription.Id, CancellationToken.None);
        Assert.Null(reloaded!.LocallyCancelledAt);
        Assert.True(reloaded.CancelAtPeriodEnd); // never cleared by a webhook
    }

    // ---- CancelAtPeriodEnd independence from provider-driven renewal ----

    [Fact]
    public async Task CancelAtPeriodEnd_NotImmediate_DoesNotChangeStatus()
    {
        var subscription = await SeedAsync(SubscriptionStatus.Active, _clock.UtcNow.AddDays(30));

        var updated = await _service.ApplyCustomerCancellationAsync(subscription, immediate: false, _clock.UtcNow, CancellationToken.None);

        Assert.Equal(SubscriptionStatus.Active, updated.Status);
        Assert.True(updated.CancelAtPeriodEnd);
        Assert.Null(updated.LocallyCancelledAt);
    }

    // ---- Terminal-status guard (Refunded/Expired never re-open via a later webhook) ----

    [Fact]
    public async Task RefundedSubscription_RejectsAnyFurtherEvent()
    {
        var subscription = await SeedAsync(SubscriptionStatus.Refunded, _clock.UtcNow.AddDays(30));

        var result = await _service.ApplyBillingEventAsync(Event(BillingEventType.PaymentSucceeded, _clock.UtcNow.AddDays(1)), CancellationToken.None);

        Assert.Equal(BillingEventApplicationOutcome.RejectedInvalidTransition, result.Outcome);
    }

    // ---- Unknown correlation ----

    [Fact]
    public async Task UnknownBillingProviderSubscriptionId_RejectedAsUnknownCorrelation_NeverAutoCreates()
    {
        var result = await _service.ApplyBillingEventAsync(
            Event(BillingEventType.PaymentSucceeded, _clock.UtcNow, billingProviderSubscriptionId: "does-not-exist"), CancellationToken.None);

        Assert.Equal(BillingEventApplicationOutcome.RejectedUnknownCorrelation, result.Outcome);
        Assert.Null(result.Subscription);
    }

    // ---- ReconcileTimeBasedTransitionsAsync: composed bounds, independence from billing cursor ----

    [Fact]
    public async Task PastDue_CrossesPastDueGraceDays_TransitionsToGracePeriod()
    {
        var subscription = await SeedAsync(
            SubscriptionStatus.PastDue, _clock.UtcNow.AddDays(-5),
            entitlements: [new Entitlement { Id = Guid.NewGuid(), PlanId = Guid.Empty, Key = EntitlementKeys.PastDueGraceDays, Value = "3" },
                           new Entitlement { Id = Guid.NewGuid(), PlanId = Guid.Empty, Key = EntitlementKeys.GracePeriodDays, Value = "30" }]);

        var reconciled = await _service.ReconcileTimeBasedTransitionsAsync(subscription, _clock.UtcNow, CancellationToken.None);

        Assert.Equal(SubscriptionStatus.GracePeriod, reconciled.Status);
    }

    [Fact]
    public async Task ComposedBounds_GracePeriodDaysLessThanPastDueGraceDays_LandsDirectlyOnExpired_NeverTransientlyGracePeriod()
    {
        // GracePeriodDays (1) < PastDueGraceDays (10) — by the time PastDueGraceDays would
        // fire, the Expired bound has already passed too. Expired must be evaluated FIRST.
        var subscription = await SeedAsync(
            SubscriptionStatus.PastDue, _clock.UtcNow.AddDays(-20),
            entitlements: [new Entitlement { Id = Guid.NewGuid(), PlanId = Guid.Empty, Key = EntitlementKeys.PastDueGraceDays, Value = "10" },
                           new Entitlement { Id = Guid.NewGuid(), PlanId = Guid.Empty, Key = EntitlementKeys.GracePeriodDays, Value = "1" }]);

        var reconciled = await _service.ReconcileTimeBasedTransitionsAsync(subscription, _clock.UtcNow, CancellationToken.None);

        Assert.Equal(SubscriptionStatus.Expired, reconciled.Status);
    }

    [Fact]
    public async Task GracePeriod_CrossesGracePeriodDays_TransitionsToExpired()
    {
        var subscription = await SeedAsync(
            SubscriptionStatus.GracePeriod, _clock.UtcNow.AddDays(-10),
            entitlements: [new Entitlement { Id = Guid.NewGuid(), PlanId = Guid.Empty, Key = EntitlementKeys.GracePeriodDays, Value = "3" }]);

        var reconciled = await _service.ReconcileTimeBasedTransitionsAsync(subscription, _clock.UtcNow, CancellationToken.None);

        Assert.Equal(SubscriptionStatus.Expired, reconciled.Status);
    }

    [Fact]
    public async Task CancelAtPeriodEnd_PeriodHasEnded_TransitionsToCancelled()
    {
        var subscription = await SeedAsync(SubscriptionStatus.Active, _clock.UtcNow.AddDays(-1));
        subscription.CancelAtPeriodEnd = true;
        await _subscriptions.SaveAsync(subscription, CancellationToken.None);

        var reconciled = await _service.ReconcileTimeBasedTransitionsAsync(subscription, _clock.UtcNow, CancellationToken.None);

        Assert.Equal(SubscriptionStatus.Cancelled, reconciled.Status);
    }

    [Fact]
    public async Task ReconcileTimeBasedTransitionsAsync_NoBoundCrossed_NoOp()
    {
        var subscription = await SeedAsync(SubscriptionStatus.Active, _clock.UtcNow.AddDays(30));

        var reconciled = await _service.ReconcileTimeBasedTransitionsAsync(subscription, _clock.UtcNow, CancellationToken.None);

        Assert.Equal(SubscriptionStatus.Active, reconciled.Status);
    }

    [Fact]
    public async Task ReconcileTimeBasedTransitionsAsync_NeverTouchesBillingEventOrderingCursor()
    {
        var subscription = await SeedAsync(
            SubscriptionStatus.PastDue, _clock.UtcNow.AddDays(-5),
            entitlements: [new Entitlement { Id = Guid.NewGuid(), PlanId = Guid.Empty, Key = EntitlementKeys.PastDueGraceDays, Value = "3" },
                           new Entitlement { Id = Guid.NewGuid(), PlanId = Guid.Empty, Key = EntitlementKeys.GracePeriodDays, Value = "30" }]);
        subscription.LastBillingEventAt = _clock.UtcNow.AddDays(-100);
        subscription.LastBillingEventPrecedence = 1;
        await _subscriptions.SaveAsync(subscription, CancellationToken.None);

        var reconciled = await _service.ReconcileTimeBasedTransitionsAsync(subscription, _clock.UtcNow, CancellationToken.None);

        Assert.Equal(_clock.UtcNow.AddDays(-100), reconciled.LastBillingEventAt);
        Assert.Equal(1, reconciled.LastBillingEventPrecedence);
    }
}
