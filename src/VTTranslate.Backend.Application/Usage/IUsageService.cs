using VTTranslate.Backend.Domain.Entities;

namespace VTTranslate.Backend.Application.Usage;

/// <summary>
/// Server-authoritative usage accounting (Phase 6.2B §7). Client-reported usage is
/// NEVER treated as the billing/enforcement authority — see
/// <see cref="RecordClientReportedHintAsync"/> vs. <see cref="RecordServerDerivedUsageAsync"/>.
///
/// FUTURE INTEGRATION (Phase 6.6+, not implemented here): once the real-time-translation
/// provider-token endpoint (Phase 6.1 §5/§9) exists, this service's
/// <see cref="RecordServerDerivedUsageAsync"/> is what that endpoint's token
/// issuance/renewal/expiry tracking will call to produce the authoritative record — this
/// phase only builds the recording/query primitives that future call site will use.
/// </summary>
public interface IUsageService
{
    /// <summary>
    /// Records SERVER-DERIVED usage — the only source ever summed for entitlement
    /// enforcement or billing (Phase 6.2B §7). Callers must be trusted backend
    /// components (e.g., the future provider-token lifecycle tracker), never a
    /// direct client-facing endpoint.
    /// </summary>
    Task RecordServerDerivedUsageAsync(Guid accountId, Guid deviceId, string direction, double secondsUsed, string? provider, CancellationToken ct);

    /// <summary>
    /// Records a CLIENT-REPORTED usage hint (Phase 6.1 §9's <c>/usage/report</c>). Used
    /// only for prompt display-summary updates and anomaly detection against the
    /// server-derived figure — this method must never be used to satisfy an entitlement
    /// check.
    /// </summary>
    Task RecordClientReportedHintAsync(Guid accountId, Guid deviceId, string direction, double secondsUsed, CancellationToken ct);

    /// <summary>Sums SERVER-DERIVED usage only for the given account/period — this is what <see cref="Entitlements.EntitlementService"/> calls; a client-reported hint never affects this figure.</summary>
    Task<double> GetAuthoritativeUsageSecondsAsync(Guid accountId, string periodBucket, CancellationToken ct);

    /// <summary>Display-only summary (Phase 6.1 §9's <c>/usage/summary</c>) — includes both sources for transparency, but callers must not mistake this for the enforcement figure (use <see cref="GetAuthoritativeUsageSecondsAsync"/> for that).</summary>
    Task<UsageSummary> GetSummaryAsync(Guid accountId, string periodBucket, CancellationToken ct);
}

public sealed record UsageSummary(string PeriodBucket, double ServerDerivedSeconds, double ClientReportedSeconds);
