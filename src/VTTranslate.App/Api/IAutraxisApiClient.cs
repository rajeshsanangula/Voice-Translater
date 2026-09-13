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

    /// <summary>Requests a short-lived provider credential (docs §22/§23) — the caller must keep the result memory-only, never persist or log <see cref="Api.ProviderAccessGrantDto.AccessToken"/>.</summary>
    Task<Api.ProviderAccessGrantDto> RequestProviderAccessAsync(Guid deviceId, string provider, string capability, CancellationToken ct = default);

    Task<Api.TranslationSessionStartedDto> StartTranslationSessionAsync(Guid deviceId, string? clientSessionId, string? direction, CancellationToken ct = default);
    Task<Api.TranslationSessionOperationDto> HeartbeatTranslationSessionAsync(Guid sessionId, CancellationToken ct = default);
    Task<Api.TranslationSessionOperationDto> EndTranslationSessionAsync(Guid sessionId, CancellationToken ct = default);
}
