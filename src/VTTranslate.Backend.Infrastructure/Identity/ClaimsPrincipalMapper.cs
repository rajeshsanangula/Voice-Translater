using System.Security.Claims;

namespace VTTranslate.Backend.Infrastructure.Identity;

/// <summary>
/// The ONLY place in this codebase that touches <see cref="System.Security.Claims.ClaimsPrincipal"/>
/// outside the API composition root's own middleware wiring. Converts an ASP.NET
/// <see cref="ClaimsPrincipal"/> — produced by the real JwtBearer authentication
/// middleware AFTER it has already verified the token's signature/issuer/audience/expiry
/// — into the plain, Domain-safe dictionary shape <see cref="Abstractions.IIdentityProvider.TryCreatePrincipal"/>
/// expects. This is what keeps <c>ClaimsPrincipal</c> (an ASP.NET type) out of
/// Domain/Application entirely, per the Phase 6.4 instruction.
///
/// If the same claim type appears more than once, the FIRST value wins — Entra tokens do
/// not legitimately emit duplicate single-valued claims like <c>sub</c>/<c>email</c>, so
/// this is a defensive, deterministic choice, not an attempt to merge conflicting
/// claims into one.
/// </summary>
public static class ClaimsPrincipalMapper
{
    public static IReadOnlyDictionary<string, string> ToClaimsDictionary(this ClaimsPrincipal principal)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var claim in principal.Claims)
        {
            if (!result.ContainsKey(claim.Type))
                result[claim.Type] = claim.Value;
        }
        return result;
    }
}
