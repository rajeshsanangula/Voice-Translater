using VTTranslate.Backend.Domain.Abstractions;

namespace VTTranslate.Backend.Infrastructure.Billing;

/// <summary>
/// PLACEHOLDER — proves <see cref="IBillingProvider"/> is a real, dependency-injectable
/// contract; deliberately does NOT call Paddle or any billing provider. Per the Phase
/// 6.3 instruction ("do NOT implement Paddle"), every method throws. A future phase
/// replaces this registration with a real <c>PaddleBillingProvider</c> — nothing else in
/// the codebase should need to change when that happens.
/// </summary>
public sealed class NotImplementedBillingProvider : IBillingProvider
{
    public string ProviderName => "NotImplemented";

    public Task<BillingCustomer?> FindCustomerAsync(Guid accountId, CancellationToken ct) =>
        throw new NotImplementedException("Billing provider integration (Paddle, evaluated but not yet approved for integration) is not implemented in Phase 6.3.");

    public Task<BillingSubscriptionState?> GetSubscriptionStateAsync(string billingProviderSubscriptionId, CancellationToken ct) =>
        throw new NotImplementedException("Billing provider integration (Paddle, evaluated but not yet approved for integration) is not implemented in Phase 6.3.");

    public Task<NormalizedBillingEvent?> TryVerifyAndNormalizeWebhookAsync(string rawPayload, IReadOnlyDictionary<string, string> headers, CancellationToken ct) =>
        throw new NotImplementedException("Billing provider integration (Paddle, evaluated but not yet approved for integration) is not implemented in Phase 6.6 — no real webhook signature verification exists yet.");
}
