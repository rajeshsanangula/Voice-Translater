using VTTranslate.Core.Providers;

namespace VTTranslate.Core.Tests;

/// <summary>
/// Phase 7.2 — deterministic, no-real-Azure-network tests for
/// <see cref="AzureSpeechTranslationProvider.TryUpdateAuthorizationTokenAsync"/>'s
/// guard behavior. These test only the pre-recognizer-creation and instance-typing
/// paths (never a live Azure connection) — the "same recognizer continues after live
/// token replacement" claim itself is validated separately against real Azure
/// (docs/phase-7.2-long-running-translation-session-continuity.md, "REAL AZURE
/// VALIDATION" addendum) and is not repeated here as a mock.
/// </summary>
public class AzureSpeechTranslationProviderRenewalTests
{
    [Fact]
    public void ImplementsIRenewableCredentialProvider()
    {
        var provider = AzureSpeechTranslationProvider.FromAuthorizationToken("fake-token", "eastus", "en-US-JennyNeural");
        Assert.IsAssignableFrom<IRenewableCredentialProvider>(provider);
    }

    [Fact]
    public async Task TryUpdateAuthorizationTokenAsync_BeforeStartAsync_ReturnsFalse_NeverThrows()
    {
        // No recognizer has ever been created (StartAsync was never called) — this
        // must be treated identically to "Stop() already won": nothing live to
        // update, not an error.
        var provider = AzureSpeechTranslationProvider.FromAuthorizationToken("fake-token", "eastus", "en-US-JennyNeural");

        var applied = await provider.TryUpdateAuthorizationTokenAsync("new-token", CancellationToken.None);

        Assert.False(applied);
    }

    [Fact]
    public async Task TryUpdateAuthorizationTokenAsync_RespectsCancellation()
    {
        var provider = AzureSpeechTranslationProvider.FromAuthorizationToken("fake-token", "eastus", "en-US-JennyNeural");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provider.TryUpdateAuthorizationTokenAsync("new-token", cts.Token));
    }

    [Fact]
    public async Task TryUpdateAuthorizationTokenAsync_AfterStopAsync_ReturnsFalse_NeverThrows()
    {
        // StopAsync() is safe to call even on a never-started provider (it only
        // acquires the lock and no-ops if there is no recognizer) — this exercises
        // the exact "Stop() already won" guard the live-renewal path relies on
        // (docs §13/§14), without requiring a real Azure connection to have been
        // established first.
        var provider = AzureSpeechTranslationProvider.FromAuthorizationToken("fake-token", "eastus", "en-US-JennyNeural");
        await provider.StopAsync();

        var applied = await provider.TryUpdateAuthorizationTokenAsync("new-token", CancellationToken.None);

        Assert.False(applied);
    }
}
