namespace VTTranslate.App.Authentication;

/// <summary>
/// Phase 7.1 — the single seam through which every access-token acquisition flows.
/// This is what keeps MSAL types (and the browser interaction inside interactive
/// acquisition) invisible to <see cref="IAutraxisApiClient"/>/ViewModels — unit tests
/// substitute a fake implementation with zero MSAL dependency and zero browser launch.
/// See docs/phase-7.1-customer-authentication-client-and-entra-integration.md §9/§31.
/// </summary>
public interface ITokenProvider
{
    /// <summary>
    /// Returns a valid access token for the AUTRAXIS API scope, acquiring it silently
    /// first (docs §8/§12) and falling back to interactive acquisition (system browser,
    /// PKCE, loopback) only when silent acquisition reports that interactive
    /// authentication is required. Interactive prompts are serialized per application
    /// instance (docs §15) — a concurrent caller awaits the in-flight prompt rather
    /// than triggering a second one. Throws <see cref="AuthenticationRequiredException"/>
    /// if interactive authentication is required but the caller opted out
    /// (<paramref name="allowInteractive"/> = false) or the user cancels/fails it.
    /// </summary>
    /// <param name="forceRefresh">True to bypass a cached-but-not-yet-expired token (used by the bounded one-renewal-on-401 policy, docs §13) — still tries silently first, never launches an interactive prompt merely because a refresh was forced.</param>
    /// <param name="allowInteractive">False to fail closed with <see cref="AuthenticationRequiredException"/> instead of launching a browser — used by background/automatic call sites (e.g. a heartbeat) that must never surprise the user with an unexpected browser window.</param>
    Task<string> GetAccessTokenAsync(bool forceRefresh = false, bool allowInteractive = true, CancellationToken ct = default);

    /// <summary>Removes the current account's cached tokens (docs §20/§27) — a targeted removal, never a blunt cache-file delete, so other cached accounts on the same machine are unaffected.</summary>
    Task SignOutAsync(CancellationToken ct = default);

    /// <summary>True if a cached account exists that MAY still be able to silently acquire a token — does not itself acquire one, so it never launches a browser and never throws.</summary>
    Task<bool> HasCachedAccountAsync(CancellationToken ct = default);
}

/// <summary>Thrown by <see cref="ITokenProvider.GetAccessTokenAsync"/> when a valid access token cannot be produced without interactive authentication the caller did not permit, or when the user cancelled/failed an interactive attempt. Never carries token/claim content — see docs §25.</summary>
public sealed class AuthenticationRequiredException(string reason) : Exception(reason);
