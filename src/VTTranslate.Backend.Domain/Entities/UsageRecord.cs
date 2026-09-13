using VTTranslate.Backend.Domain.Enums;

namespace VTTranslate.Backend.Domain.Entities;

/// <summary>
/// One accounted unit of translation usage. <see cref="Source"/> distinguishes
/// server-authoritative records from client-reported hints (Phase 6.2B §7) — a service
/// summing usage for entitlement enforcement or billing MUST filter to
/// <see cref="UsageRecordSource.ServerDerived"/> only; see
/// <c>VTTranslate.Backend.Application.Usage.UsageService</c>.
///
/// Billing UNIT is intentionally left as whole seconds (a neutral, unopinionated unit)
/// — the instruction is explicit not to invent a final billing unit; whether the
/// eventual commercial model bills in minutes, session-count, or something else is not
/// decided here. <see cref="SecondsUsed"/> is documented as a raw measurement, not a
/// commercial unit commitment.
/// </summary>
public sealed class UsageRecord
{
    public required Guid Id { get; init; }
    public required Guid AccountId { get; init; }
    public required Guid DeviceId { get; init; }

    /// <summary>e.g. "en-US:de-DE" — a free-form direction label mirroring the existing desktop app's two concurrent directions (Phase 6.1 §1). Not an enum here, to avoid the domain needing to know every possible language pair the product might ever support.</summary>
    public required string Direction { get; init; }

    /// <summary>Raw measured duration. NOT a final billing unit — see class doc comment.</summary>
    public required double SecondsUsed { get; init; }

    /// <summary>Which AI provider this usage was attributed to (e.g. "AzureSpeech") — for future multi-provider cost attribution; not enforced or interpreted by this phase's services.</summary>
    public string? Provider { get; init; }

    public required UsageRecordSource Source { get; init; }

    /// <summary>Billing-period bucket this record rolls up into (e.g. "2026-09" for a calendar-month period) — the exact period definition (calendar month vs. rolling window) is NOT decided in this phase; treat this as an opaque grouping key for now.</summary>
    public required string PeriodBucket { get; init; }

    public DateTimeOffset RecordedAt { get; init; }
}
