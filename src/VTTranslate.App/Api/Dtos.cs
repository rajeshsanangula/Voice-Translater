namespace VTTranslate.App.Api;

/// <summary>Phase 7.1 — response/request shapes matching the existing, frozen backend response bodies exactly (Phase 7.0/6.7/6.8/6.9, unchanged). These are plain data-transfer records; none of them ever carries an AccountId — it is always resolved server-side.</summary>
public sealed record ProfileDto(string? DisplayName, string? PreferredLanguagePair, DateTimeOffset UpdatedAt);

public sealed record DeviceDto(Guid Id, string Platform, string? DisplayName, string Status, DateTimeOffset RegisteredAt, DateTimeOffset LastSeenAt, DateTimeOffset? RevokedAt);

public sealed record ProviderAccessGrantDto(string Provider, string Capability, string AccessToken, string Region, DateTimeOffset ExpiresAt, Guid CorrelationId);

public sealed record TranslationSessionStartedDto(Guid SessionId, string State, DateTimeOffset StartedAt, bool Resumed);

public sealed record TranslationSessionOperationDto(Guid SessionId, string State, DateTimeOffset? LastActivityAt, DateTimeOffset? TerminalAt);

/// <summary>Stable, backend-shape-matching problem response (docs §30) — the client parses this `status` field, never fragile human-readable text.</summary>
public sealed record ProblemDto(string? Status);
