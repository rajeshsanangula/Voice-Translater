namespace VTTranslate.App.Providers;

/// <summary>
/// Phase 7.2 — proactively renews a per-direction short-lived provider credential
/// (Phase 6.8, unchanged) before it expires, applying it live to a running provider
/// via <see cref="VTTranslate.Core.Providers.IRenewableCredentialProvider"/>. One
/// instance per <see cref="VTTranslate.Core.Session.DirectionPipeline"/> — never
/// shared mutable state between the two translation directions
/// (docs/phase-7.2-long-running-translation-session-continuity.md §14/§16).
///
/// A renewal is NEVER a new AUTRAXIS translation session, a new device registration,
/// or a new usage event — it calls only <c>POST /provider-access</c> (Phase 6.8,
/// unchanged), exactly as the original grant already does, re-running the full
/// authorization chain every time.
/// </summary>
public interface IProviderCredentialRenewalCoordinator : IAsyncDisposable
{
    /// <summary>
    /// Begins the proactive renewal loop for a credential already in use, given its
    /// real <c>ExpiresAt</c>. <paramref name="sessionCt"/> is the same cancellation
    /// token the owning session/pipeline uses — cancelling it (including via an
    /// explicit Stop()) immediately and unconditionally stops all future renewal
    /// activity; a renewal already in flight discards its own result rather than
    /// applying it (docs §13/§14/§18).
    /// </summary>
    void Start(DateTimeOffset initialExpiresAt, CancellationToken sessionCt);
}
