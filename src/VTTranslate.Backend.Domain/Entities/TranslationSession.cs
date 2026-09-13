using VTTranslate.Backend.Domain.Enums;

namespace VTTranslate.Backend.Domain.Entities;

/// <summary>
/// Phase 6.9 — a provider-neutral, server-authoritative translation session
/// (docs/phase-6.9-usage-metering-and-session-accounting.md). Deliberately does NOT
/// store audio, transcript, translated text, provider bearer tokens, provider master
/// secrets, or the authentication JWT — only lifecycle/accounting metadata. The
/// server-generated <see cref="Id"/> is the authoritative identity; <see cref="ClientSessionId"/>
/// is an optional client-supplied correlation key used only for idempotency/reconnect
/// matching, never as the database primary identity.
/// </summary>
public sealed class TranslationSession
{
    public required Guid Id { get; init; }
    public required Guid AccountId { get; init; }
    public required Guid DeviceId { get; init; }

    /// <summary>Optional client-supplied correlation key for idempotent start/reconnect — see the partial unique index on (AccountId, ClientSessionId) scoped to Active rows only (EF configuration), which is the actual concurrency-safe enforcement mechanism, not this field alone.</summary>
    public string? ClientSessionId { get; init; }

    public required TranslationSessionState State { get; set; }

    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>Server-controlled — updated only by a successful heartbeat while the session is Active. Used as the effective end boundary when a session is reconciled to Expired (as opposed to an explicit End, which uses the end call's own timestamp).</summary>
    public required DateTimeOffset LastActivityAt { get; set; }

    /// <summary>Set exactly once, when the session first becomes terminal (Ended/Expired/Aborted) — never updated again afterward.</summary>
    public DateTimeOffset? TerminalAt { get; set; }

    /// <summary>Free-form direction label, mirroring UsageRecord.Direction's existing convention (e.g. "en-US:de-DE") — optional at the API boundary, defaulted to a placeholder if not supplied. Not interpreted by any business logic in this phase.</summary>
    public string? Direction { get; init; }
}
