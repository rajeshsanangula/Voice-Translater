using VTTranslate.App.Authentication;

namespace VTTranslate.App.Tests;

/// <summary>Deterministic <see cref="ITokenProvider"/> test double — no MSAL, no browser, no network. Lets tests script exactly the silent/interactive/failure sequence they need.</summary>
public sealed class FakeTokenProvider : ITokenProvider
{
    public int SilentCallCount { get; private set; }
    public int ForceRefreshCallCount { get; private set; }
    public int InteractiveCallCount { get; private set; }
    public bool HasCachedAccount { get; set; } = true;
    public bool ThrowOnNextInteractive { get; set; }

    /// <summary>Queue of tokens to return on successive silent (non-force-refresh) calls; when exhausted, throws AuthenticationRequiredException unless AllowSilentSuccessAfterQueueEmpty.</summary>
    public Queue<string> SilentTokens { get; } = new();
    public Queue<string> ForceRefreshTokens { get; } = new();
    public string InteractiveToken { get; set; } = "interactive-token";
    public bool SilentRequiresInteractive { get; set; }

    public Task<string> GetAccessTokenAsync(bool forceRefresh = false, bool allowInteractive = true, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (forceRefresh)
        {
            ForceRefreshCallCount++;
            if (ForceRefreshTokens.Count > 0) return Task.FromResult(ForceRefreshTokens.Dequeue());
            if (!allowInteractive) throw new AuthenticationRequiredException("force-refresh exhausted, interactive not allowed");
            return AcquireInteractive();
        }

        SilentCallCount++;
        if (!SilentRequiresInteractive && SilentTokens.Count > 0) return Task.FromResult(SilentTokens.Dequeue());

        if (!allowInteractive) throw new AuthenticationRequiredException("silent failed, interactive not allowed");
        return AcquireInteractive();
    }

    private Task<string> AcquireInteractive()
    {
        InteractiveCallCount++;
        if (ThrowOnNextInteractive) throw new AuthenticationRequiredException("interactive cancelled");
        return Task.FromResult(InteractiveToken);
    }

    public Task SignOutAsync(CancellationToken ct = default)
    {
        HasCachedAccount = false;
        return Task.CompletedTask;
    }

    public Task<bool> HasCachedAccountAsync(CancellationToken ct = default) => Task.FromResult(HasCachedAccount);
}
