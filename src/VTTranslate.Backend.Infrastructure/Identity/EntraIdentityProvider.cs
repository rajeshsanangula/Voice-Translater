using VTTranslate.Backend.Domain.Abstractions;

namespace VTTranslate.Backend.Infrastructure.Identity;

/// <summary>
/// The production <see cref="IIdentityProvider"/> implementation for Microsoft Entra
/// External ID (Phase 6.4). Deliberately does NOT parse, decode, or verify a raw token —
/// that is performed by the real, standard
/// <c>Microsoft.AspNetCore.Authentication.JwtBearer</c> middleware, configured in the
/// API composition root (see <c>VTTranslate.Backend.Api.Program</c> and
/// <c>VTTranslate.Backend.Api.Security.AccountResolutionMiddleware</c>). By the time
/// <see cref="TryCreatePrincipal"/> is called, the caller has ALREADY verified the
/// token's signature, issuer, audience, and expiry via that middleware — this class only
/// extracts the well-known Entra claim types into the Domain-safe
/// <see cref="AuthenticatedPrincipal"/> shape.
///
/// Claim types used (standard Entra External ID / Microsoft identity platform v2.0
/// claim names — not invented): <c>sub</c> (stable per-application subject identifier —
/// preferred) or <c>oid</c> (directory object ID — fallback, still stable) for
/// <see cref="AuthenticatedPrincipal.ExternalSubjectId"/>; <c>email</c> or
/// <c>preferred_username</c> for <see cref="AuthenticatedPrincipal.Email"/>; <c>name</c>
/// for <see cref="AuthenticatedPrincipal.DisplayName"/>; <c>email_verified</c> (if
/// present — Entra External ID does not always emit this claim, so its absence is
/// treated as "not verified," never "verified by default," per the fail-closed
/// principle) for <see cref="AuthenticatedPrincipal.EmailVerified"/>; <c>iat</c>/<c>exp</c>
/// (Unix seconds, standard JWT claims) for issued/expiry timestamps.
///
/// NEVER reads or maps any role/permission claim — see the class-level doc comment on
/// <see cref="IIdentityProvider"/> for why AUTRAXIS roles are never sourced from Entra.
/// </summary>
public sealed class EntraIdentityProvider : IIdentityProvider
{
    public const string SubjectClaimType = "sub";
    public const string ObjectIdClaimType = "oid";
    public const string EmailClaimType = "email";
    public const string PreferredUsernameClaimType = "preferred_username";
    public const string EmailVerifiedClaimType = "email_verified";
    public const string NameClaimType = "name";
    public const string IssuedAtClaimType = "iat";
    public const string ExpiryClaimType = "exp";

    public string ProviderName => "EntraExternalId";

    public AuthenticatedPrincipal? TryCreatePrincipal(IReadOnlyDictionary<string, string> verifiedClaims)
    {
        // Stable external subject identifier is REQUIRED — fail closed (return null,
        // never fabricate or fall back to a less-stable identifier such as email) if
        // neither the standard "sub" nor "oid" claim is present and non-empty.
        var subject = GetNonEmpty(verifiedClaims, SubjectClaimType) ?? GetNonEmpty(verifiedClaims, ObjectIdClaimType);
        if (subject is null) return null;

        if (!TryGetUnixSeconds(verifiedClaims, IssuedAtClaimType, out var issuedAt)) return null;
        if (!TryGetUnixSeconds(verifiedClaims, ExpiryClaimType, out var expiresAt)) return null;

        var email = GetNonEmpty(verifiedClaims, EmailClaimType) ?? GetNonEmpty(verifiedClaims, PreferredUsernameClaimType);
        var emailVerified = verifiedClaims.TryGetValue(EmailVerifiedClaimType, out var ev) &&
                             bool.TryParse(ev, out var evParsed) && evParsed; // absence/malformed => false, never true by default
        var displayName = GetNonEmpty(verifiedClaims, NameClaimType);

        return new AuthenticatedPrincipal(ProviderName, subject, email, emailVerified, displayName, issuedAt, expiresAt);
    }

    private static string? GetNonEmpty(IReadOnlyDictionary<string, string> claims, string key) =>
        claims.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    private static bool TryGetUnixSeconds(IReadOnlyDictionary<string, string> claims, string key, out DateTimeOffset value)
    {
        value = default;
        if (!claims.TryGetValue(key, out var raw) || !long.TryParse(raw, out var seconds)) return false;
        value = DateTimeOffset.FromUnixTimeSeconds(seconds);
        return true;
    }
}
