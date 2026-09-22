using VTTranslate.Backend.Application.Usage;
using VTTranslate.Backend.Domain.Abstractions;
using VTTranslate.Backend.Domain.Entities;
using VTTranslate.Backend.Domain.Enums;

namespace VTTranslate.Backend.Application.Subscriptions;

/// <summary>
/// Read model for the customer's trial (Phase 25B): the persisted trial period and the trial-lifetime usage allowance.
/// <see cref="UsedSeconds"/> is server-derived usage recorded since the trial start (never reset by a calendar month);
/// <see cref="LimitSeconds"/> is the plan's <c>TrialUsageLimitSeconds</c> (never the paid <c>UsageLimitSecondsPerPeriod</c>).
/// </summary>
public sealed record TrialUsageStatus(
    string Status,
    DateTimeOffset PeriodStart,
    DateTimeOffset PeriodEnd,
    double UsedSeconds,
    double? LimitSeconds,
    double? RemainingSeconds,
    bool Ended,
    bool Exhausted);

public interface ITrialUsageReporter
{
    /// <summary>Returns null when the account has no trial (live Trial subscription, or an Expired trial as its latest subscription).</summary>
    Task<TrialUsageStatus?> GetAsync(Guid accountId, CancellationToken ct);
}

public sealed class TrialUsageReporter(
    ISubscriptionRepository subscriptions,
    IPlanRepository plans,
    IUsageService usage,
    IClock clock) : ITrialUsageReporter
{
    public async Task<TrialUsageStatus?> GetAsync(Guid accountId, CancellationToken ct)
    {
        var subscription = await subscriptions.FindByAccountAsync(accountId, ct);
        if (subscription is null)
        {
            // An ended trial is persisted as Expired (not live); keep reporting it so My Account can say "your trial has ended".
            var history = await subscriptions.FindHistoryByAccountAsync(accountId, ct);
            subscription = history.OrderByDescending(s => s.CurrentPeriodEnd).FirstOrDefault(s => s.Status == SubscriptionStatus.Expired);
            if (subscription is null) return null;
        }

        var entitlements = await plans.GetEntitlementsAsync(subscription.PlanId, ct);
        var isTrial = subscription.Status == SubscriptionStatus.Trial
            || (subscription.Status == SubscriptionStatus.Expired && entitlements.Any(e => e.Key == EntitlementKeys.TrialDurationDays));
        if (!isTrial) return null;

        var limit = double.TryParse(entitlements.FirstOrDefault(e => e.Key == EntitlementKeys.TrialUsageLimitSeconds)?.Value, out var l) && l > 0 ? l : (double?)null;
        var used = await usage.GetAuthoritativeUsageSecondsSinceAsync(accountId, subscription.CurrentPeriodStart, ct);
        var ended = subscription.Status == SubscriptionStatus.Expired || clock.UtcNow > subscription.CurrentPeriodEnd;

        return new TrialUsageStatus(
            subscription.Status.ToString(),
            subscription.CurrentPeriodStart,
            subscription.CurrentPeriodEnd,
            used,
            limit,
            limit is null ? null : Math.Max(0, limit.Value - used),
            ended,
            Exhausted: limit is not null && used >= limit.Value);
    }
}
