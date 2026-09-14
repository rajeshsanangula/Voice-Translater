using VTTranslate.App.Api;
using VTTranslate.Core.Diagnostics;
using VTTranslate.Core.Providers;

namespace VTTranslate.App.Providers;

/// <summary>
/// Phase 7.2 — see <see cref="IProviderCredentialRenewalCoordinator"/> for the
/// contract. Implements docs/phase-7.2-long-running-translation-session-continuity.md
/// §9/§10/§17: proactive renewal, scheduled a fixed safety margin before the
/// credential's real <c>ExpiresAt</c>, with a small bounded retry (exponential
/// backoff) for transient AUTRAXIS-API failures only — an authorization/denial
/// response is never retried, and cancellation is never treated as a failure.
///
/// One sequential loop per instance — there is never more than one renewal attempt
/// in flight for a given direction at a time (docs §35).
/// </summary>
public sealed class ProviderCredentialRenewalCoordinator : IProviderCredentialRenewalCoordinator
{
    /// <summary>Default proactive-renewal safety margin — renew this long before the credential's real expiry. Conservative relative to Azure's fixed ~10-minute lifetime (leaves ~8 minutes of normal use before the first renewal attempt) without renewing so early that it meaningfully increases provider-access traffic (docs §9/§33).</summary>
    public static readonly TimeSpan DefaultSafetyWindow = TimeSpan.FromMinutes(2);

    private const int MaxRenewalAttemptsPerCycle = 3;
    private static readonly TimeSpan InitialBackoff = TimeSpan.FromSeconds(2);

    private readonly IAutraxisApiClient _apiClient;
    private readonly IRenewableCredentialProvider _provider;
    private readonly Guid _deviceId;
    private readonly string _providerName;
    private readonly string _capability;
    private readonly string _direction; // log tag only — never used for control flow
    private readonly IDiagnosticLogger _logger;
    private readonly TimeSpan _safetyWindow;

    private CancellationTokenSource? _lifetimeCts;
    private Task? _loopTask;
    private DateTimeOffset _currentExpiresAt;
    private bool _disposed;

    public ProviderCredentialRenewalCoordinator(
        IAutraxisApiClient apiClient,
        IRenewableCredentialProvider provider,
        Guid deviceId,
        string providerName,
        string capability,
        string direction,
        IDiagnosticLogger logger,
        TimeSpan? safetyWindow = null)
    {
        _apiClient = apiClient;
        _provider = provider;
        _deviceId = deviceId;
        _providerName = providerName;
        _capability = capability;
        _direction = direction;
        _logger = logger;
        _safetyWindow = safetyWindow ?? DefaultSafetyWindow;
    }

    public void Start(DateTimeOffset initialExpiresAt, CancellationToken sessionCt)
    {
        _currentExpiresAt = initialExpiresAt;
        _lifetimeCts = CancellationTokenSource.CreateLinkedTokenSource(sessionCt);
        _loopTask = Task.Run(() => RunLoopAsync(_lifetimeCts.Token));
    }

    /// <summary>Computes how long to wait before the next renewal attempt — never negative (an already-due/overdue credential renews immediately, per docs §27's fail-safe expiry handling). Public and pure (no I/O, no side effects) so it is directly unit-testable.</summary>
    public static TimeSpan ComputeRenewalDelay(DateTimeOffset expiresAt, TimeSpan safetyWindow, DateTimeOffset now)
    {
        var dueAt = expiresAt - safetyWindow;
        var delay = dueAt - now;
        return delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        try
        {
            while (true)
            {
                // ThrowIfCancellationRequested (rather than a boolean while-condition)
                // guarantees cancellation is ALWAYS reported through the catch block
                // below — including when cancellation happens before this iteration
                // ever reaches Task.Delay — so "cancelled" is never silently
                // indistinguishable from "loop simply exited."
                ct.ThrowIfCancellationRequested();

                var delay = ComputeRenewalDelay(_currentExpiresAt, _safetyWindow, DateTimeOffset.UtcNow);
                await Task.Delay(delay, ct);

                _logger.Log(_direction, "ProviderCredentialRenewalScheduled", $"expiresAt={_currentExpiresAt:O}");
                var succeeded = await RenewOnceWithBoundedRetryAsync(ct);
                if (!succeeded)
                {
                    // Give up proactively. This is not an infinite-retry situation and
                    // not a silent failure: the EXISTING, unmodified reconnect/fatal-
                    // error path in the provider itself still governs what happens
                    // once the current (un-renewed) credential eventually actually
                    // expires — exactly the same behavior as before Phase 7.2 existed.
                    // See docs §17 "PROVIDER"/"AUTHORIZATION" categories.
                    _logger.Log(_direction, "ProviderCredentialRenewalGivenUp", $"expiresAt={_currentExpiresAt:O}");
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Stop()/cancellation always wins — never surfaced as a failure (docs §14/§25).
            _logger.Log(_direction, "ProviderCredentialRenewalCancelled", "reason=stop_or_session_ended");
        }
    }

    private async Task<bool> RenewOnceWithBoundedRetryAsync(CancellationToken ct)
    {
        var backoff = InitialBackoff;

        for (var attempt = 1; attempt <= MaxRenewalAttemptsPerCycle; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            _logger.Log(_direction, "ProviderCredentialRenewalStarted", $"attempt={attempt}/{MaxRenewalAttemptsPerCycle}");
            var startedAt = DateTimeOffset.UtcNow;

            try
            {
                // Docs §11/§16: the renewal request is IDENTICAL in shape to the
                // original grant request and goes through the exact same,
                // unmodified Phase 6.8 gateway — full device/entitlement/
                // capability re-authorization, every single time. No shortcut, no
                // cache, no "it worked before" assumption.
                var grant = await _apiClient.RequestProviderAccessAsync(_deviceId, _providerName, _capability, ct);
                var latencyMs = (DateTimeOffset.UtcNow - startedAt).TotalMilliseconds;

                // Docs §27: fail closed on invalid/unusable metadata rather than
                // trusting it blindly.
                if (string.IsNullOrEmpty(grant.AccessToken) || grant.ExpiresAt <= DateTimeOffset.UtcNow)
                {
                    _logger.Log(_direction, "ProviderCredentialRenewalFailed", "category=configuration reason=invalid_grant_metadata");
                    return false;
                }

                var applied = await _provider.TryUpdateAuthorizationTokenAsync(grant.AccessToken, ct);
                if (!applied)
                {
                    // Stop() already won, or there is no live connection to update —
                    // not a failure to retry (docs §13/§14).
                    _logger.Log(_direction, "ProviderCredentialLiveReplacementFailed", "reason=stopped_or_not_started");
                    return false;
                }

                _currentExpiresAt = grant.ExpiresAt;
                _logger.Log(_direction, "ProviderCredentialLiveReplacementSucceeded",
                    $"attempt={attempt} latencyMs={latencyMs:F0} newExpiresAt={grant.ExpiresAt:O}");
                return true;
            }
            catch (OperationCanceledException)
            {
                throw; // never classified as a renewal failure — propagate to the loop's own handler
            }
            catch (AutraxisApiException ex) when (ex.Category is ApiErrorCategory.NetworkUnavailable or ApiErrorCategory.ServiceUnavailable)
            {
                // TRANSIENT (docs §17) — bounded retry with exponential backoff.
                _logger.Log(_direction, "ProviderCredentialRenewalFailed", $"category=transient attempt={attempt}/{MaxRenewalAttemptsPerCycle} apiCategory={ex.Category}");
                if (attempt == MaxRenewalAttemptsPerCycle) return false;
                await Task.Delay(backoff, ct);
                backoff += backoff; // 2s, 4s, 8s — bounded, deterministic, never unbounded
            }
            catch (AutraxisApiException ex)
            {
                // AUTHORIZATION/CONFIGURATION/PROVIDER-denial categories (docs §17) —
                // never retried; retrying an authorization denial cannot succeed
                // differently, matching the existing IsTransientFailure philosophy
                // already proven in AzureSpeechTranslationProvider.
                _logger.Log(_direction, "ProviderCredentialRenewalFailed", $"category=permanent apiCategory={ex.Category}");
                return false;
            }
        }

        return false;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _lifetimeCts?.Cancel();
        if (_loopTask is not null)
        {
            try { await _loopTask; }
            catch (OperationCanceledException) { /* expected on Stop */ }
        }
        _lifetimeCts?.Dispose();
    }
}
