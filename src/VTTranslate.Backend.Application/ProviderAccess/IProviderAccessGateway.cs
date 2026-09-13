using VTTranslate.Backend.Domain.Abstractions;
using VTTranslate.Backend.Domain.Enums;

namespace VTTranslate.Backend.Application.ProviderAccess;

/// <summary>
/// Phase 6.8 — the single authorization chain for brokered provider access
/// (docs/phase-6.8-provider-access-gateway.md §4): device authorization (Phase 6.7,
/// reused unchanged) -> subscription/entitlement/usage authority
/// (<see cref="Entitlements.IEntitlementService"/>, Phase 6.3/6.6, reused unchanged) ->
/// provider/capability support -> short-lived credential issuance -> audit. Never
/// duplicates device or subscription logic — this is purely an orchestration layer over
/// the two existing authorities plus the new provider-issuance boundary.
/// </summary>
public interface IProviderAccessGateway
{
    Task<ProviderAccessResult> RequestAccessAsync(Guid accountId, Guid deviceId, Provider provider, ProviderCapability capability, CancellationToken ct);
}

public enum ProviderAccessOutcome
{
    Granted,
    DeviceNotAuthorized,
    EntitlementDenied,
    UsageDenied,
    UnsupportedProvider,
    UnsupportedCapability,
    ProviderUnavailable,
}

public sealed record ProviderAccessResult(ProviderAccessOutcome Outcome, IssuedProviderCredential? Credential, Guid? CorrelationId, string Reason)
{
    public static ProviderAccessResult Granted(IssuedProviderCredential credential, Guid correlationId) =>
        new(ProviderAccessOutcome.Granted, credential, correlationId, "ok");

    public static ProviderAccessResult Deny(ProviderAccessOutcome outcome, string reason) =>
        new(outcome, null, null, reason);
}
