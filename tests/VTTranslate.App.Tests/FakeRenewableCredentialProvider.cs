using VTTranslate.Core.Providers;

namespace VTTranslate.App.Tests;

/// <summary>Deterministic <see cref="IRenewableCredentialProvider"/> test double — no real Azure SDK/recognizer dependency.</summary>
public sealed class FakeRenewableCredentialProvider : IRenewableCredentialProvider
{
    public int UpdateCallCount { get; private set; }
    public List<string> AppliedTokens { get; } = new();
    public bool StoppedOrNotStarted { get; set; } // simulates "Stop() already won / no live recognizer"

    public Task<bool> TryUpdateAuthorizationTokenAsync(string newToken, CancellationToken ct)
    {
        UpdateCallCount++;
        if (StoppedOrNotStarted) return Task.FromResult(false);
        AppliedTokens.Add(newToken);
        return Task.FromResult(true);
    }
}
