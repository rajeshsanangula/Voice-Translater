namespace VTTranslate.Backend.Domain.Enums;

/// <summary>The subscription lifecycle approved in docs/phase-6-account-subscription-device-architecture.md §4, unchanged through Phase 6.2B.</summary>
public enum SubscriptionStatus
{
    Trial,
    Active,
    GracePeriod,
    Expired,
    Cancelled,

    /// <summary>Phase 6.6: a payment attempt has failed but access hasn't yet been cut — see docs/phase-6.6-billing-subscription.md for the exact bounded-access rule and its relationship to <see cref="GracePeriod"/>.</summary>
    PastDue,

    /// <summary>Phase 6.6: terminal — a refund or chargeback was received. Distinct from <see cref="Cancelled"/> (customer/provider-initiated) since it implies a specific reversal with its own audit trail.</summary>
    Refunded,
}
