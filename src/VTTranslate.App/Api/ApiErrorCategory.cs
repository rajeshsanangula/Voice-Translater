namespace VTTranslate.App.Api;

/// <summary>
/// Phase 7.1 — stable client-internal error categories
/// (docs/phase-7.1-customer-authentication-client-and-entra-integration.md §30/§37).
/// The client maps the backend's existing <c>{ "status": "..." }</c> problem shape
/// (or a connectivity/authentication failure) into exactly one of these — it never
/// pattern-matches arbitrary human-readable text, and never exposes a raw stack trace,
/// SQL detail, or internal identifier to the end user.
/// </summary>
public enum ApiErrorCategory
{
    None,
    AuthenticationRequired,
    AuthenticationCancelled,
    AccountNotUsable,
    DeviceNotAuthorized,
    EntitlementDenied,
    ProviderAccessDenied,
    NetworkUnavailable,
    ServiceUnavailable,
    BadRequest,
    Unknown,
}

/// <summary>Carries a stable category (never raw exception text) plus the backend's own status string, if any, purely for internal diagnostics (never shown to the end user, never logged with token content — docs §25).</summary>
public sealed class AutraxisApiException(ApiErrorCategory category, string? backendStatus = null, Exception? inner = null)
    : Exception($"AUTRAXIS API call failed: {category}" + (backendStatus is null ? "" : $" ({backendStatus})"), inner)
{
    public ApiErrorCategory Category { get; } = category;
    public string? BackendStatus { get; } = backendStatus;
}
