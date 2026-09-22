using VTTranslate.Backend.Application.Devices;
using VTTranslate.Backend.Application.Entitlements;
using VTTranslate.Backend.Application.Usage;
using VTTranslate.Backend.Domain;
using VTTranslate.Backend.Domain.Abstractions;
using VTTranslate.Backend.Domain.Entities;
using VTTranslate.Backend.Domain.Enums;

namespace VTTranslate.Backend.Application.Sessions;

public sealed class TranslationSessionService(
    IDeviceRegistrationService devices,
    IEntitlementService entitlements,
    ITranslationSessionRepository sessions,
    IAccountRepository accounts,
    IUsageService usage,
    IAuditEventRepository audit,
    IUnitOfWork unitOfWork,
    IClock clock,
    TimeSpan leaseDuration) : ITranslationSessionService
{
    private const string UnspecifiedDirection = "unspecified";

    public async Task<SessionStartResult> StartSessionAsync(Guid accountId, Guid deviceId, string? clientSessionId, string? direction, CancellationToken ct)
    {
        // Fast-path idempotent resume check BEFORE opening a transaction — the
        // authoritative, race-safe check happens again inside the lock below; this is
        // purely an optimization so a normal (non-racing) reconnect doesn't need a
        // write transaction at all.
        if (!string.IsNullOrEmpty(clientSessionId))
        {
            var existing = await sessions.FindActiveByClientSessionIdAsync(accountId, clientSessionId, ct);
            if (existing is not null)
            {
                var reconciled = await ReconcileExpiryAsync(existing, deviceId, ct);
                if (reconciled.State == TranslationSessionState.Active)
                    return new SessionStartResult(SessionStartOutcome.Resumed, reconciled, "resumed existing active session");
                // else: existing session is now terminal — a genuinely new session may be created (Phase 6.9 §15).
            }
        }

        return await unitOfWork.ExecuteInTransactionAsync(async innerCt =>
        {
            // Serializes concurrent admission attempts for the SAME account — closes the
            // check-then-act race where two simultaneous starts could both observe the
            // same stale usage-against-limit result (docs §21). All checks below are
            // deliberately re-run INSIDE the lock, not just before it, so there is no
            // TOCTOU gap between "checked" and "admitted".
            await accounts.LockAccountForUsageAccountingAsync(accountId, innerCt);

            if (!string.IsNullOrEmpty(clientSessionId))
            {
                var raced = await sessions.FindActiveByClientSessionIdAsync(accountId, clientSessionId, innerCt);
                if (raced is not null)
                    return new SessionStartResult(SessionStartOutcome.Resumed, raced, "resumed existing active session (race)");
            }

            if (!await devices.IsDeviceAuthorizedAsync(accountId, deviceId, innerCt))
            {
                await AuditAsync(accountId, null, "TranslationSessionDeniedDeviceNotAuthorized", innerCt);
                return new SessionStartResult(SessionStartOutcome.DeviceNotAuthorized, null, "device is not authorized for this account");
            }

            var decision = await entitlements.CanStartTranslationSessionAsync(accountId, deviceId, innerCt);
            if (!decision.Allowed)
            {
                var outcome = decision.Reason.Contains("usage limit", StringComparison.OrdinalIgnoreCase)
                    ? SessionStartOutcome.UsageDenied
                    : SessionStartOutcome.EntitlementDenied;
                await AuditAsync(accountId, null, $"TranslationSessionDenied{outcome}", innerCt);
                return new SessionStartResult(outcome, null, decision.Reason, decision.Code);
            }

            var now = clock.UtcNow;
            var session = new TranslationSession
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                DeviceId = deviceId,
                ClientSessionId = clientSessionId,
                State = TranslationSessionState.Active,
                StartedAt = now,
                LastActivityAt = now,
                Direction = string.IsNullOrWhiteSpace(direction) ? UnspecifiedDirection : direction,
            };

            try
            {
                await sessions.SaveAsync(session, innerCt);
            }
            catch (ActiveSessionAlreadyExistsException)
            {
                // A concurrent start for the same (account, clientSessionId) won the
                // race at the database level — return that one, not an error (Phase 6.9
                // §11: a retry of the same logical start must never create duplicates).
                var winner = await sessions.FindActiveByClientSessionIdAsync(accountId, clientSessionId!, innerCt)
                    ?? throw new InvalidOperationException("Active session collision reported but the winning row could not be found.");
                return new SessionStartResult(SessionStartOutcome.Resumed, winner, "resumed existing active session (post-insert race)");
            }

            await AuditAsync(accountId, session.Id, "TranslationSessionStarted", innerCt);
            return new SessionStartResult(SessionStartOutcome.Started, session, "started");
        }, ct);
    }

    public async Task<SessionOperationResult> HeartbeatAsync(Guid accountId, Guid sessionId, CancellationToken ct)
    {
        var session = await sessions.FindByIdAsync(sessionId, ct);
        if (session is null || session.AccountId != accountId)
            return new SessionOperationResult(SessionOperationOutcome.NotFound, null, "session not found");

        var reconciled = await ReconcileExpiryAsync(session, session.DeviceId, ct);
        if (reconciled.State != TranslationSessionState.Active)
            return new SessionOperationResult(SessionOperationOutcome.AlreadyTerminal, reconciled, $"session is {reconciled.State}");

        reconciled.LastActivityAt = clock.UtcNow;
        try
        {
            await sessions.SaveAsync(reconciled, ct);
        }
        catch (ConcurrentUpdateException)
        {
            // Lost a race against a concurrent end/expiry — re-read; the winner's write
            // is authoritative and terminal state must win (Phase 6.9 §22).
            var fresh = await sessions.FindByIdAsync(sessionId, ct) ?? reconciled;
            return fresh.State == TranslationSessionState.Active
                ? new SessionOperationResult(SessionOperationOutcome.Success, fresh, "heartbeat recorded")
                : new SessionOperationResult(SessionOperationOutcome.AlreadyTerminal, fresh, $"session is {fresh.State}");
        }

        return new SessionOperationResult(SessionOperationOutcome.Success, reconciled, "heartbeat recorded");
    }

    public async Task<SessionOperationResult> EndAsync(Guid accountId, Guid sessionId, CancellationToken ct)
    {
        var session = await sessions.FindByIdAsync(sessionId, ct);
        if (session is null || session.AccountId != accountId)
            return new SessionOperationResult(SessionOperationOutcome.NotFound, null, "session not found");

        var reconciled = await ReconcileExpiryAsync(session, session.DeviceId, ct);
        if (reconciled.State != TranslationSessionState.Active)
            // Already terminal (Ended/Expired/Aborted) — idempotent: return the existing
            // terminal record, never record usage a second time (Phase 6.9 §13).
            return new SessionOperationResult(SessionOperationOutcome.AlreadyTerminal, reconciled, $"session is {reconciled.State}");

        return await unitOfWork.ExecuteInTransactionAsync(async innerCt =>
        {
            var now = clock.UtcNow;
            var durationSeconds = Math.Max(0, (now - reconciled.StartedAt).TotalSeconds);

            reconciled.State = TranslationSessionState.Ended;
            reconciled.TerminalAt = now;

            try
            {
                await sessions.SaveAsync(reconciled, innerCt);
            }
            catch (ConcurrentUpdateException)
            {
                var fresh = await sessions.FindByIdAsync(sessionId, innerCt) ?? reconciled;
                // Someone else (another End, or a lazy expiry reconciliation) already
                // made this terminal first — idempotent, no duplicate usage.
                return new SessionOperationResult(SessionOperationOutcome.AlreadyTerminal, fresh, $"session is {fresh.State}");
            }

            await usage.RecordServerDerivedUsageAsync(accountId, reconciled.DeviceId, reconciled.Direction ?? UnspecifiedDirection, durationSeconds, provider: null, innerCt);
            await AuditAsync(accountId, reconciled.Id, "TranslationSessionEnded", innerCt);

            return new SessionOperationResult(SessionOperationOutcome.Success, reconciled, "ended");
        }, ct);
    }

    /// <summary>
    /// Purely time-based reconciliation (Phase 6.9 §14, mirrors Phase 6.6's
    /// ReconcileTimeBasedTransitionsAsync pattern exactly): if the session is Active but
    /// its lease has lapsed since the last server-observed activity, it is transitioned
    /// to Expired and its authoritative usage (StartedAt -> LastActivityAt — the last
    /// PROVEN-alive moment, never "now", since "now" could be arbitrarily later than
    /// when the client actually disappeared) is recorded atomically. Idempotent:
    /// re-evaluating an already-terminal or still-within-lease session is a no-op.
    /// </summary>
    private async Task<TranslationSession> ReconcileExpiryAsync(TranslationSession session, Guid deviceId, CancellationToken ct)
    {
        if (session.State != TranslationSessionState.Active) return session;

        var now = clock.UtcNow;
        if (now <= session.LastActivityAt.Add(leaseDuration)) return session; // still within lease — no-op

        return await unitOfWork.ExecuteInTransactionAsync(async innerCt =>
        {
            var durationSeconds = Math.Max(0, (session.LastActivityAt - session.StartedAt).TotalSeconds);
            session.State = TranslationSessionState.Expired;
            session.TerminalAt = now;

            try
            {
                await sessions.SaveAsync(session, innerCt);
            }
            catch (ConcurrentUpdateException)
            {
                // Another operation (an explicit End, or a concurrent reconciliation)
                // already made this session terminal first — that write is authoritative;
                // re-read and defer to it rather than double-recording usage.
                return await sessions.FindByIdAsync(session.Id, innerCt) ?? session;
            }

            await usage.RecordServerDerivedUsageAsync(session.AccountId, deviceId, session.Direction ?? UnspecifiedDirection, durationSeconds, provider: null, innerCt);
            await AuditAsync(session.AccountId, session.Id, "TranslationSessionExpired", innerCt);
            return session;
        }, ct);
    }

    private async Task AuditAsync(Guid accountId, Guid? sessionId, string eventType, CancellationToken ct) =>
        await audit.AddAsync(new AuditEvent
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            EventType = eventType,
            Metadata = sessionId is null ? null : $"sessionId={sessionId}",
            OccurredAt = clock.UtcNow,
        }, ct);
}
