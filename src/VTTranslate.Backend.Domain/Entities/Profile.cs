namespace VTTranslate.Backend.Domain.Entities;

/// <summary>Display-facing profile data for an <see cref="Account"/> — see Phase 6.1 §3/§8. Deliberately minimal: no payment data (delegated to the billing provider, §9), no speech/translation content ever stored here.</summary>
public sealed class Profile
{
    public required Guid AccountId { get; init; }
    public string? DisplayName { get; set; }

    /// <summary>e.g. "en-US"/"de-DE" — maps to the existing desktop app's EN/DE direction defaults. Business meaning of "which of the two existing directions this actually seeds" is not yet finalized against the real Windows client (Phase 6.8 concern), so this is documented as UNDECIDED usage, not an undecided field.</summary>
    public string? PreferredLanguagePair { get; set; }

    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; set; }
}
