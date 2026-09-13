using VTTranslate.Backend.Application.Devices;
using VTTranslate.Backend.Application.Entitlements;
using VTTranslate.Backend.Domain.Abstractions;
using VTTranslate.Backend.Domain.Entities;
using VTTranslate.Backend.Domain.Enums;

namespace VTTranslate.Backend.Application.ProviderAccess;

public sealed class ProviderAccessGateway(
    IDeviceRegistrationService devices,
    IEntitlementService entitlements,
    IEnumerable<IProviderCredentialIssuer> issuers,
    IProviderAccessRepository grants,
    IAuditEventRepository audit,
    IUnitOfWork unitOfWork,
    IClock clock,
    TimeSpan credentialLifetime) : IProviderAccessGateway
{
    public async Task<ProviderAccessResult> RequestAccessAsync(Guid accountId, Guid deviceId, Provider provider, ProviderCapability capability, CancellationToken ct)
    {
        // Step 1 (docs/phase-6.8-provider-access-gateway.md §4): device authorization,
        // reusing Phase 6.7's existing IsDeviceAuthorizedAsync unchanged — this single
        // check already covers "device doesn't exist", "device belongs to another
        // account", and "device is revoked" identically (it returns false for all three,
        // by design, so this gateway cannot distinguish or leak which case applies).
        if (!await devices.IsDeviceAuthorizedAsync(accountId, deviceId, ct))
        {
            await AuditAsync(accountId, deviceId, provider, capability, "ProviderAccessDeniedDeviceNotAuthorized", ct);
            return ProviderAccessResult.Deny(ProviderAccessOutcome.DeviceNotAuthorized, "device is not authorized for this account");
        }

        // Step 2: subscription/entitlement/usage authority — reused UNCHANGED from
        // Phase 6.3/6.6's EntitlementService. Never re-implemented here.
        var decision = await entitlements.CanStartTranslationSessionAsync(accountId, deviceId, ct);
        if (!decision.Allowed)
        {
            var outcome = decision.Reason.Contains("usage limit", StringComparison.OrdinalIgnoreCase)
                ? ProviderAccessOutcome.UsageDenied
                : ProviderAccessOutcome.EntitlementDenied;
            await AuditAsync(accountId, deviceId, provider, capability, $"ProviderAccessDenied{outcome}", ct);
            return ProviderAccessResult.Deny(outcome, decision.Reason);
        }

        // Step 3: provider/capability support.
        var issuer = issuers.FirstOrDefault(i => i.Provider == provider);
        if (issuer is null)
        {
            await AuditAsync(accountId, deviceId, provider, capability, "ProviderAccessDeniedUnsupportedProvider", ct);
            return ProviderAccessResult.Deny(ProviderAccessOutcome.UnsupportedProvider, "provider is not configured/supported");
        }
        if (!issuer.SupportsCapability(capability))
        {
            await AuditAsync(accountId, deviceId, provider, capability, "ProviderAccessDeniedUnsupportedCapability", ct);
            return ProviderAccessResult.Deny(ProviderAccessOutcome.UnsupportedCapability, "capability is not supported by this provider");
        }

        // Step 4: issuance. The master credential never leaves the issuer implementation
        // (Infrastructure) — only the short-lived result crosses this boundary.
        var credential = await issuer.IssueAsync(capability, credentialLifetime, ct);
        if (credential is null)
        {
            await AuditAsync(accountId, deviceId, provider, capability, "ProviderAccessDeniedProviderUnavailable", ct);
            return ProviderAccessResult.Deny(ProviderAccessOutcome.ProviderUnavailable, "provider credential issuance failed");
        }

        // Step 5: persist the grant + audit record atomically (Phase 6.6's IUnitOfWork
        // pattern, reused) — never the credential/token value itself.
        var correlationId = Guid.NewGuid();
        await unitOfWork.ExecuteInTransactionAsync<object?>(async innerCt =>
        {
            await grants.SaveAsync(new ProviderAccessGrant
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                DeviceId = deviceId,
                Provider = provider,
                Capability = capability,
                IssuedAt = clock.UtcNow,
                ExpiresAt = credential.ExpiresAt,
                CorrelationId = correlationId,
            }, innerCt);

            await audit.AddAsync(new AuditEvent
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                EventType = "ProviderAccessIssued",
                Metadata = $"deviceId={deviceId};provider={provider};capability={capability};correlationId={correlationId};expiresAt={credential.ExpiresAt:O}",
                OccurredAt = clock.UtcNow,
            }, innerCt);

            return null;
        }, ct);

        return ProviderAccessResult.Granted(credential, correlationId);
    }

    private async Task AuditAsync(Guid accountId, Guid deviceId, Provider provider, ProviderCapability capability, string eventType, CancellationToken ct) =>
        await audit.AddAsync(new AuditEvent
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            EventType = eventType,
            Metadata = $"deviceId={deviceId};provider={provider};capability={capability}",
            OccurredAt = clock.UtcNow,
        }, ct);
}
