using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using VTTranslate.App.Authentication;

namespace VTTranslate.App.Api;

/// <summary>
/// Phase 7.1 — production <see cref="IAutraxisApiClient"/>. HTTPS-only base address
/// (docs §12/§20), bounded per-request timeout, cancellation-aware throughout, and the
/// hard-bounded "one renewal, one retry" 401 policy (docs §13) — never an unbounded
/// loop, never a generic network-retry policy applied to non-idempotent mutations
/// (docs §20's explicit separation of authentication retry from network retry).
/// </summary>
public sealed class AutraxisApiClient : IAutraxisApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly ITokenProvider _tokenProvider;

    public AutraxisApiClient(HttpClient http, ITokenProvider tokenProvider, AuthenticationOptions options)
    {
        if (!options.ApiBaseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
            !options.ApiBaseUrl.StartsWith("http://localhost", StringComparison.OrdinalIgnoreCase))
        {
            // HTTPS-only in any non-local configuration (docs §12/§20) — fail closed
            // rather than silently allow a plaintext production backend call.
            throw new InvalidOperationException("Api base URL must use HTTPS (http://localhost is permitted for local development only).");
        }

        _http = http;
        _http.BaseAddress = new Uri(options.ApiBaseUrl, UriKind.Absolute);
        _http.Timeout = TimeSpan.FromSeconds(30);
        _tokenProvider = tokenProvider;
    }

    public async Task<ProfileDto> GetProfileAsync(CancellationToken ct = default) =>
        await SendAsync<ProfileDto>(HttpMethod.Get, "/profile", body: null, ct) ?? throw EmptyResponse();

    public async Task<ProfileDto> UpdateProfileAsync(string? displayName, string? preferredLanguagePair, CancellationToken ct = default) =>
        await SendAsync<ProfileDto>(HttpMethod.Put, "/profile", new { displayName, preferredLanguagePair }, ct) ?? throw EmptyResponse();

    public async Task<IReadOnlyList<DeviceDto>> GetDevicesAsync(CancellationToken ct = default) =>
        await SendAsync<List<DeviceDto>>(HttpMethod.Get, "/devices", body: null, ct) ?? [];

    public async Task<DeviceDto> RegisterDeviceAsync(string platform, string? displayName, CancellationToken ct = default) =>
        await SendAsync<DeviceDto>(HttpMethod.Post, "/devices", new { platform, displayName }, ct) ?? throw EmptyResponse();

    public Task RevokeDeviceAsync(Guid deviceId, CancellationToken ct = default) =>
        SendAsync<object>(HttpMethod.Post, $"/devices/{deviceId}/revoke", body: null, ct);

    public async Task<ProviderAccessGrantDto> RequestProviderAccessAsync(Guid deviceId, string provider, string capability, CancellationToken ct = default) =>
        await SendAsync<ProviderAccessGrantDto>(HttpMethod.Post, "/provider-access", new { deviceId = deviceId.ToString(), provider, capability }, ct) ?? throw EmptyResponse();

    public async Task<TranslationSessionStartedDto> StartTranslationSessionAsync(Guid deviceId, string? clientSessionId, string? direction, CancellationToken ct = default) =>
        await SendAsync<TranslationSessionStartedDto>(HttpMethod.Post, "/translation-sessions", new { deviceId = deviceId.ToString(), clientSessionId, direction }, ct) ?? throw EmptyResponse();

    public async Task<TranslationSessionOperationDto> HeartbeatTranslationSessionAsync(Guid sessionId, CancellationToken ct = default) =>
        await SendAsync<TranslationSessionOperationDto>(HttpMethod.Post, $"/translation-sessions/{sessionId}/heartbeat", body: null, ct) ?? throw EmptyResponse();

    public async Task<TranslationSessionOperationDto> EndTranslationSessionAsync(Guid sessionId, CancellationToken ct = default) =>
        await SendAsync<TranslationSessionOperationDto>(HttpMethod.Post, $"/translation-sessions/{sessionId}/end", body: null, ct) ?? throw EmptyResponse();

    // ---- Phase 7.3: customer subscription/entitlement/usage visibility ----
    public async Task<SubscriptionDto> GetSubscriptionAsync(CancellationToken ct = default) =>
        await SendAsync<SubscriptionDto>(HttpMethod.Get, "/subscription", body: null, ct) ?? throw EmptyResponse();

    public async Task<EntitlementsDto> GetEntitlementsAsync(CancellationToken ct = default) =>
        await SendAsync<EntitlementsDto>(HttpMethod.Get, "/entitlements", body: null, ct) ?? throw EmptyResponse();

    public async Task<UsageSummaryDto> GetUsageAsync(CancellationToken ct = default) =>
        await SendAsync<UsageSummaryDto>(HttpMethod.Get, "/usage", body: null, ct) ?? throw EmptyResponse();

    public async Task<CancelSubscriptionResultDto> CancelSubscriptionAsync(bool immediate, CancellationToken ct = default) =>
        await SendAsync<CancelSubscriptionResultDto>(HttpMethod.Post, "/subscription/cancel", new { immediate }, ct) ?? throw EmptyResponse();

    private static AutraxisApiException EmptyResponse() => new(ApiErrorCategory.ServiceUnavailable, "empty_response");

    /// <summary>
    /// The single call site implementing the bounded 401 policy (docs §13) — attach
    /// token, send; on 401, force-refresh the token EXACTLY once and retry the SAME
    /// request EXACTLY once; a second 401 fails closed as
    /// <see cref="ApiErrorCategory.AuthenticationRequired"/>, never a further retry.
    /// A 403 is never retried and never triggers renewal (docs §14) — it is mapped
    /// directly to its own category and thrown.
    /// </summary>
    private async Task<T?> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await SendOnceAsync(method, path, body, forceRefresh: false, ct);
        }
        catch (AuthenticationRequiredException ex)
        {
            throw new AutraxisApiException(ApiErrorCategory.AuthenticationRequired, inner: ex);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new AutraxisApiException(ApiErrorCategory.ServiceUnavailable, inner: null); // timeout, not caller cancellation
        }
        catch (HttpRequestException ex)
        {
            throw new AutraxisApiException(ApiErrorCategory.NetworkUnavailable, inner: ex);
        }

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            response.Dispose();
            try
            {
                // Exactly one forced renewal, exactly one retry — never a loop.
                response = await SendOnceAsync(method, path, body, forceRefresh: true, ct);
            }
            catch (AuthenticationRequiredException ex)
            {
                throw new AutraxisApiException(ApiErrorCategory.AuthenticationRequired, inner: ex);
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                response.Dispose();
                throw new AutraxisApiException(ApiErrorCategory.AuthenticationRequired);
            }
        }

        return await HandleResponseAsync<T>(response, ct);
    }

    private async Task<HttpResponseMessage> SendOnceAsync(HttpMethod method, string path, object? body, bool forceRefresh, CancellationToken ct)
    {
        string token;
        try
        {
            token = await _tokenProvider.GetAccessTokenAsync(forceRefresh, allowInteractive: false, ct);
        }
        catch (AuthenticationRequiredException)
        {
            throw; // let the caller decide whether to trigger interactive sign-in — this client never launches a browser itself
        }

        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) request.Content = JsonContent.Create(body, options: JsonOptions);

        return await _http.SendAsync(request, ct);
    }

    private static async Task<T?> HandleResponseAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        using (response)
        {
            if (response.IsSuccessStatusCode)
            {
                if (typeof(T) == typeof(object)) return default;
                return await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct);
            }

            var status = await TryReadStatusAsync(response, ct);

            var category = response.StatusCode switch
            {
                HttpStatusCode.Forbidden when status == "device_not_authorized" => ApiErrorCategory.DeviceNotAuthorized,
                HttpStatusCode.Forbidden when status is "entitlement_denied" or "usage_denied" => ApiErrorCategory.EntitlementDenied,
                HttpStatusCode.Forbidden when status is "account_not_found" or "account_suspended" => ApiErrorCategory.AccountNotUsable,
                HttpStatusCode.Forbidden => ApiErrorCategory.ProviderAccessDenied,
                HttpStatusCode.BadRequest => ApiErrorCategory.BadRequest,
                // Phase 7.3: only this one specific, backend-guaranteed status string maps
                // to NoSubscription (Program.cs's GET /subscription and GET /entitlements
                // both return exactly { status: "no_subscription" } for this case, and no
                // other endpoint returns this string) — every other 404 still falls through
                // to the generic BadRequest mapping below, never inferred as "no subscription".
                HttpStatusCode.NotFound when status == "no_subscription" => ApiErrorCategory.NoSubscription,
                HttpStatusCode.NotFound => ApiErrorCategory.BadRequest,
                HttpStatusCode.ServiceUnavailable => ApiErrorCategory.ServiceUnavailable,
                >= HttpStatusCode.InternalServerError => ApiErrorCategory.ServiceUnavailable,
                _ => ApiErrorCategory.Unknown,
            };

            throw new AutraxisApiException(category, status);
        }
    }

    private static async Task<string?> TryReadStatusAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var problem = await response.Content.ReadFromJsonAsync<ProblemDto>(JsonOptions, ct);
            return problem?.Status;
        }
        catch (JsonException)
        {
            return null; // malformed/non-JSON body — never crash the caller over a diagnostic detail
        }
    }
}
