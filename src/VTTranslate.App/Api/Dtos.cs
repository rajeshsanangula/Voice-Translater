namespace VTTranslate.App.Api;

/// <summary>Phase 7.1 — response/request shapes matching the existing, frozen backend response bodies exactly (Phase 7.0/6.7/6.8/6.9, unchanged). These are plain data-transfer records; none of them ever carries an AccountId — it is always resolved server-side.</summary>
public sealed record ProfileDto(string? DisplayName, string? PreferredLanguagePair, DateTimeOffset UpdatedAt);

public sealed record DeviceDto(Guid Id, string Platform, string? DisplayName, string Status, DateTimeOffset RegisteredAt, DateTimeOffset LastSeenAt, DateTimeOffset? RevokedAt);

public sealed record ProviderAccessGrantDto(string Provider, string Capability, string AccessToken, string Region, DateTimeOffset ExpiresAt, Guid CorrelationId);

public sealed record TranslationSessionStartedDto(Guid SessionId, string State, DateTimeOffset StartedAt, bool Resumed);

public sealed record TranslationSessionOperationDto(Guid SessionId, string State, DateTimeOffset? LastActivityAt, DateTimeOffset? TerminalAt);

/// <summary>Stable, backend-shape-matching problem response (docs §30) — the client parses this `status` field, never fragile human-readable text.</summary>
public sealed record ProblemDto(string? Status);

// ---- Phase 7.3 — customer subscription/entitlement/usage visibility ----
// Shapes matched exactly against the actual, unmodified GET /subscription,
// GET /entitlements, GET /usage, and POST /subscription/cancel response bodies in
// VTTranslate.Backend.Api/Program.cs (inspected directly, not assumed from the
// architecture proposal). No PlanId-to-name resolution exists in the actual
// /subscription or /entitlements response — see AccountViewModel's own doc comment
// for how the client handles that contract limitation without fabricating a name.

/// <summary>Matches `GET /subscription`'s `{ status, planId, currentPeriodStart, currentPeriodEnd, cancelAtPeriodEnd }` body exactly.</summary>
public sealed record SubscriptionDto(string Status, Guid PlanId, DateTimeOffset CurrentPeriodStart, DateTimeOffset CurrentPeriodEnd, bool CancelAtPeriodEnd);

/// <summary>Matches `POST /subscription/cancel`'s `{ status, cancelAtPeriodEnd }` body exactly — deliberately narrower than <see cref="SubscriptionDto"/> since the endpoint returns fewer fields.</summary>
public sealed record CancelSubscriptionResultDto(string Status, bool CancelAtPeriodEnd);

/// <summary>Matches `GET /entitlements`'s `{ subscriptionStatus, entitlements }` body exactly. <c>Entitlements</c> is deliberately open-ended (server may add keys without a client contract change) per Entitlement.cs's own doc comment — the client must never assume an exhaustive/closed key set.</summary>
public sealed record EntitlementsDto(string SubscriptionStatus, IReadOnlyDictionary<string, string> Entitlements);

/// <summary>Matches `GET /usage`'s `UsageSummary` record (`PeriodBucket`, `ServerDerivedSeconds`, `ClientReportedSeconds`) exactly — see UsageService.cs. <c>ServerDerivedSeconds</c> is the only authoritative value; <c>ClientReportedSeconds</c> is a hint only, never used for display-as-authoritative or enforcement.</summary>
public sealed record UsageSummaryDto(string PeriodBucket, double ServerDerivedSeconds, double ClientReportedSeconds);
