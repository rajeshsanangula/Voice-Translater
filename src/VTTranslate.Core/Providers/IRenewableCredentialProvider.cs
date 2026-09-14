namespace VTTranslate.Core.Providers;

/// <summary>
/// Phase 7.2 — optional capability: a provider whose short-lived authorization
/// credential can be replaced on the SAME live connection, without stopping,
/// disposing, or recreating it. Empirically validated for
/// <see cref="AzureSpeechTranslationProvider"/> against real Azure Speech
/// (docs/phase-7.2-long-running-translation-session-continuity.md, "REAL AZURE
/// VALIDATION" addendum) — three independent runs confirmed the same recognizer
/// instance continues producing partial and final results after a live token swap,
/// including during active mid-utterance speech, with no forced reconnect.
///
/// This interface is deliberately provider-neutral: it says nothing about Azure,
/// TranslationRecognizer, or any SDK type. A caller (e.g.
/// <c>ProviderCredentialRenewalCoordinator</c> in <c>VTTranslate.App</c>) checks for
/// this capability via a type check on whatever <see cref="ISpeechTranslationProvider"/>
/// instance it holds — exactly the same pattern already used for
/// <see cref="IReconnectingProvider"/>.
/// </summary>
public interface IRenewableCredentialProvider
{
    /// <summary>
    /// Attempts to apply <paramref name="newToken"/> to the currently-live connection.
    /// Returns <c>true</c> if applied; returns <c>false</c> (never throws for this
    /// reason) if the provider has already been stopped or has no live connection to
    /// update — this is not a failure to retry, it means the session is ending or has
    /// not started, and the caller should simply stop trying. Implementations must
    /// never apply a token after their own <c>StopAsync</c> has been called, and must
    /// never log or expose <paramref name="newToken"/>'s value.
    /// </summary>
    Task<bool> TryUpdateAuthorizationTokenAsync(string newToken, CancellationToken ct);
}
