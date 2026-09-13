namespace VTTranslate.Backend.Domain.Enums;

/// <summary>The subscription lifecycle approved in docs/phase-6-account-subscription-device-architecture.md §4, unchanged through Phase 6.2B.</summary>
public enum SubscriptionStatus
{
    Trial,
    Active,
    GracePeriod,
    Expired,
    Cancelled,
}
