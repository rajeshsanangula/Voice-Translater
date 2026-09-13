namespace VTTranslate.Backend.Domain.Abstractions;

/// <summary>
/// The internal identity boundary approved in
/// docs/phase-6.2b-resolved-architecture-decisions.md §2, IMPLEMENTED for real in Phase
/// 6.4. The rest of the domain and application layers depend ONLY on this interface and
/// on <see cref="AuthenticatedPrincipal"/> — never on Microsoft Entra External ID types,
/// ASP.NET <c>ClaimsPrincipal</c>/<c>HttpContext</c>, or JwtBearer types directly. This
/// mirrors the isolation already proven in this repository by
/// <c>VTTranslate.Core.Providers.ISpeechTranslationProvider</c> around Azure Speech.
///
/// PHASE 6.4 DESIGN DECISION (see docs/phase-6.4-entra-authentication.md §8 for the full
/// rationale): actual cryptographic token verification — signature, issuer, audience,
/// expiry, not-before, signing-key rotation — is performed BEFORE this interface is ever
/// invoked, by the real, standard, Microsoft-supported
/// <c>Microsoft.AspNetCore.Authentication.JwtBearer</c> middleware configured in the API
/// composition root. This interface's job starts AFTER that verification has already
/// succeeded: given the verified claims from that already-authenticated request, extract
/// the stable identity fields the rest of the system needs.
///
/// This is why <see cref="TryCreatePrincipal"/> takes a plain
/// <c>IReadOnlyDictionary&lt;string,string&gt;</c> of already-verified claim values —
/// never a raw token string (this interface does not parse or validate tokens itself;
/// doing so would mean re-implementing cryptographic validation outside the standard
/// framework mechanism, which is explicitly prohibited) and never a
/// <c>System.Security.Claims.ClaimsPrincipal</c> (which would leak an ASP.NET type into
/// Domain).
///
/// CRITICAL: <see cref="AuthenticatedPrincipal"/> deliberately carries NO role/authorization
/// information. Microsoft Entra proves EXTERNAL IDENTITY only — "who authenticated?" The
/// AUTRAXIS role is a backend-owned concept, resolved separately from the AUTRAXIS
/// <c>Account</c> record after this principal is produced (see
/// <c>VTTranslate.Backend.Application.Identity.IAccountResolutionService</c>) — never
/// read from an Entra claim. This eliminates the "unknown/missing role claim" attack
/// surface structurally, rather than mitigating it after the fact.
/// </summary>
public interface IIdentityProvider
{
    /// <summary>A short, stable name for this implementation (e.g. "EntraExternalId") — used only for diagnostics/audit metadata and as the <see cref="Entities.Account.ExternalIdentityProvider"/> discriminator, never for branching application logic.</summary>
    string ProviderName { get; }

    /// <summary>
    /// Builds a Domain-safe <see cref="AuthenticatedPrincipal"/> from already-verified
    /// claim key/value pairs. Verification (signature/issuer/audience/expiry) has
    /// already happened upstream (ASP.NET JwtBearer middleware) by the time this is
    /// called — this method's only job is extracting and validating the SHAPE of the
    /// claims this provider needs (e.g. a stable subject identifier must be present and
    /// non-empty). Returns null (fail closed) if a required claim is missing or
    /// malformed — never fabricates a value or falls back to a less-stable identifier.
    /// </summary>
    AuthenticatedPrincipal? TryCreatePrincipal(IReadOnlyDictionary<string, string> verifiedClaims);
}

/// <summary>
/// The ONLY shape the rest of the backend sees from authentication — never an Entra
/// token, claim collection, or SDK type directly (Phase 6.2B §2). Carries identity only
/// — no role, no AUTRAXIS <c>Account</c> reference, no device information. See the
/// class-level doc comment on <see cref="IIdentityProvider"/> for why role is
/// deliberately absent.
/// </summary>
public sealed record AuthenticatedPrincipal(
    string Provider,
    string ExternalSubjectId,
    string? Email,
    bool EmailVerified,
    string? DisplayName,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt);
