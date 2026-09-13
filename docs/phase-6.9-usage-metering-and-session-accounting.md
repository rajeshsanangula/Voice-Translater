# Phase 6.9 — Authoritative Provider Usage Metering & Translation Session Accounting

## Objective

Close the gap between Phase 6.8 (authorized provider access) and true usage accounting.
**Authorization ≠ usage.** Obtaining a short-lived Azure provider credential via
`POST /provider-access` never, by itself, creates billable usage. Usage is determined
exclusively by the backend, from server-controlled timestamps and persisted session
state, never by the client and never merely because a provider credential was issued.

## Architecture

Domain → Application → Infrastructure → API, unchanged. New pieces:

- **Domain**: `TranslationSessionState` enum (`Active`, `Ended`, `Expired`, `Aborted`),
  `TranslationSession` entity, `ITranslationSessionRepository`,
  `ActiveSessionAlreadyExistsException`, and `IAccountRepository.LockAccountForUsageAccountingAsync`
  (a second, separately-named row-lock method — deliberately not unified with Phase
  6.7's `LockAccountForDeviceRegistrationAsync` so that frozen Phase 6.7 code/tests are
  never touched).
- **Application**: `ITranslationSessionService` / `TranslationSessionService` — reuses
  `IDeviceRegistrationService`, `IEntitlementService`, `IUsageService`,
  `IAuditEventRepository`, and `IUnitOfWork` unchanged.
- **Infrastructure**: `InMemoryTranslationSessionRepository` (test double, enforces the
  same active-session uniqueness in-process for unit tests) and
  `EfTranslationSessionRepository` (real PostgreSQL, translates unique-violation and
  concurrency exceptions at the boundary), `TranslationSessionConfiguration` (EF Core
  mapping), migration `AddTranslationSessions`.
- **API**: `POST /translation-sessions`, `POST /translation-sessions/{id}/heartbeat`,
  `POST /translation-sessions/{id}/end`, `TranslationSessionOptions` (`LeaseSeconds`,
  non-secret, default 60).

## Trust model

The client is untrusted for: account ID, entitlement, subscription status, plan, usage
allowance, usage duration/amount, billing state, device ownership, and any authorization
decision. The client may supply: `deviceId` (verified against the authenticated account),
an optional `clientSessionId` (correlation only, never the DB primary identity), and an
optional `direction` label. The account is always derived from
`AccountResolutionMiddleware`, exactly as in every prior phase's endpoints.

## State machine

```
START → ACTIVE → { END, EXPIRE, ABORT } → TERMINAL
```

Terminal states (`Ended`, `Expired`, `Aborted`) never transition further. Heartbeats
cannot revive a terminal session. `End` is idempotent — repeating it returns the same
success shape, never a second usage record. `Aborted` is defined but has no trigger in
this phase (mirrors Phase 6.7's unused `DeviceStatus.Pending` precedent).

## Usage model

`TranslationSessionSeconds`-equivalent: an integer count of seconds, computed
server-side, recorded via the existing `IUsageService.RecordServerDerivedUsageAsync`
(`UsageRecordSource.ServerDerived`) — no new usage table, no provider-specific usage
concept. Usage is written at exactly two points, both server-triggered:

1. **Explicit end** (`POST .../end`) — duration = `now − session.StartedAt`, where `now`
   is the server's own clock at the call (the client is verifiably connected at that
   instant).
2. **Lazy expiry reconciliation** — duration = `session.LastActivityAt − session.StartedAt`,
   using the last *proven-alive* timestamp rather than the reconciliation-time `now`,
   which could be arbitrarily later than when the client actually disappeared and would
   otherwise inflate recorded usage for a long-abandoned session.

Usage is **never** written at session start, and **never** written by
`/provider-access` — this is the literal enforcement of "authorization ≠ usage."

## Lease / heartbeat / expiry

`TranslationSessionOptions.LeaseSeconds` (default 60, configuration-driven, non-secret)
defines the maximum silence window. There is no background sweep job — expiry is
detected lazily, the same read-triggered reconciliation pattern Phase 6.6 established
for subscription lifecycle (`ReconcileExpiryAsync`, invoked at the top of `Heartbeat`
and `End`). A session with no activity for longer than the lease is transitioned to
`Expired` (with usage recorded up to `LastActivityAt`) the next time anything touches
it — a heartbeat, an end call, or a subsequent start. This also naturally covers client
crashes: nothing depends on the client calling `/end`, and all state is persisted, so a
server restart loses no in-flight accounting.

## Reconnect / idempotency

Idempotency and reconnect-after-expiry are both enforced by one PostgreSQL partial
unique index:

```sql
UNIQUE (AccountId, ClientSessionId) WHERE State = 'Active' AND ClientSessionId IS NOT NULL
```

A session-start retry with the same `clientSessionId` while the prior session is still
Active resolves to `Resumed` (the existing session's ID is returned, no new row, no
duplicate usage). Once that session is terminal, the same `clientSessionId` is free to
be reused by a genuinely new session — the index only ever constrains one live row at a
time. This mirrors the Phase 6.6 `Subscription` partial-index precedent.

## Concurrency model

- **Concurrent session admission** (same account, simultaneous start requests against a
  near-exhausted usage allowance): `LockAccountForUsageAccountingAsync` takes a
  PostgreSQL `SELECT ... FOR UPDATE` row lock on the account inside
  `IUnitOfWork.ExecuteInTransactionAsync` before re-checking device authorization,
  entitlement, and the idempotency key — serializing admission decisions per account,
  the same pattern Phase 6.7 established for pooled device registration. A
  process-local lock is never the production authority.
- **Session-level races** (heartbeat-vs-end, end-vs-end, heartbeat-vs-expiry,
  expiry-vs-end): `xmin`-based optimistic concurrency on `TranslationSession` (the same
  pattern used for `Account`/`Subscription`/`Device`). A losing writer's
  `ConcurrentUpdateException` is caught and the operation re-reads the now-terminal
  state, returning the idempotent outcome rather than propagating an error — terminal
  state always wins against a stale update.

## Transactional integrity

Session termination (explicit end or lazy expiry) and its usage/audit writes are one
atomic unit via `IUnitOfWork.ExecuteInTransactionAsync` — there is no path that leaves
usage recorded with the session still Active, or the session terminal with no usage
recorded. Verified by a real PostgreSQL rollback test
(`TranslationSessionEnd_ExceptionInsideTransaction_RollsBackBothSessionAndUsageWrites`).

## Account / device isolation

Every operation resolves the account exclusively from
`AccountResolutionMiddleware`. `HeartbeatAsync`/`EndAsync` return the same `NotFound`
outcome whether a session doesn't exist or belongs to another account
(`session is null || session.AccountId != accountId`) — no existence leak, identical to
the account/device isolation pattern established in Phases 6.4/6.7/6.8. Device
ownership/revocation is re-verified via the unchanged
`IDeviceRegistrationService.IsDeviceAuthorizedAsync`.

## Subscription / entitlement interaction

Session admission reuses `IEntitlementService.CanStartTranslationSessionAsync` unchanged
— no duplicated subscription/usage-limit logic. The same substring-based
`Reason.Contains("usage limit", ...)` split used in Phase 6.8's `ProviderAccessGateway`
distinguishes `EntitlementDenied` from `UsageDenied`.

## Usage API

`GET /usage` (Phase 6.3) required no change: it already aggregates all
`UsageRecordSource.ServerDerived` rows for the authenticated account via
`IUsageRecordRepository`, and Phase 6.9's session-derived records are written through
that exact same repository — they are included automatically.

## Audit

`TranslationSessionStarted` and `TranslationSessionEnded` are recorded via the existing
`IAuditEventRepository`, inside the same transaction as the state change they describe.
Lazy expiry additionally records `TranslationSessionEnded`/expiry audit entries at
reconciliation time. Heartbeats do **not** create a permanent audit record per call —
only lifecycle transitions are audited, per §28's "avoid excessive permanent audit
records per heartbeat." No audio, transcript, translated text, provider bearer token,
provider master secret, or auth JWT is ever logged or persisted.

## Privacy

`TranslationSession` stores only: server-generated session ID, account ID, device ID,
optional client correlation ID, state, start/last-activity/terminal timestamps, and an
optional direction label. No audio, transcript, or translated text is ever persisted,
for any reason including accounting.

## Provider neutrality

No `AzureUsageMinutes`-style provider-specific concept was introduced.
`TranslationSession`/`TranslationSessionState` are provider-agnostic; the optional
`Direction` field is the only session metadata resembling provider context, and it is a
plain language-pair label, not a provider identifier.

## Response contract

Session responses return only: `sessionId`, `state`, and the relevant timestamp
(`startedAt`/`lastActivityAt`/`terminalAt`) plus, for start, whether the call `resumed`
an existing session. No internal database identifiers beyond the session ID itself, no
provider credentials, no auth tokens, no stack traces.

## Error contracts

| Condition | Response |
|---|---|
| Unauthenticated | 401 (unchanged middleware) |
| Suspended/unknown account | 403 `account_suspended` / `account_not_found` (unchanged middleware) |
| Malformed `deviceId` | 400 `invalid_device_id` |
| Unauthorized/nonexistent/cross-account/revoked device | 403 `device_not_authorized` (non-enumerating) |
| Entitlement denied | 403 `entitlement_denied` |
| Usage limit exceeded | 403 `usage_limit_exceeded` |
| Nonexistent or cross-account session (heartbeat/end) | 404 `session_not_found` (non-enumerating) |
| Terminal session heartbeat | 409 `session_terminal` |
| Terminal session end (repeat) | 200 — idempotent success, not an error |

## Deferred / out of scope

- Provider-specific usage telemetry reconciliation (Azure-side actual-usage
  cross-checking) — the schema is provider-neutral and does not preclude adding this
  later, but no reconciliation logic exists yet.
- Mute/silence-based session termination or "muted time isn't billable" policy — mute
  state is not tracked by this phase at all.
- A background expiry-sweep scheduler — expiry is purely lazy/read-triggered, matching
  existing codebase precedent (no scheduler infrastructure exists anywhere in this
  repo).
- Any Paddle/billing-portal/payment UI, mobile UI, admin portal, or new
  ASR/translation/TTS behavior — entirely untouched.

## Test strategy

- **Unit tests** (`TranslationSessionServiceTests.cs`, in-memory repositories): start
  authorization paths (success, nonexistent/cross-account/revoked device, no
  subscription, usage-limit-exhausted), idempotent duplicate start, reconnect-after-expiry,
  heartbeat (active/after-end/after-expiry-never-revives/cross-account), end
  (server-computed duration, idempotent repeat, cross-account denial), expiry
  (usage bounded by last activity, not wall-clock time), and a structural
  no-secret-fields check on the `TranslationSession` entity.
- **HTTP boundary tests** (`TranslationSessionEndpointTests.cs`, `WebApplicationFactory`):
  anonymous 401, malformed request 400, device authorization/isolation 403, entitlement
  denial 403, successful start 201 with no leaked secrets/internal IDs, idempotent
  resume 200, heartbeat/end 404/409/200 paths, and proof that extra client-supplied
  JSON fields (`accountId`, `usageDuration`, `allowedMinutes`, `providerSecret`) are
  silently ignored rather than honored.
- **Real PostgreSQL tests** (`EfPostgresPersistenceTests.cs`, Testcontainers,
  `[SkipIfNoDockerFact]`): table existence, FK enforcement, the partial-unique-index
  behavior (duplicate Active rejected, Active+terminal coexist), a transactional
  rollback proof for End, and — per §21/§36's explicit requirement that an in-memory
  double cannot prove PostgreSQL concurrency safety — a concurrent-session-admission
  race test (`ConcurrentSessionStart_NeverExceedsUsageLimit_RealPostgresLock`) driving
  8 simultaneous `StartSessionAsync` calls, each on its own connection/service graph,
  against an account with a fully exhausted usage allowance, asserting zero can slip
  through to create an Active session.

These tests self-skip in this sandbox (no local Docker); they execute for real in CI via
the existing `.github/workflows/phase-6.5-db-verification.yml`, unmodified.

## Operational considerations

`TranslationSessionOptions.LeaseSeconds` is configuration-bound (`TranslationSessions`
section in `appsettings.json`, non-secret, bounded 1–3600 by
`ConfigurationSecretSafetyTests`), defaulting to 60 seconds if unset or non-positive —
never "no expiry."
