namespace VTTranslate.App.Authentication;

/// <summary>Phase 7.1 — the ViewModel-facing authentication surface. Never exposes MSAL types; ViewModels depend only on this and <see cref="IAutraxisApiClient"/>, mirroring how <c>MainViewModel</c> already depends on <c>ISpeechTranslationProvider</c> rather than an SDK type directly.</summary>
public interface IAuthenticationService
{
    /// <summary>Current authentication state — updated only by this service, observed by ViewModels via <see cref="StateChanged"/>.</summary>
    AuthenticationState State { get; }

    event EventHandler<AuthenticationState>? StateChanged;

    /// <summary>Interactive sign-in (system browser, Authorization Code + PKCE). Safe to call when already signed in — resolves immediately. Cancellation-aware; never blocks the calling thread synchronously.</summary>
    Task SignInAsync(CancellationToken ct = default);

    /// <summary>Attempts a silent sign-in against any cached account, WITHOUT ever launching a browser (docs §21/§26 startup behavior) — resolves to <see cref="AuthenticationState.SignedOut"/> rather than throwing if no cached account can be silently renewed.</summary>
    Task TrySilentSignInAsync(CancellationToken ct = default);

    /// <summary>Local logout only (docs §20/§27) — clears the cached account/token state for the current user; does NOT revoke a device, does NOT mutate a subscription, and does NOT necessarily end every Entra browser session.</summary>
    Task SignOutAsync(CancellationToken ct = default);
}

public enum AuthenticationState
{
    SignedOut,
    Authenticating,
    SignedIn,
    AuthenticationFailed,
}
