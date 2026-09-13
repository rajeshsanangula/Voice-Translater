namespace VTTranslate.App.Devices;

/// <summary>
/// Corrective patch (Phase 7.1 runtime-risk audit, Risk 2) — persists the
/// SERVER-ISSUED <c>Device.Id</c> (Phase 6.7, unchanged) locally so it survives an
/// application process restart, closing the "every restart consumes another pooled
/// device slot" defect. This store never generates or derives an identifier itself —
/// it only remembers whichever Guid the backend's own <c>POST /devices</c> response
/// already returned. Never machine-name/MAC/CPU/disk/SID/IP-derived — hardware
/// fingerprinting remains explicitly out of scope (Phase 6.2B §6, unchanged).
///
/// Keyed by a caller-supplied, per-authenticated-identity <c>accountKey</c> (see
/// <see cref="Authentication.ITokenProvider.GetAccountKeyAsync"/>) — never by an
/// AUTRAXIS AccountId, which the client never learns at all (no endpoint this client
/// calls returns one). This is what prevents a device identifier registered under one
/// signed-in identity from ever being looked up while a different identity is
/// signed in on the same Windows profile (docs' own "account isolation" requirement,
/// extended to this local cache).
/// </summary>
public interface IDeviceIdentityStore
{
    /// <summary>Returns the persisted device id for this account key, or null if none is stored or the stored value is malformed — a malformed value is treated identically to "no usable persisted device," never as an error.</summary>
    Task<Guid?> GetDeviceIdAsync(string accountKey, CancellationToken ct = default);

    Task SetDeviceIdAsync(string accountKey, Guid deviceId, CancellationToken ct = default);

    Task ClearDeviceIdAsync(string accountKey, CancellationToken ct = default);
}
