namespace VTTranslate.Backend.Domain.Entities;

/// <summary>
/// One configurable limit/grant belonging to a <see cref="Plan"/>. Deliberately a
/// key/value shape (not a fixed set of typed columns) so new entitlement types can be
/// added later without a schema change — see
/// docs/phase-6.2b-resolved-architecture-decisions.md §5. <see cref="Value"/> is a
/// string so both numeric (e.g. "5") and set-like (e.g. "en-US,de-DE") values fit
/// without inventing a variant type this early.
/// </summary>
public sealed class Entitlement
{
    public required Guid Id { get; init; }
    public required Guid PlanId { get; init; }
    public required string Key { get; init; }
    public required string Value { get; set; }
}

/// <summary>
/// Well-known entitlement keys actually referenced by <c>EntitlementService</c> in this
/// phase. NOT an exhaustive or closed list — new keys may be added to a <see cref="Plan"/>
/// without touching this class; these constants exist only so the handful of keys the
/// application layer currently reads have one spelling, not scattered string literals.
/// Every numeric value referenced here is a PLACEHOLDER, configured per environment/plan,
/// never compiled into application logic (Phase 6.2B §4/§5, Phase 6.3 instruction: "do
/// not hard-code final trial duration... final usage limits").
/// </summary>
public static class EntitlementKeys
{
    public const string MaxActiveDevices = "MaxActiveDevices";
    public const string TrialDurationDays = "TrialDurationDays";
    public const string TrialUsageLimitSeconds = "TrialUsageLimitSeconds";
    public const string UsageLimitSecondsPerPeriod = "UsageLimitSecondsPerPeriod";

    /// <summary>Bound on how long a GracePeriod subscription may continue to pass the entitlement gate before being treated as Expired — the mechanism that keeps grace periods from becoming an indefinite bypass (Phase 6.2B §12).</summary>
    public const string GracePeriodDays = "GracePeriodDays";
}
