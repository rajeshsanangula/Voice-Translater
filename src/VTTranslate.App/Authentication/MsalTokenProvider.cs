using Microsoft.Identity.Client;

namespace VTTranslate.App.Authentication;

/// <summary>
/// Phase 7.1 — the only class in this application that references
/// <c>Microsoft.Identity.Client</c> types directly. Implements Authorization Code +
/// PKCE via MSAL.NET's own <c>AcquireTokenInteractive</c> (system browser, MSAL's
/// built-in loopback redirect listener — no custom URI scheme, no embedded browser)
/// with <c>AcquireTokenSilent</c> attempted first on every call (docs §8/§12).
///
/// Interactive-prompt serialization (docs §15, §22): a <see cref="SemaphoreSlim"/>
/// ensures at most one interactive browser prompt is ever in flight for this process;
/// a concurrent caller awaits the SAME in-flight attempt's result rather than
/// launching a second browser window. Cancellation-aware throughout; never blocks a
/// calling thread synchronously (no <c>.Result</c>/<c>.Wait()</c> anywhere in this
/// class).
/// </summary>
public sealed class MsalTokenProvider : ITokenProvider
{
    private readonly IPublicClientApplication _app;
    private readonly string[] _scopes;
    private readonly SemaphoreSlim _interactiveGate = new(1, 1);
    private IAccount? _lastUsedAccount;

    private MsalTokenProvider(IPublicClientApplication app, string[] scopes)
    {
        _app = app;
        _scopes = scopes;
    }

    public static async Task<MsalTokenProvider> CreateAsync(AuthenticationOptions options, ITokenCacheStore cacheStore, CancellationToken ct = default)
    {
        if (!options.IsConfigured)
            throw new InvalidOperationException("Customer authentication is not configured (Authority/ClientId/ApiScope missing). Refusing to start unauthenticated — see AuthenticationOptions.");

        var app = PublicClientApplicationBuilder
            .Create(options.ClientId)
            .WithAuthority(options.Authority)
            // MSAL's own default loopback redirect — no custom URI scheme, no
            // registry/manifest dependency (docs §10/§16).
            .WithDefaultRedirectUri()
            .Build();

        await cacheStore.ConfigureAsync(app, ct);

        return new MsalTokenProvider(app, [options.ApiScope]);
    }

    public async Task<string> GetAccessTokenAsync(bool forceRefresh = false, bool allowInteractive = true, CancellationToken ct = default)
    {
        var account = _lastUsedAccount ?? (await _app.GetAccountsAsync()).FirstOrDefault();

        try
        {
            var silentBuilder = _app.AcquireTokenSilent(_scopes, account);
            if (forceRefresh) silentBuilder = silentBuilder.WithForceRefresh(true);

            var result = await silentBuilder.ExecuteAsync(ct);
            _lastUsedAccount = result.Account;
            return result.AccessToken;
        }
        catch (MsalUiRequiredException)
        {
            if (!allowInteractive)
                throw new AuthenticationRequiredException("Interactive authentication is required but was not permitted for this call.");

            return await AcquireInteractiveAsync(ct);
        }
    }

    private async Task<string> AcquireInteractiveAsync(CancellationToken ct)
    {
        // Serialize interactive prompts (docs §15/§22): a second concurrent caller
        // waits for the first attempt's result instead of triggering a second browser
        // window. No deadlock risk — the semaphore is always released in `finally`,
        // and cancellation of a WAITING caller is honored via WaitAsync's own
        // CancellationToken support without blocking the UI thread.
        await _interactiveGate.WaitAsync(ct);
        try
        {
            // Re-check silently once more now that we hold the gate — another caller
            // may have just completed an interactive acquisition while we waited.
            var account = _lastUsedAccount ?? (await _app.GetAccountsAsync()).FirstOrDefault();
            try
            {
                var result = await _app.AcquireTokenSilent(_scopes, account).ExecuteAsync(ct);
                _lastUsedAccount = result.Account;
                return result.AccessToken;
            }
            catch (MsalUiRequiredException)
            {
                // Falls through to interactive below.
            }

            try
            {
                var result = await _app.AcquireTokenInteractive(_scopes).ExecuteAsync(ct);
                _lastUsedAccount = result.Account;
                return result.AccessToken;
            }
            catch (MsalClientException) when (!ct.IsCancellationRequested)
            {
                // Covers user-cancelled/browser-closed and similar client-side
                // interactive-flow failures — mapped to the stable application error
                // category rather than leaking raw MSAL exception text (docs §29/§37).
                throw new AuthenticationRequiredException("Interactive authentication was cancelled or could not complete.");
            }
        }
        finally
        {
            _interactiveGate.Release();
        }
    }

    public async Task SignOutAsync(CancellationToken ct = default)
    {
        var account = _lastUsedAccount ?? (await _app.GetAccountsAsync()).FirstOrDefault();
        if (account is not null)
            await _app.RemoveAsync(account); // targeted removal — never a blunt cache-file delete (docs §20)
        _lastUsedAccount = null;
    }

    public async Task<bool> HasCachedAccountAsync(CancellationToken ct = default) =>
        (await _app.GetAccountsAsync()).Any();

    public async Task<string?> GetAccountKeyAsync(CancellationToken ct = default)
    {
        var account = _lastUsedAccount ?? (await _app.GetAccountsAsync()).FirstOrDefault();
        // HomeAccountId.Identifier is MSAL's own stable per-Entra-identity key — the
        // same account always yields the same value across process restarts, and two
        // different accounts never collide. Never the AUTRAXIS AccountId (unknown to
        // this class), never sent to the API — local keying only.
        return account?.HomeAccountId?.Identifier;
    }
}
