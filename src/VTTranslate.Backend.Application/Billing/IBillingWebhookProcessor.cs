namespace VTTranslate.Backend.Application.Billing;

/// <summary>
/// Phase 6.6 — orchestrates: signature verification (Infrastructure, via
/// IBillingProvider) -> BillingEvent idempotency/persistence lifecycle -> dispatch to
/// ISubscriptionLifecycleService. See docs/phase-6.6-billing-subscription.md §7/§8 for
/// the exact transaction/trust-boundary rules this implements.
/// </summary>
public interface IBillingWebhookProcessor
{
    Task<WebhookProcessingResult> ProcessAsync(string rawPayload, IReadOnlyDictionary<string, string> headers, CancellationToken ct);
}

/// <summary>The only thing an HTTP endpoint needs to translate this into a response — never leaks internal processing detail.</summary>
public sealed record WebhookProcessingResult(int HttpStatusCode);
