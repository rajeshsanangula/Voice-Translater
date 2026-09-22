using VTTranslate.Backend.Application.Billing;
using VTTranslate.Backend.Domain.Abstractions;
using VTTranslate.Backend.Domain.Entities;
using VTTranslate.Backend.Domain.Enums;

namespace VTTranslate.Backend.Application.Subscriptions;

public enum TrialProvisioningOutcome
{
    /// <summary>A new Trial subscription was created for the account.</summary>
    Started,

    /// <summary>The account already has a live subscription — returned unchanged (idempotent).</summary>
    AlreadySubscribed,

    /// <summary>The account previously had a subscription that is no longer live; a free trial is never granted twice.</summary>
    TrialAlreadyUsed,

    /// <summary>The operator-configured trial plan is missing or incomplete — fail closed, never grant an unbounded subscription.</summary>
    TrialUnavailable,

    /// <summary>The account does not exist or is not <see cref="AccountStatus.Active"/>.</summary>
    AccountNotEligible,
}

public sealed record TrialProvisioningResult(TrialProvisioningOutcome Outcome, Subscription? Subscription);

public interface ISubscriptionProvisioningService
{
    /// <summary>
    /// Starts the operator-configured free Trial for <paramref name="accountId"/>. The caller supplies ONLY
    /// the (server-derived) account — never a plan, status, quota, period or any billing value.
    /// This is entitlement provisioning only: it performs no payment and never claims one was taken.
    /// </summary>
    Task<TrialProvisioningResult> StartTrialAsync(Guid accountId, CancellationToken ct);
}

/// <summary>
/// Minimum-viable production path for a customer's FIRST subscription (Phase 25).
/// Distinguishes: authentication (already done by the caller), entitlement (this service) and payment
/// (not implemented — <c>NotImplementedBillingProvider</c>; a Trial is free and bounded).
/// The plan is chosen by operator configuration, its limits come from that plan's persisted
/// <see cref="Entitlement"/> rows (never compiled-in constants), and the plan MUST define a bounded trial
/// duration, a bounded usage limit and a device limit — otherwise nothing is granted.
/// </summary>
public sealed class SubscriptionProvisioningService(
    IAccountRepository accounts,
    ISubscriptionRepository subscriptions,
    IPlanRepository plans,
    IAuditEventRepository audit,
    ISubscriptionLifecycleService lifecycle,
    IUnitOfWork unitOfWork,
    IClock clock,
    Guid trialPlanId) : ISubscriptionProvisioningService
{
    public async Task<TrialProvisioningResult> StartTrialAsync(Guid accountId, CancellationToken ct)
    {
        try
        {
            return await unitOfWork.ExecuteInTransactionAsync(innerCt => StartTrialCoreAsync(accountId, innerCt), ct);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("already has a live subscription", StringComparison.Ordinal))
        {
            // Lost a race against a concurrent request for the SAME account (the in-memory repository's
            // duplicate-live-subscription invariant; PostgreSQL is serialized by the account row lock and
            // backstopped by the partial unique index). Resolve the winner instead of surfacing an error.
            var winner = await subscriptions.FindByAccountAsync(accountId, ct);
            if (winner is not null)
                return new TrialProvisioningResult(TrialProvisioningOutcome.AlreadySubscribed, winner);
            throw;
        }
    }

    private async Task<TrialProvisioningResult> StartTrialCoreAsync(Guid accountId, CancellationToken ct)
    {
        // Serialize concurrent provisioning for this account BEFORE reading its subscription state.
        await accounts.LockAccountForUsageAccountingAsync(accountId, ct);

        var account = await accounts.FindByIdAsync(accountId, ct);
        if (account is null || account.Status != AccountStatus.Active)
            return new TrialProvisioningResult(TrialProvisioningOutcome.AccountNotEligible, null);

        var live = await subscriptions.FindByAccountAsync(accountId, ct);
        if (live is not null)
        {
            // Phase 25B: an ended Trial is persisted as Expired first — it is then history, so it can neither be returned as
            // "already subscribed" nor restarted: one free trial per account, ever.
            live = await lifecycle.ReconcileTimeBasedTransitionsAsync(live, clock.UtcNow, ct);
            return live.Status == SubscriptionStatus.Expired
                ? new TrialProvisioningResult(TrialProvisioningOutcome.TrialAlreadyUsed, null)
                : new TrialProvisioningResult(TrialProvisioningOutcome.AlreadySubscribed, live);
        }

        // One free trial per account, ever: any prior (now terminal) subscription blocks a new one.
        var history = await subscriptions.FindHistoryByAccountAsync(accountId, ct);
        if (history.Count > 0)
            return new TrialProvisioningResult(TrialProvisioningOutcome.TrialAlreadyUsed, null);

        var plan = await plans.FindByIdAsync(trialPlanId, ct);
        if (plan is null)
            return new TrialProvisioningResult(TrialProvisioningOutcome.TrialUnavailable, null);

        var entitlements = await plans.GetEntitlementsAsync(trialPlanId, ct);
        var durationDays = ReadPositiveInt(entitlements, EntitlementKeys.TrialDurationDays);
        var usageLimit = ReadPositiveDouble(entitlements, EntitlementKeys.TrialUsageLimitSeconds);
        var maxDevices = ReadPositiveInt(entitlements, EntitlementKeys.MaxActiveDevices);
        if (durationDays is null || usageLimit is null || maxDevices is null)
            return new TrialProvisioningResult(TrialProvisioningOutcome.TrialUnavailable, null);

        var now = clock.UtcNow;
        var subscription = new Subscription
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            PlanId = plan.Id,
            Status = SubscriptionStatus.Trial,
            CurrentPeriodStart = now,
            CurrentPeriodEnd = now.AddDays(durationDays.Value),
            CreatedAt = now,
            UpdatedAt = now,
        };
        await subscriptions.SaveAsync(subscription, ct);

        await audit.AddAsync(new AuditEvent
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            EventType = "SubscriptionTrialStarted",
            Metadata = $"subscriptionId={subscription.Id} planId={plan.Id} durationDays={durationDays.Value}",
            OccurredAt = now,
        }, ct);

        return new TrialProvisioningResult(TrialProvisioningOutcome.Started, subscription);
    }

    private static int? ReadPositiveInt(IReadOnlyList<Entitlement> entitlements, string key) =>
        int.TryParse(entitlements.FirstOrDefault(e => e.Key == key)?.Value, out var v) && v > 0 ? v : null;

    private static double? ReadPositiveDouble(IReadOnlyList<Entitlement> entitlements, string key) =>
        double.TryParse(entitlements.FirstOrDefault(e => e.Key == key)?.Value, out var v) && v > 0 ? v : null;
}
