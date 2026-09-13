using System.IO;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;

namespace VTTranslate.App.Authentication;

/// <summary>
/// Phase 7.1 — the production <see cref="ITokenCacheStore"/>. Uses
/// <c>Microsoft.Identity.Client.Extensions.Msal</c>'s Microsoft-maintained
/// <see cref="MsalCacheHelper"/>, which protects the on-disk cache file with Windows
/// DPAPI (<c>System.Security.Cryptography.ProtectedData</c>,
/// <c>DataProtectionScope.CurrentUser</c>) on Windows — no custom cryptography is
/// implemented here (docs §9: "do not invent custom cryptography when MSAL's supported
/// cache helper is appropriate").
///
/// What is stored: the serialized MSAL token cache (access/refresh/ID tokens and
/// minimal account metadata) for every account signed in on this Windows user profile
/// and not since explicitly signed out. What is encrypted: the entire cache file, via
/// DPAPI, scoped to the current Windows user — a different Windows user's own DPAPI
/// key material cannot decrypt this file (docs §9's multi-user-machine guarantee).
/// Never stores a provider master key, a provider bearer credential, or any AUTRAXIS
/// backend secret — those are never Entra tokens and never touch this cache.
/// </summary>
public sealed class DpapiTokenCacheStore(string cacheFileName = "autraxis_msal_cache.bin") : ITokenCacheStore
{
    public async Task ConfigureAsync(IPublicClientApplication app, CancellationToken ct = default)
    {
        var cacheDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AUTRAXIS", "VoiceTranslater", "TokenCache");

        var storageProperties = new StorageCreationPropertiesBuilder(cacheFileName, cacheDirectory)
            .Build();

        // MsalCacheHelper applies DPAPI (CurrentUser scope) automatically on Windows —
        // no explicit ProtectedData call is made here; this IS the "OS-backed storage,
        // no custom cryptography" outcome the architecture requires.
        var cacheHelper = await MsalCacheHelper.CreateAsync(storageProperties);
        cacheHelper.RegisterCache(app.UserTokenCache);
    }
}
