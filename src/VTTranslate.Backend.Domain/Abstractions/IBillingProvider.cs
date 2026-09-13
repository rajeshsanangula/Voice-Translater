using VTTranslate.Backend.Domain.Enums;

namespace VTTranslate.Backend.Domain.Abstractions;

/// <summary>
/// The internal billing boundary approved in
/// docs/phase-6.2b-resolved-architecture-decisions.md §9. Paddle (Merchant of Record) is
/// the evaluated/recommended first implementation, but per the Phase 6.3 instruction it
/// MUST NOT be integrated in this phase — this interface exists purely to define the
/// shape so <c>Subscription</c>/<c>Plan</c> and the rest of the domain never depend on
/// Paddle-specific concepts (their terminology, their webhook event names, their
/// tax-handling fields). A future <c>PaddleBillingProvider</c> implementation is the
/// only thing that changes when billing is actually integrated.
///
/// NO IMPLEMENTATION IN THIS PHASE CALLS ANY REAL BILLING PROVIDER. The only
/// implementation present
/// (<c>VTTranslate.Backend.Infrastructure.Billing.NotImplementedBillingProvider</c>)
/// deliberately throws <see cref="NotImplementedException"/>.
/// </summary>
public interface IBillingProvider
{
    /// <summary>A short, stable name for this implementation (e.g. "Paddle") — diagnostics/audit only, never branched on outside this boundary.</summary>
    string ProviderName { get; }

    /// <summary>Looks up the billing provider's own customer record for an AUTRAXIS account, if one exists. Returns null if the account has never had a billing-provider interaction (e.g., still on a Trial plan with no billing provider involved at all).</summary>
    Task<BillingCustomer?> FindCustomerAsync(Guid accountId, CancellationToken ct);

    /// <summary>Looks up the current lifecycle state of a subscription at the billing provider, by the OPAQUE <c>Subscription.BillingProviderSubscriptionId</c> handle (never a provider-specific type leaking into the domain).</summary>
    Task<BillingSubscriptionState?> GetSubscriptionStateAsync(string billingProviderSubscriptionId, CancellationToken ct);

    /// <summary>
    /// Phase 6.6 — the ONLY entry point through which raw webhook bytes are ever touched.
    /// Verifies the payload's authenticity (provider-specific signature scheme, entirely
    /// inside the implementation — never in Application/Domain) and, only if genuine,
    /// normalizes it to a vendor-neutral <see cref="NormalizedBillingEvent"/>. Returns
    /// null on ANY authenticity failure — fail closed, no partial trust, no exception
    /// carrying provider internals. Does NOT resolve an AUTRAXIS Account/Subscription
    /// (that correlation happens afterward, in Application, against already-verified data).
    /// </summary>
    Task<NormalizedBillingEvent?> TryVerifyAndNormalizeWebhookAsync(string rawPayload, IReadOnlyDictionary<string, string> headers, CancellationToken ct);
}

/// <summary>
/// Phase 6.6 — the vendor-neutral shape of an authenticated (signature-verified) billing
/// webhook event. <see cref="EventType"/> is one of a small, closed set this system
/// recognizes (see <c>BillingEventType</c>) — an unrecognized provider event type never
/// reaches this record; the provider adapter maps it here or the verification call
/// returns null/an unmapped marker, handled explicitly by the webhook processor.
/// </summary>
public sealed record NormalizedBillingEvent(
    string Provider,
    string ProviderEventId,
    BillingEventType EventType,
    DateTimeOffset OccurredAt,
    string? BillingProviderSubscriptionId,
    string? BillingProviderCustomerId,
    string RawPayloadHash);

/// <summary>
/// Phase 6.6 — the fixed, closed set of billing event types this system's core
/// (<c>ISubscriptionLifecycleService</c>) understands, each with its own precedence rank
/// for the deterministic equal-timestamp ordering algorithm. Adding a new provider event
/// type means mapping it to one of these (or adding a new member here WITH an explicit
/// precedence assignment) — there is no default/implicit rank.
/// </summary>
public enum BillingEventType
{
    SubscriptionCreated,
    TrialConverted,
    PaymentSucceeded,
    PaymentFailed,
    CancellationRequested,
    Refunded,
    ChargebackReceived,
}

/// <summary>Vendor-neutral customer reference — never a Paddle-specific customer object.</summary>
public sealed record BillingCustomer(string BillingProviderCustomerId, string? Email);

/// <summary>
/// Vendor-neutral subscription state as reported by the billing provider. Deliberately
/// mirrors <see cref="Enums.SubscriptionStatus"/>'s shape so the application layer can
/// reconcile the two without needing to understand any specific provider's own status
/// vocabulary.
/// </summary>
public sealed record BillingSubscriptionState(
    SubscriptionStatus Status,
    DateTimeOffset CurrentPeriodStart,
    DateTimeOffset CurrentPeriodEnd);
