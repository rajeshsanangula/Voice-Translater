using VTTranslate.Backend.Domain.Enums;

namespace VTTranslate.Backend.Domain.Entities;

/// <summary>One account's subscription to a plan — see Phase 6.1 §4/§8, unchanged through Phase 6.2B. <see cref="BillingProviderSubscriptionId"/> is an OPAQUE handle so the billing provider (Paddle or otherwise) is swappable without a schema change (Phase 6.2B §9).</summary>
public sealed class Subscription
{
    public required Guid Id { get; init; }
    public required Guid AccountId { get; init; }
    public required Guid PlanId { get; set; }
    public required SubscriptionStatus Status { get; set; }

    public DateTimeOffset CurrentPeriodStart { get; set; }
    public DateTimeOffset CurrentPeriodEnd { get; set; }

    /// <summary>Opaque reference into whichever <see cref="Abstractions.IBillingProvider"/> is configured. Null for Trial subscriptions (no billing provider involved yet).</summary>
    public string? BillingProviderSubscriptionId { get; set; }

    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; set; }
}
