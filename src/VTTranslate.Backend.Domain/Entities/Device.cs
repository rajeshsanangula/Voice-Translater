using VTTranslate.Backend.Domain.Enums;

namespace VTTranslate.Backend.Domain.Entities;

/// <summary>
/// A server-authorized device, identified by an application-generated registration
/// identifier — explicitly NOT a hardware fingerprint (Phase 6.2B §6). Field set matches
/// Phase 6.1 §8/Phase 6.2B §6 exactly, now implemented.
/// </summary>
public sealed class Device
{
    /// <summary>The AUTRAXIS-issued device identifier, generated at registration time — never derived from hardware characteristics.</summary>
    public required Guid Id { get; init; }

    public required Guid AccountId { get; init; }
    public required DevicePlatform Platform { get; init; }

    /// <summary>User-editable label (e.g. "Sanan's Laptop"). Not used for identity — display only.</summary>
    public string? DisplayName { get; set; }

    public required DeviceStatus Status { get; set; }
    public DateTimeOffset RegisteredAt { get; init; }
    public DateTimeOffset LastSeenAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}
