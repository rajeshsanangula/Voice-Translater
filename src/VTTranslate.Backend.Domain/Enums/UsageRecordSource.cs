namespace VTTranslate.Backend.Domain.Enums;

/// <summary>
/// Where a <see cref="Entities.UsageRecord"/>'s duration figure came from. Justified
/// directly by Phase 6.2B §7 ("server-authoritative... client may display, never
/// authoritative") — the domain must be able to tell the two apart so a service can
/// refuse to treat a <see cref="ClientReportedHint"/> record as billing-authoritative.
/// </summary>
public enum UsageRecordSource
{
    /// <summary>Derived server-side from provider-token issuance/renewal/expiry tracking (Phase 6.2B §7). The only source treated as authoritative for entitlement enforcement and billing.</summary>
    ServerDerived,

    /// <summary>Reported by the client at session end (Phase 6.1 §9's <c>/usage/report</c>). Used only to close out a record promptly for display purposes and for anomaly detection against the server-derived figure — NEVER authoritative on its own.</summary>
    ClientReportedHint,
}
