using Microsoft.Identity.Client;

namespace VTTranslate.App.Authentication;

/// <summary>
/// Phase 7.1 — thin seam around the DPAPI-backed MSAL cache-persistence wiring
/// (<c>Microsoft.Identity.Client.Extensions.Msal</c>'s <c>MsalCacheHelper</c>), so
/// <see cref="MsalTokenProvider"/> does not need to know the storage mechanism
/// directly, and so tests can supply a no-op implementation without touching a real
/// file on disk. See docs §9/§11.
/// </summary>
public interface ITokenCacheStore
{
    /// <summary>Attaches this store's persistence to the given public client application's user token cache. Never stores anything but MSAL's own serialized cache — no provider secret, no AUTRAXIS-issued credential of any kind ever passes through this seam.</summary>
    Task ConfigureAsync(IPublicClientApplication app, CancellationToken ct = default);
}
