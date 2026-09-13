namespace VTTranslate.App.Authentication;

/// <summary>Phase 7.1 — thin state-machine wrapper around <see cref="ITokenProvider"/> for ViewModel consumption. Never references MSAL types directly.</summary>
public sealed class AuthenticationService(ITokenProvider tokenProvider) : IAuthenticationService
{
    private AuthenticationState _state = AuthenticationState.SignedOut;

    public AuthenticationState State
    {
        get => _state;
        private set
        {
            if (_state == value) return;
            _state = value;
            StateChanged?.Invoke(this, value);
        }
    }

    public event EventHandler<AuthenticationState>? StateChanged;

    public async Task SignInAsync(CancellationToken ct = default)
    {
        if (State == AuthenticationState.SignedIn) return;

        State = AuthenticationState.Authenticating;
        try
        {
            await tokenProvider.GetAccessTokenAsync(forceRefresh: false, allowInteractive: true, ct);
            State = AuthenticationState.SignedIn;
        }
        catch (AuthenticationRequiredException)
        {
            State = AuthenticationState.AuthenticationFailed;
            throw;
        }
        catch (OperationCanceledException)
        {
            State = AuthenticationState.SignedOut;
            throw;
        }
    }

    public async Task TrySilentSignInAsync(CancellationToken ct = default)
    {
        // Startup rule (docs §21/§26): never launch a browser without explicit user
        // action — allowInteractive: false guarantees this call cannot pop a window.
        try
        {
            if (!await tokenProvider.HasCachedAccountAsync(ct))
            {
                State = AuthenticationState.SignedOut;
                return;
            }

            await tokenProvider.GetAccessTokenAsync(forceRefresh: false, allowInteractive: false, ct);
            State = AuthenticationState.SignedIn;
        }
        catch (AuthenticationRequiredException)
        {
            State = AuthenticationState.SignedOut;
        }
    }

    public async Task SignOutAsync(CancellationToken ct = default)
    {
        await tokenProvider.SignOutAsync(ct);
        State = AuthenticationState.SignedOut;
    }
}
