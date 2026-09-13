using System.Text.RegularExpressions;
using VTTranslate.Backend.Domain.Abstractions;
using VTTranslate.Backend.Domain.Entities;

namespace VTTranslate.Backend.Application.Profiles;

/// <summary>
/// Phase 7.0. <see cref="DisplayName"/> validation is a simple length bound (matching
/// the entity's own <c>HasMaxLength(200)</c> mapping — see EntityConfigurations.cs);
/// <see cref="PreferredLanguagePair"/> validation is a light shape check
/// (<c>xx-XX:yy-YY</c>, matching the entity's <c>HasMaxLength(50)</c>) rather than a
/// hard-coded list of specific language pairs, since committing to an exact supported
/// set here would duplicate a decision that belongs to the translation engine, not the
/// profile layer.
/// </summary>
public sealed class ProfileService(IProfileRepository profiles, IClock clock) : IProfileService
{
    private const int MaxDisplayNameLength = 200;
    private static readonly Regex LanguagePairPattern = new(@"^[a-z]{2}-[A-Z]{2}:[a-z]{2}-[A-Z]{2}$", RegexOptions.Compiled);

    public async Task<ProfileView> GetAsync(Guid accountId, CancellationToken ct)
    {
        var profile = await profiles.FindByAccountIdAsync(accountId, ct);
        // A Profile is created transactionally alongside every Account by
        // AccountProvisioningService, so this should always exist for a usable account;
        // fail safe (empty view) rather than throw for any pre-Phase-7.0 account created
        // before Profile wiring existed.
        return new ProfileView(profile?.DisplayName, profile?.PreferredLanguagePair, profile?.UpdatedAt ?? default);
    }

    public async Task<ProfileUpdateResult> UpdateAsync(Guid accountId, string? displayName, string? preferredLanguagePair, CancellationToken ct)
    {
        if (displayName is { Length: > MaxDisplayNameLength })
            return new ProfileUpdateResult(ProfileUpdateOutcome.ValidationFailed, null);

        if (preferredLanguagePair is not null && !LanguagePairPattern.IsMatch(preferredLanguagePair))
            return new ProfileUpdateResult(ProfileUpdateOutcome.ValidationFailed, null);

        var profile = await profiles.FindByAccountIdAsync(accountId, ct)
            ?? new Profile { AccountId = accountId, CreatedAt = clock.UtcNow, UpdatedAt = clock.UtcNow };

        profile.DisplayName = displayName;
        profile.PreferredLanguagePair = preferredLanguagePair;
        profile.UpdatedAt = clock.UtcNow;

        await profiles.SaveAsync(profile, ct);

        return new ProfileUpdateResult(ProfileUpdateOutcome.Success, new ProfileView(profile.DisplayName, profile.PreferredLanguagePair, profile.UpdatedAt));
    }
}
