using System.IO;
using System.Text.Json;

namespace VTTranslate.App.Devices;

/// <summary>
/// Corrective patch (Phase 7.1 runtime-risk audit, Risk 2) — production
/// <see cref="IDeviceIdentityStore"/>. A single small JSON file, per Windows user
/// (via <see cref="Environment.SpecialFolder.LocalApplicationData"/>, the same
/// per-user scope the DPAPI token cache already uses — see
/// <c>Authentication.DpapiTokenCacheStore</c>), mapping account key -> device id.
/// Deliberately NOT coupled to the MSAL token cache file or its DPAPI protection —
/// a device id is server-issued metadata identifying a licensed installation, not a
/// bearer credential, so it does not need (and per the corrective-patch instruction,
/// should not gain) a custom encryption scheme merely for a GUID.
///
/// A file-level <see cref="SemaphoreSlim"/> serializes read-modify-write access so
/// concurrent callers (see <c>MainViewModel</c>'s own registration gate, which already
/// prevents concurrent registration attempts) never corrupt the file with an
/// interleaved write. All input (the on-disk JSON, and every account key/device id
/// passed in) is treated as untrusted local state — malformed JSON, missing keys, or
/// non-GUID values are all treated as "nothing persisted," never as a crash.
/// </summary>
public sealed class LocalFileDeviceIdentityStore : IDeviceIdentityStore
{
    private readonly string _filePath;
    private readonly SemaphoreSlim _fileLock = new(1, 1);

    public LocalFileDeviceIdentityStore(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AUTRAXIS", "VoiceTranslater", "DeviceIdentity", "devices.json");
    }

    public async Task<Guid?> GetDeviceIdAsync(string accountKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(accountKey)) return null;

        await _fileLock.WaitAsync(ct);
        try
        {
            var map = await ReadMapLockedAsync(ct);
            if (map.TryGetValue(accountKey, out var raw) && Guid.TryParse(raw, out var deviceId))
                return deviceId;

            return null; // missing OR malformed — both treated identically, per instruction
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task SetDeviceIdAsync(string accountKey, Guid deviceId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(accountKey)) return;

        await _fileLock.WaitAsync(ct);
        try
        {
            var map = await ReadMapLockedAsync(ct);
            map[accountKey] = deviceId.ToString();
            await WriteMapLockedAsync(map, ct);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task ClearDeviceIdAsync(string accountKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(accountKey)) return;

        await _fileLock.WaitAsync(ct);
        try
        {
            var map = await ReadMapLockedAsync(ct);
            if (map.Remove(accountKey))
                await WriteMapLockedAsync(map, ct);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    private async Task<Dictionary<string, string>> ReadMapLockedAsync(CancellationToken ct)
    {
        try
        {
            if (!File.Exists(_filePath)) return new Dictionary<string, string>();

            await using var stream = File.OpenRead(_filePath);
            var map = await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(stream, cancellationToken: ct);
            return map ?? new Dictionary<string, string>();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Corrupt/unreadable local state must never crash the application — treated
            // as "nothing persisted," exactly like AppSettings.Load()'s own precedent.
            return new Dictionary<string, string>();
        }
    }

    private async Task WriteMapLockedAsync(Dictionary<string, string> map, CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        await using var stream = File.Create(_filePath);
        await JsonSerializer.SerializeAsync(stream, map, new JsonSerializerOptions { WriteIndented = true }, ct);
    }
}
