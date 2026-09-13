using VTTranslate.Backend.Application.Billing;
using VTTranslate.Backend.Domain.Abstractions;
using VTTranslate.Backend.Domain.Entities;
using VTTranslate.Backend.Domain.Enums;
using VTTranslate.Backend.Infrastructure.Persistence;

namespace VTTranslate.Backend.Tests;

/// <summary>
/// A deterministic, test-only IBillingProvider double — no real Paddle SDK, no real
/// signing key, no network call. Exercises the trust-boundary table
/// (docs/phase-6.6-billing-subscription.md §7/§8) against IBillingWebhookProcessor.
/// </summary>
/// <summary>Throws unconditionally from ApplyBillingEventAsync — simulates a transient
/// infrastructure failure DURING the application attempt (Phase 6.6 §7/§8's "Failed"
/// path), as distinct from a content/authenticity/ordering rejection (which
/// SubscriptionLifecycleService itself returns as a normal result, never an exception).</summary>
internal sealed class ThrowingSubscriptionLifecycleService : ISubscriptionLifecycleService
{
    public Task<BillingEventApplicationResult> ApplyBillingEventAsync(NormalizedBillingEvent normalizedEvent, CancellationToken ct) =>
        throw new InvalidOperationException("simulated transient infrastructure failure");

    public Task<Subscription> ApplyCustomerCancellationAsync(Subscription subscription, bool immediate, DateTimeOffset now, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<Subscription> ReconcileTimeBasedTransitionsAsync(Subscription subscription, DateTimeOffset now, CancellationToken ct) =>
        throw new NotSupportedException();
}

internal sealed class FakeBillingProvider : IBillingProvider
{
    public string ProviderName => "FakeProvider";
    public NormalizedBillingEvent? NextResult { get; set; }
    public bool RejectSignature { get; set; }

    public Task<BillingCustomer?> FindCustomerAsync(Guid accountId, CancellationToken ct) => Task.FromResult<BillingCustomer?>(null);
    public Task<BillingSubscriptionState?> GetSubscriptionStateAsync(string billingProviderSubscriptionId, CancellationToken ct) => Task.FromResult<BillingSubscriptionState?>(null);

    public Task<NormalizedBillingEvent?> TryVerifyAndNormalizeWebhookAsync(string rawPayload, IReadOnlyDictionary<string, string> headers, CancellationToken ct) =>
        Task.FromResult(RejectSignature ? null : NextResult);
}

public class BillingWebhookProcessorTests
{
    private readonly FakeBillingProvider _provider = new();
    private readonly InMemoryBillingEventRepository _billingEvents = new();
    private readonly InMemorySubscriptionRepository _subscriptions = new();
    private readonly InMemoryPlanRepository _plans = new();
    private readonly InMemoryAuditEventRepository _audit = new();
    private readonly InMemoryUnitOfWork _unitOfWork = new();
    private readonly FakeClock _clock = new();
    private readonly SubscriptionLifecycleService _lifecycle;
    private readonly BillingWebhookProcessor _processor;

    public BillingWebhookProcessorTests()
    {
        _lifecycle = new SubscriptionLifecycleService(_subscriptions, _plans, _audit, _clock);
        _processor = new BillingWebhookProcessor(_provider, _billingEvents, _lifecycle, _unitOfWork, _audit, _clock);
    }

    private async Task<Subscription> SeedSubscriptionAsync(string billingProviderSubscriptionId = "sub-1")
    {
        var planId = Guid.NewGuid();
        _plans.Seed(new Plan { Id = planId, Name = "Test", IsPubliclyPurchasable = false }, []);
        var subscription = new Subscription
        {
            Id = Guid.NewGuid(),
            AccountId = Guid.NewGuid(),
            PlanId = planId,
            Status = SubscriptionStatus.Active,
            CurrentPeriodStart = _clock.UtcNow.AddDays(-1),
            CurrentPeriodEnd = _clock.UtcNow.AddDays(30),
            BillingProviderSubscriptionId = billingProviderSubscriptionId,
        };
        await _subscriptions.SaveAsync(subscription, CancellationToken.None);
        return subscription;
    }

    // ---- Trust boundary ----

    [Fact]
    public async Task InvalidSignature_NoRowPersisted_Returns400()
    {
        _provider.RejectSignature = true;

        var result = await _processor.ProcessAsync("raw", new Dictionary<string, string>(), CancellationToken.None);

        Assert.Equal(400, result.HttpStatusCode);
        Assert.Empty(_audit.Events.Where(e => e.EventType != "WebhookSignatureRejected"));
        Assert.Contains(_audit.Events, e => e.EventType == "WebhookSignatureRejected");
    }

    [Fact]
    public async Task UnknownCorrelation_RowPersistedAsRejected_Returns200()
    {
        _provider.NextResult = new NormalizedBillingEvent("Paddle", "evt-1", BillingEventType.PaymentSucceeded, _clock.UtcNow, "does-not-exist", "cust-1", "hash");

        var result = await _processor.ProcessAsync("raw", new Dictionary<string, string>(), CancellationToken.None);

        Assert.Equal(200, result.HttpStatusCode);
        var stored = await _billingEvents.FindByProviderEventIdAsync("Paddle", "evt-1", CancellationToken.None);
        Assert.NotNull(stored);
        Assert.Equal(BillingEventProcessingStatus.Rejected, stored!.ProcessingStatus);
        Assert.Null(stored.SubscriptionId);
    }

    [Fact]
    public async Task ValidApplicableEvent_RowPersistedAsProcessed_SubscriptionUpdated_Returns200()
    {
        var subscription = await SeedSubscriptionAsync();
        _provider.NextResult = new NormalizedBillingEvent("Paddle", "evt-2", BillingEventType.PaymentFailed, _clock.UtcNow, "sub-1", "cust-1", "hash");

        var result = await _processor.ProcessAsync("raw", new Dictionary<string, string>(), CancellationToken.None);

        Assert.Equal(200, result.HttpStatusCode);
        var stored = await _billingEvents.FindByProviderEventIdAsync("Paddle", "evt-2", CancellationToken.None);
        Assert.Equal(BillingEventProcessingStatus.Processed, stored!.ProcessingStatus);
        Assert.Equal(subscription.Id, stored.SubscriptionId);

        var reloaded = await _subscriptions.FindByIdAsync(subscription.Id, CancellationToken.None);
        Assert.Equal(SubscriptionStatus.PastDue, reloaded!.Status);
    }

    [Fact]
    public async Task StaleEvent_RowPersistedAsSuperseded_Returns200()
    {
        var subscription = await SeedSubscriptionAsync();
        _provider.NextResult = new NormalizedBillingEvent("Paddle", "evt-3", BillingEventType.PaymentFailed, _clock.UtcNow, "sub-1", "cust-1", "hash");
        await _processor.ProcessAsync("raw", new Dictionary<string, string>(), CancellationToken.None);

        _provider.NextResult = new NormalizedBillingEvent("Paddle", "evt-4", BillingEventType.PaymentSucceeded, _clock.UtcNow.AddMinutes(-5), "sub-1", "cust-1", "hash");
        var result = await _processor.ProcessAsync("raw", new Dictionary<string, string>(), CancellationToken.None);

        Assert.Equal(200, result.HttpStatusCode);
        var stored = await _billingEvents.FindByProviderEventIdAsync("Paddle", "evt-4", CancellationToken.None);
        Assert.Equal(BillingEventProcessingStatus.Superseded, stored!.ProcessingStatus);
    }

    // ---- Idempotency / duplicate delivery / redelivery ----

    [Fact]
    public async Task DuplicateDelivery_SameProviderEventId_DoesNotReprocess_StillReturns200()
    {
        await SeedSubscriptionAsync();
        _provider.NextResult = new NormalizedBillingEvent("Paddle", "evt-5", BillingEventType.PaymentFailed, _clock.UtcNow, "sub-1", "cust-1", "hash");

        var first = await _processor.ProcessAsync("raw", new Dictionary<string, string>(), CancellationToken.None);
        var second = await _processor.ProcessAsync("raw", new Dictionary<string, string>(), CancellationToken.None);

        Assert.Equal(200, first.HttpStatusCode);
        Assert.Equal(200, second.HttpStatusCode);

        // Exactly one BillingEvent row exists for this (Provider, ProviderEventId) — the
        // second delivery must not create a second row or re-apply the transition.
        var stored = await _billingEvents.FindByProviderEventIdAsync("Paddle", "evt-5", CancellationToken.None);
        Assert.NotNull(stored);
        Assert.Equal(BillingEventProcessingStatus.Processed, stored!.ProcessingStatus);

        var auditCountForThisSubscription = _audit.Events.Count(e => e.EventType == "SubscriptionPastDue");
        Assert.Equal(1, auditCountForThisSubscription); // applied exactly once, not twice
    }

    // ---- Failed path: transient infrastructure failure, NOT conflated with rejection ----

    [Fact]
    public async Task TransientFailureDuringApplication_MarksBillingEventFailed_Returns500_DistinctFromRejectionOrSupersession()
    {
        // Uses a lifecycle-service double that throws (simulating a transient DB/
        // connection failure during step 3's application attempt) — never how a normal
        // content/authenticity/ordering problem is signaled (those are returned as
        // ordinary BillingEventApplicationResult outcomes and never throw).
        var throwingLifecycle = new ThrowingSubscriptionLifecycleService();
        var processor = new BillingWebhookProcessor(_provider, _billingEvents, throwingLifecycle, _unitOfWork, _audit, _clock);
        _provider.NextResult = new NormalizedBillingEvent("Paddle", "evt-failed-1", BillingEventType.PaymentFailed, _clock.UtcNow, "sub-1", "cust-1", "hash");

        var result = await processor.ProcessAsync("raw", new Dictionary<string, string>(), CancellationToken.None);

        Assert.Equal(500, result.HttpStatusCode);
        var stored = await _billingEvents.FindByProviderEventIdAsync("Paddle", "evt-failed-1", CancellationToken.None);
        Assert.NotNull(stored);
        Assert.Equal(BillingEventProcessingStatus.Failed, stored!.ProcessingStatus);
        // Distinct from every content-based terminal outcome — Failed is retryable, those are not.
        Assert.NotEqual(BillingEventProcessingStatus.Rejected, stored.ProcessingStatus);
        Assert.NotEqual(BillingEventProcessingStatus.Superseded, stored.ProcessingStatus);
        Assert.NotEqual(BillingEventProcessingStatus.Processed, stored.ProcessingStatus);
    }

    [Fact]
    public async Task RedeliveryOfTerminalEvent_AcknowledgedWithoutReprocessing()
    {
        await SeedSubscriptionAsync();
        _provider.NextResult = new NormalizedBillingEvent("Paddle", "evt-6", BillingEventType.PaymentFailed, _clock.UtcNow, "sub-1", "cust-1", "hash");
        await _processor.ProcessAsync("raw", new Dictionary<string, string>(), CancellationToken.None);

        // Redeliver the exact same event again.
        var redelivery = await _processor.ProcessAsync("raw", new Dictionary<string, string>(), CancellationToken.None);

        Assert.Equal(200, redelivery.HttpStatusCode);
        var stored = await _billingEvents.FindByProviderEventIdAsync("Paddle", "evt-6", CancellationToken.None);
        Assert.Equal(BillingEventProcessingStatus.Processed, stored!.ProcessingStatus);
    }
}
