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
            return EntitlementDecision.Deny("no subscription found for this account");

        var entitlements = await plans.GetEntitlementsAsync(subscription.PlanId, ct);

        // 3. Subscription status must permit translation. Expired/Cancelled never do.
        switch (subscription.Status)
        {
            case SubscriptionStatus.Expired:
                return EntitlementDecision.Deny("subscription is expired");
            case SubscriptionStatus.Cancelled:
                return EntitlementDecision.Deny("subscription is cancelled");

            case SubscriptionStatus.GracePeriod:
                // Grace period must NEVER become an indefinite offline/billing bypass
                // (Phase 6.2B §12) — bounded strictly by GracePeriodDays past the
                // subscription's own period end. If that entitlement is missing or the
                // bound has passed, deny — fail closed, never open.
                var graceDays = ReadInt(entitlements, EntitlementKeys.GracePeriodDays);
                if (graceDays is null || clock.UtcNow > subscription.CurrentPeriodEnd.AddDays(graceDays.Value))
                    return EntitlementDecision.Deny("grace period has elapsed");
                break;

            case SubscriptionStatus.Trial:
            case SubscriptionStatus.Active:
                if (clock.UtcNow > subscription.CurrentPeriodEnd)
                    return EntitlementDecision.Deny("subscription period has ended");
                break;
        }

        // 4. Usage-against-limit check — server-authoritative only (Phase 6.2B §7/§6).
        var limitKey = subscription.Status == SubscriptionStatus.Trial
            ? EntitlementKeys.TrialUsageLimitSeconds
            : EntitlementKeys.UsageLimitSecondsPerPeriod;
        var limitSeconds = ReadDouble(entitlements, limitKey);
        if (limitSeconds is not null)
        {
            var periodBucket = clock.UtcNow.ToString("yyyy-MM");
            var usedSeconds = await usage.GetAuthoritativeUsageSecondsAsync(accountId, periodBucket, ct);
            if (usedSeconds >= limitSeconds.Value)
                return EntitlementDecision.Deny("usage limit for this period has been reached");
        }

        return EntitlementDecision.Allow("ok");
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
