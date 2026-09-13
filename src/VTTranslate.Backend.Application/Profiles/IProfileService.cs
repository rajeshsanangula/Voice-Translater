namespace VTTranslate.Backend.Application.Profiles;

/// <summary>
/// Phase 7.0 — wires the previously-persisted-but-unused <see cref="Domain.Entities.Profile"/>
/// entity (see docs/phase-7.0-production-identity-and-account-lifecycle.md §9). Every
/// method takes <c>accountId</c> only from the caller — API endpoints must resolve it
/// exclusively from <c>AccountResolutionMiddleware</c>, never from a client-supplied
/// value, so this service has no way to be pointed at another account's profile even if
/// misused.
/// </summary>
public interface IProfileService
{
    Task<ProfileView> GetAsync(Guid accountId, CancellationToken ct);
    Task<ProfileUpdateResult> UpdateAsync(Guid accountId, string? displayName, string? preferredLanguagePair, CancellationToken ct);
}

/// <summary>Read shape — deliberately excludes <see cref="Domain.Entities.Profile.AccountId"/> itself; a caller already knows its own account from context, and the response contract should not need to echo it back (docs/phase-7.0... §33, "AccountId must come from authenticated server-side AccountResolution").</summary>
public sealed record ProfileView(string? DisplayName, string? PreferredLanguagePair, DateTimeOffset UpdatedAt);

public enum ProfileUpdateOutcome
{
    Success,
    ValidationFailed,
}

public sealed record ProfileUpdateResult(ProfileUpdateOutcome Outcome, ProfileView? Profile);
