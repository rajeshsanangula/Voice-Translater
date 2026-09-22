namespace VTTranslate.App.Api;

/// <summary>
/// Phase 7.1 — the single authenticated HTTP boundary to the AUTRAXIS backend
/// (docs §12/§19/§20). Every call attaches a bearer access token obtained from
/// <see cref="Authentication.ITokenProvider"/>; a 401 triggers exactly one forced
/// token renewal and one request retry (docs §13); a 403 never triggers renewal
/// (docs §14). No AccountId parameter appears anywhere on this interface — the
/// backend always resolves it from the bearer token (docs §18).
/// </summary>
public interface IAutraxisApiClient
{
    Task<Api.ProfileDto> GetProfileAsync(CancellationToken ct = default);
    Task<Api.ProfileDto> UpdateProfileAsync(string? displayName, string? preferredLanguagePair, CancellationToken ct = default);

    Task<IReadOnlyList<Api.DeviceDto>> GetDevicesAsync(CancellationToken ct = default);
    Task<Api.DeviceDto> RegisterDeviceAsync(string platform, string? displayName, CancellationToken ct = default);
    Task RevokeDeviceAsync(Guid deviceId, CancellationToken ct = default);

    /// <summary>Phase 25E — see <see cref="AutraxisApiClient.ReplaceDeviceAsync"/>'s doc comment.</summary>
    Task<Api.DeviceDto> ReplaceDeviceAsync(string platform, string? displayName, CancellationToken ct = default);

    /// <summary>Requests a short-lived provider credential (docs §22/§23) — the caller must keep the result memory-only, never persist or log <see cref="Api.ProviderAccessGrantDto.AccessToken"/>.</summary>
    Task<Api.ProviderAccessGrantDto> RequestProviderAccessAsync(Guid deviceId, string provider, string capability, CancellationToken ct = default);

    Task<Api.TranslationSessionStartedDto> StartTranslationSessionAsync(Guid deviceId, string? clientSessionId, string? direction, CancellationToken ct = default);
    Task<Api.TranslationSessionOperationDto> HeartbeatTranslationSessionAsync(Guid sessionId, CancellationToken ct = default);
    Task<Api.TranslationSessionOperationDto> EndTranslationSessionAsync(Guid sessionId, CancellationToken ct = default);

    // ---- Phase 7.3: customer subscription/entitlement/usage visibility ----
    // Read-mostly; the client is never authoritative for any of this data (docs
    // phase-7.3-customer-subscription-and-usage-visibility.md §16) — every value is
    // re-fetched from the backend, never cached beyond the current view's lifetime.

    /// <summary>Throws <see cref="AutraxisApiException"/> with <see cref="Api.ApiErrorCategory.NoSubscription"/> if the account has no subscription row yet (the backend's own guaranteed `no_subscription` shape) — never inferred from a generic 404.</summary>
    Task<Api.SubscriptionDto> GetSubscriptionAsync(CancellationToken ct = default);

    /// <summary>Same <see cref="Api.ApiErrorCategory.NoSubscription"/> contract as <see cref="GetSubscriptionAsync"/> — the backend returns the identical shape when there is no subscription.</summary>
    Task<Api.EntitlementsDto> GetEntitlementsAsync(CancellationToken ct = default);

    /// <summary>Never 404s for "no subscription" — the backend computes a usage summary unconditionally (UsageService.GetSummaryAsync), so this call has no <see cref="Api.ApiErrorCategory.NoSubscription"/> case.</summary>
    Task<Api.UsageSummaryDto> GetUsageAsync(CancellationToken ct = default);

    /// <summary>Preserves the existing backend cancellation contract exactly (`CancelSubscriptionRequest(bool Immediate)`, Program.cs) — <paramref name="immediate"/> true cancels now; false schedules cancellation at the current period's end. Never invents a third policy.</summary>
    Task<Api.CancelSubscriptionResultDto> CancelSubscriptionAsync(bool immediate, CancellationToken ct = default);
}
