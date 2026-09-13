namespace VTTranslate.Backend.Domain.Enums;

/// <summary>
/// Phase 6.2B-approved role model. Ordered from least to most privileged — callers may
/// rely on the numeric ordering for a simple "at least this privileged" hierarchy check
/// (see <c>VTTranslate.Backend.Application.Authorization</c>), but must NEVER accept a
/// role value from a client request; a principal's role is only ever what the identity
/// boundary (<see cref="Domain.Abstractions.IIdentityProvider"/>) reports from a verified
/// token claim.
/// </summary>
public enum Role
{
    Customer = 0,
    Admin = 1,
    SuperAdmin = 2,
}
