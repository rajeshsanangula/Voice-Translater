namespace VTTranslate.App.Devices;

/// <summary>
/// Corrective patch (Phase 7.1 runtime-risk audit, Risk 2). Owns the full
/// persist-and-reuse algorithm for the server-issued <c>Device.Id</c> so this logic
/// is independently testable and kept out of <c>MainViewModel</c>. See
/// <see cref="IDeviceIdentityStore"/>'s own doc comment for what is/isn't persisted
/// and why. Phase 6.7's backend contract (<c>POST/GET /devices</c>,
/// <c>POST /devices/{id}/revoke</c>) is used exactly as-is — this class only decides
/// WHEN to call it.
/// </summary>
public interface IDeviceRegistrationCoordinator
{
    /// <summary>
    /// Returns a usable device id: the persisted one for the current authenticated
    /// identity if one exists, otherwise a freshly server-registered one (which is
    /// then persisted). Never calls <c>POST /devices</c> merely because the process
    /// restarted — only when no persisted id exists for the current identity, or
    /// <paramref name="forceReplace"/> is explicitly requested (recovery path, see
    /// <see cref="ExecuteWithDeviceRecoveryAsync{T}"/>). Concurrency-safe: concurrent
    /// callers within one process are serialized so at most one registration call is
    /// ever made for a given not-yet-persisted identity.
    /// </summary>
    Task<Guid> EnsureDeviceRegisteredAsync(CancellationToken ct = default, bool forceReplace = false);

    /// <summary>
    /// Runs <paramref name="operation"/> with <paramref name="deviceId"/>. If it fails
    /// with <see cref="VTTranslate.App.Api.ApiErrorCategory.DeviceNotAuthorized"/> (the persisted id
    /// has been revoked, or otherwise rejected by the backend), performs EXACTLY ONE
    /// replacement registration and retries <paramref name="operation"/> EXACTLY ONCE
    /// more with the new id — never a second replacement, never a retry loop. Any
    /// other failure (including a second <c>DeviceNotAuthorized</c>) propagates
    /// unchanged to the caller, which must map it to the existing, unchanged error
    /// categories (e.g. entitlement/limit/account-status denials fail safely, not
    /// silently).
    /// </summary>
    Task<(T Result, Guid DeviceId)> ExecuteWithDeviceRecoveryAsync<T>(Guid deviceId, Func<Guid, Task<T>> operation, CancellationToken ct = default);
}
