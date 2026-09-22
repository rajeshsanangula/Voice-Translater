using VTTranslate.Backend.Domain.Entities;

namespace VTTranslate.Backend.Application.Sessions;

/// <summary>
/// Phase 6.9 — server-authoritative translation-session lifecycle
/// (docs/phase-6.9-usage-metering-and-session-accounting.md). Reuses
/// IDeviceRegistrationService (Phase 6.7) and IEntitlementService (Phase 6.3/6.6)
/// unchanged for admission; never duplicates device or subscription logic. Completely
/// separate from Phase 6.8's IProviderAccessGateway — issuing a provider credential
/// never starts, extends, or ends a session, and never creates usage.
/// </summary>
public interface ITranslationSessionService
{
    Task<SessionStartResult> StartSessionAsync(Guid accountId, Guid deviceId, string? clientSessionId, string? direction, CancellationToken ct);
    Task<SessionOperationResult> HeartbeatAsync(Guid accountId, Guid sessionId, CancellationToken ct);
    Task<SessionOperationResult> EndAsync(Guid accountId, Guid sessionId, CancellationToken ct);
}

public enum SessionStartOutcome
{
    Started,
    Resumed,
    DeviceNotAuthorized,
    EntitlementDenied,
    UsageDenied,
}

public sealed record SessionStartResult(SessionStartOutcome Outcome, TranslationSession? Session, string Reason, string? Code = null);

public enum SessionOperationOutcome
{
    Success,
    NotFound,
    AlreadyTerminal,
}

public sealed record SessionOperationResult(SessionOperationOutcome Outcome, TranslationSession? Session, string Reason);
