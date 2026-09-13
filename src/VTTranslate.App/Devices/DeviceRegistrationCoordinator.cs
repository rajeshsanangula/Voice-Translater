using VTTranslate.App.Api;
using VTTranslate.App.Authentication;

namespace VTTranslate.App.Devices;

/// <summary>Corrective patch (Phase 7.1 runtime-risk audit, Risk 2). See <see cref="IDeviceRegistrationCoordinator"/> for the contract.</summary>
public sealed class DeviceRegistrationCoordinator(
    IAutraxisApiClient apiClient,
    ITokenProvider tokenProvider,
    IDeviceIdentityStore identityStore,
    string platform,
    string? displayName) : IDeviceRegistrationCoordinator
{
    private readonly SemaphoreSlim _registrationGate = new(1, 1);

    public async Task<Guid> EnsureDeviceRegisteredAsync(CancellationToken ct = default, bool forceReplace = false)
    {
        await _registrationGate.WaitAsync(ct);
        try
        {
            var accountKey = await tokenProvider.GetAccountKeyAsync(ct);
            if (accountKey is null)
                throw new InvalidOperationException("No authenticated account context is available for device registration.");

            if (!forceReplace)
            {
                var persisted = await identityStore.GetDeviceIdAsync(accountKey, ct);
                if (persisted is { } persistedId) return persistedId; // reuse — no POST /devices call
            }
            else
            {
                // Recovery path: the previously-persisted id was explicitly rejected
                // by the backend — drop it before requesting a replacement so a
                // concurrent caller never reads the now-known-invalid value.
                await identityStore.ClearDeviceIdAsync(accountKey, ct);
            }

            var device = await apiClient.RegisterDeviceAsync(platform, displayName, ct);
            await identityStore.SetDeviceIdAsync(accountKey, device.Id, ct);
            return device.Id;
        }
        finally
        {
            _registrationGate.Release();
        }
    }

    public async Task<(T Result, Guid DeviceId)> ExecuteWithDeviceRecoveryAsync<T>(Guid deviceId, Func<Guid, Task<T>> operation, CancellationToken ct = default)
    {
        try
        {
            return (await operation(deviceId), deviceId);
        }
        catch (AutraxisApiException ex) when (ex.Category == ApiErrorCategory.DeviceNotAuthorized)
        {
            // Bounded recovery: exactly one replacement registration, exactly one
            // retry of the SAME operation. A second failure (of any category,
            // including a repeated DeviceNotAuthorized) propagates unchanged — never
            // a further replacement, never a loop.
            var replacementDeviceId = await EnsureDeviceRegisteredAsync(ct, forceReplace: true);
            var result = await operation(replacementDeviceId);
            return (result, replacementDeviceId);
        }
    }
}
