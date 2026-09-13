using VTTranslate.Backend.Infrastructure.Billing;

namespace VTTranslate.Backend.Tests;

/// <summary>
/// Proves the billing PLACEHOLDER implementation never fakes success — per the Phase
/// 6.3/6.4 instruction ("do NOT implement Paddle"), every operation must throw
/// <see cref="NotImplementedException"/>, never return a fabricated customer/subscription
/// state. (The identity-provider placeholder this file tested in Phase 6.3,
/// <c>NotImplementedIdentityProvider</c>, was superseded and removed in Phase 6.4 by the
/// real <c>EntraIdentityProvider</c> — see <c>EntraIdentityProviderTests</c>.)
/// </summary>
public class ProviderPlaceholderTests
{
    [Fact]
    public async Task BillingProvider_FindCustomer_ThrowsNotImplemented_NeverFakesACustomer()
    {
        var provider = new NotImplementedBillingProvider();
        await Assert.ThrowsAsync<NotImplementedException>(() =>
            provider.FindCustomerAsync(Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task BillingProvider_GetSubscriptionState_ThrowsNotImplemented_NeverFakesAStatus()
    {
        var provider = new NotImplementedBillingProvider();
        await Assert.ThrowsAsync<NotImplementedException>(() =>
            provider.GetSubscriptionStateAsync("bp-sub-123", CancellationToken.None));
    }

    [Fact]
    public void BillingProvider_NeverReturnsARealisticProviderName()
    {
        var provider = new NotImplementedBillingProvider();
        Assert.Equal("NotImplemented", provider.ProviderName);
    }
}
