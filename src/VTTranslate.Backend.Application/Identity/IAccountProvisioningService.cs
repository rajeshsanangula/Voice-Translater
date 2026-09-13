using VTTranslate.Backend.Domain.Abstractions;
using VTTranslate.Backend.Domain.Entities;

namespace VTTranslate.Backend.Application.Identity;

/// <summary>
/// Phase 7.0 — implements the approved "hybrid gated just-in-time provisioning" policy
/// (Option D, docs/phase-7.0-production-identity-and-account-lifecycle.md §7). Invoked
/// ONLY by <c>AccountResolutionMiddleware</c>, and ONLY when
/// <see cref="Application.Identity.IAccountResolutionService"/> has already returned
/// <see cref="AccountResolutionOutcome.AccountNotFound"/> for an already-authenticated,
/// already-verified <see cref="AuthenticatedPrincipal"/> — this service never receives
/// or trusts anything from a raw client request body; every input is either the already
/// -verified principal or server-side configuration/state.
///
/// This is a controlled, explicit, observable decision point — never a silent side
/// effect buried inside account resolution or repository lookup — matching the flow the
/// architecture document requires: "Account exists? NO -> provisioning policy -> Allowed?".
/// </summary>
public interface IAccountProvisioningService
{
    Task<AccountProvisioningResult> ProvisionAsync(AuthenticatedPrincipal principal, CancellationToken ct);
}

public enum AccountProvisioningOutcome
{
    /// <summary>An <see cref="Account"/> (existing or newly created) is available and Active. This also covers the case where a concurrent request won a provisioning race — the caller receives the winner's Account, not an error.</summary>
    Provisioned,

    /// <summary>Provisioning was not allowed for this attempt (provisioning disabled by configuration, email not verified when verification is required, or the rate limit was exceeded). Deliberately a single, uniform outcome — the caller must not surface which specific reason applied, to avoid revealing provisioning-policy internals or enabling account-existence enumeration (see §17 of the architecture document).</summary>
    Denied,
}

public sealed record AccountProvisioningResult(AccountProvisioningOutcome Outcome, Account? Account);
