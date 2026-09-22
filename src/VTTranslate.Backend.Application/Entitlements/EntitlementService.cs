using VTTranslate.Backend.Application.Devices;
using VTTranslate.Backend.Application.Usage;
using VTTranslate.Backend.Domain.Abstractions;
using VTTranslate.Backend.Domain.Entities;
using VTTranslate.Backend.Domain.Enums;

namespace VTTranslate.Backend.Application.Entitlements;

/// <summary>
/// Implements the decision logic described in <see cref="IEntitlementService"/>. Every
/// numeric threshold is read from the account's <see cref="Plan"/>'s
/// <see cref="Entitlement"/> rows — never a compiled constant — per the Phase 6.3
/// instruction ("do not hard-code pricing/trial duration/usage limits").
/// </summary>
public sealed class EntitlementService(
    ISubscriptionRepository subscriptions,
    IPlanRepository plans,
    IDeviceRegistrationService devices,
    IUsageService usage,
    IClock clock) : IEntitlementService
{
    public async Task<EntitlementDecision> CanStartTranslationSessionAsync(Guid accountId, Guid deviceId, CancellationToken ct)
    {
        // 1. Device must be authorized for this account (Phase 6.1 §5's device check).
        if (!await devices.IsDeviceAuthorizedAsync(accountId, deviceId, ct))
            return EntitlementDecision.Deny("device is not authorized for this account");

        // 2. A subscription must exist.
        var subscription = await subscriptions.FindByAccountAsync(accountId, ct);
        if (subscription is null)
            return await DenyNoLiveSubscriptionAsync(accountId, ct);

        var entitlements = await plans.GetEntitlementsAsync(subscription.PlanId, ct);

        // 3. Subscription status must permit translation. Every status is handled
        // explicitly, with an unconditional fail-closed default (Phase 6.6) — an
        // unrecognized/unhandled status must never silently fall through to Allow.
        switch (subscription.Status)
        {
            case SubscriptionStatus.Expired:
                return EntitlementDecision.Deny("subscription is expired");
            case SubscriptionStatus.Cancelled:
                return EntitlementDecision.Deny("subscription is cancelled");
            case SubscriptionStatus.Refunded:
                return EntitlementDecision.Deny("subscription was refunded");

            case SubscriptionStatus.GracePeriod:
                // Grace period must NEVER become an indefinite offline/billing bypass
                // (Phase 6.2B §12) — bounded strictly by GracePeriodDays past the
                // subscription's own period end. If that entitlement is missing or the
                // bound has passed, deny — fail closed, never open.
                var graceDays = ReadInt(entitlements, EntitlementKeys.GracePeriodDays);
                if (graceDays is null || clock.UtcNow > subscription.CurrentPeriodEnd.AddDays(graceDays.Value))
                    return EntitlementDecision.Deny("grace period has elapsed");
                break;

            case SubscriptionStatus.PastDue:
                // Phase 6.6 approved product decision: PastDue grants BOUNDED access only
                // (Option A) — never unlimited access while payment is failing. Mirrors
                // GracePeriod's own pattern exactly, using a distinct, independently
                // configurable entitlement key (see EntitlementKeys.PastDueGraceDays's own
                // doc comment for how the two bounds compose).
                var pastDueDays = ReadInt(entitlements, EntitlementKeys.PastDueGraceDays);
                if (pastDueDays is null || clock.UtcNow > subscription.CurrentPeriodEnd.AddDays(pastDueDays.Value))
                    return EntitlementDecision.Deny("past-due grace period has elapsed");
                break;

            case SubscriptionStatus.Trial:
                // Phase 25B: an unpaid trial whose period has passed is unusable, regardless of whether
                // time-based reconciliation has persisted the Expired transition yet.
                if (clock.UtcNow > subscription.CurrentPeriodEnd)
                    return EntitlementDecision.Deny("trial period has ended", TrialEndedCode);
                break;

            case SubscriptionStatus.Active:
                if (clock.UtcNow > subscription.CurrentPeriodEnd)
                    return EntitlementDecision.Deny("subscription period has ended");
                break;

            default:
                // Unconditional correctness rule (Phase 6.6): an unrecognized status must
                // never be treated as implicitly allowed. Fail closed.
                return EntitlementDecision.Deny("unrecognized subscription status");
        }

        // 4. Usage-against-limit check — server-authoritative only (Phase 6.2B §7/§6).
        if (subscription.Status == SubscriptionStatus.Trial)
        {
            // Phase 25B: the trial gets ONE allowance for the whole trial period — server-derived usage recorded since the
            // persisted trial start, summed across month buckets (never reset by the calendar month). A trial plan without a
            // positive limit fails closed: an unbounded trial is never granted. This check runs only when a session STARTS;
            // a running session is never cut off, so one session can overshoot the allowance (usage is recorded when it ends).
            var trialLimit = ReadDouble(entitlements, EntitlementKeys.TrialUsageLimitSeconds);
            if (trialLimit is null or <= 0)
                return EntitlementDecision.Deny("trial usage limit is not configured", "trial_not_configured");

            var trialUsed = await usage.GetAuthoritativeUsageSecondsSinceAsync(accountId, subscription.CurrentPeriodStart, ct);
            if (trialUsed >= trialLimit.Value)
                return EntitlementDecision.Deny("usage limit for this trial has been reached", TrialExhaustedCode);

            return EntitlementDecision.Allow("ok");
        }

        var limitSeconds = ReadDouble(entitlements, EntitlementKeys.UsageLimitSecondsPerPeriod);
        if (limitSeconds is not null)
        {
            var periodBucket = clock.UtcNow.ToString("yyyy-MM");
            var usedSeconds = await usage.GetAuthoritativeUsageSecondsAsync(accountId, periodBucket, ct);
            if (usedSeconds >= limitSeconds.Value)
                return EntitlementDecision.Deny("usage limit for this period has been reached");
        }

        return EntitlementDecision.Allow("ok");
    }

    /// <summary>Machine-readable denial codes the client maps to an upgrade message (Phase 25B). No payment is implied.</summary>
    public const string TrialEndedCode = "trial_expired";
    public const string TrialExhaustedCode = "trial_usage_exhausted";

    private async Task<EntitlementDecision> DenyNoLiveSubscriptionAsync(Guid accountId, CancellationToken ct)
    {
        var history = await subscriptions.FindHistoryByAccountAsync(accountId, ct);
        var latest = history.OrderByDescending(s => s.CurrentPeriodEnd).FirstOrDefault();
        if (latest is { Status: SubscriptionStatus.Expired })
        {
            // Only an ended TRIAL gets the machine-readable upgrade reason; any other expired subscription keeps the
            // pre-existing "no subscription found" denial unchanged.
            var planEntitlements = await plans.GetEntitlementsAsync(latest.PlanId, ct);
            if (planEntitlements.Any(e => e.Key == EntitlementKeys.TrialDurationDays))
                return EntitlementDecision.Deny("trial has ended", TrialEndedCode);
        }

        return EntitlementDecision.Deny("no subscription found for this account");
    }

    private static int? ReadInt(IReadOnlyList<Entitlement> entitlements, string key)
    {
        var value = entitlements.FirstOrDefault(e => e.Key == key)?.Value;
        return value is not null && int.TryParse(value, out var parsed) ? parsed : null;
    }

    private static double? ReadDouble(IReadOnlyList<Entitlement> entitlements, string key)
    {
        var value = entitlements.FirstOrDefault(e => e.Key == key)?.Value;
        return value is not null && double.TryParse(value, out var parsed) ? parsed : null;
    }
}
