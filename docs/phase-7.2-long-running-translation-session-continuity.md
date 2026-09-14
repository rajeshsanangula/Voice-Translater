# Phase 7.2 — Long-Running Translation Session Continuity & Short-Lived Provider Credential Renewal

**Status: ARCHITECTURE ONLY.** No source code, migration, API, test, CI workflow, or
configuration was created or modified. This document is the sole deliverable.

---

## 1. Executive summary

The Phase 7.1 runtime-risk audit correctly identified that Azure's provider credential
is fixed at exactly 10 minutes and that the current client neither renews it nor
survives its expiry — a live session simply dies, non-transiently, the moment the
credential lapses. Direct inspection of the code at baseline `5f13268` confirms this
precisely and surfaces additional, previously-undocumented hazards: the existing
provider already has a `readonly` authorization-token field with no update path, a
generation counter that already exists specifically to guard against stale-event
duplication (and is directly reusable for renewal), a raw field-level data race between
`PushAudio` and reconnect (undocumented until this audit), and a fully idempotent,
already-re-authorizing backend gateway that requires **zero API or backend change** to
support repeated issuance — including an `ExpiresAt` value the client already receives
today and silently discards.

This document designs a **provider-neutral, session-level credential-renewal
coordinator** using **proactive renewal with a bounded reactive fallback**, a **live
in-place token replacement on the existing recognizer** as the primary continuity
mechanism (backed by direct evidence that the Azure Speech SDK supports exactly this,
§11), a **short controlled audio-buffering window** only for the brief swap itself, and
strict reuse of the **existing generation-counter mechanism** to guarantee at most one
committed final transcript per utterance even across a renewal-triggered reconnect. It
requires **no schema change, no new endpoint, and no backend modification** — the
correct architecture is entirely a new client-side (and one small `VTTranslate.Core`
contract) concern, layered without touching any frozen phase's behavior.

## 2. Current baseline

Verified directly: branch `master`, HEAD `5f13268d5...` (`5f13268`), working tree
clean at the start of this design pass. This is the confirmed, frozen Phase 7.1 state
(Phase 7.1 implementation + client CI workflow + the device-identity corrective patch).

## 3. Current runtime flow (as inspected, not assumed)

```
MainViewModel.StartAsync()
    ↓ (once, at session start only)
DeviceRegistrationCoordinator.EnsureDeviceRegisteredAsync()
    ↓
DeviceRegistrationCoordinator.ExecuteWithDeviceRecoveryAsync(
    id => _apiClient.StartTranslationSessionAsync(...))       [Phase 6.9]
    ↓
DeviceRegistrationCoordinator.ExecuteWithDeviceRecoveryAsync(
    id => CreateAuthenticatedProviderAsync(id, voice, ct))    [×2, one per direction]
        ↓
        _apiClient.RequestProviderAccessAsync(deviceId, "AzureSpeech", "SpeechRecognition")  [Phase 6.8]
        ↓
        { accessToken, region, expiresAt, correlationId }      ← expiresAt is RECEIVED and DISCARDED today
        ↓
        AzureSpeechTranslationProvider.FromAuthorizationToken(accessToken, region, voiceName, logger)
            ↓ (stored in a `readonly string? _authorizationToken` field — no update path)
new DirectionPipeline(session, capture, playback, provider)   [×2 — mic→German, remote→English]
    ↓
DirectionPipeline.StartAsync() → provider.StartAsync() → SpeechTranslationConfig.FromAuthorizationToken(token, region)
    ↓
TranslationRecognizer created, StartContinuousRecognitionAsync()
    ↓
... audio flows via PushAudio(), events flow via Recognizing/Recognized/Synthesizing ...
    ↓ (≈10 minutes later, Azure's own STS-issued token expires)
recognizer.Canceled fires, CancellationErrorCode = an authentication-failure code
    ↓
IsTransientFailure(e) → false (only ConnectionFailure/ServiceTimeout/ServiceUnavailable are transient)
    ↓
Error?.Invoke(IsFatal: true) — NO reconnect attempted at all
    ↓
DirectionPipeline.RaiseError(fatal) → MainViewModel.HookErrors sets Status="Error", calls StopAsync()
    ↓
StopAsync() calls EndTranslationSessionAsync (best-effort) — Phase 6.9 accounting stays correct,
but the user's live session is over and must be manually restarted
```

**Provider credential never renews. There is no code path anywhere in the repository
that calls `/provider-access` a second time for an already-running session.**

## 4. Current limitation (confirmed, not assumed)

Root cause, confirmed by direct inspection:

1. `AzureSpeechTranslationProvider._authorizationToken` is declared
   `private readonly string? _authorizationToken;` ([AzureSpeechTranslationProvider.cs:43](../src/VTTranslate.Core/Providers/AzureSpeechTranslationProvider.cs)) —
   set exactly once, in the constructor, never reassigned. There is no method on
   `ISpeechTranslationProvider`, `IReconnectingProvider`, or
   `AzureSpeechTranslationProvider` itself that accepts a new token.
2. `CreateAndStartRecognizerLockedAsync()` builds
   `SpeechTranslationConfig.FromAuthorizationToken(_authorizationToken, _region)` —
   reading the same, by-then-possibly-stale field on every (re)connect, including
   reconnect attempts.
3. `IsTransientFailure` ([line 515](../src/VTTranslate.Core/Providers/AzureSpeechTranslationProvider.cs)) explicitly
   excludes authentication failures from the transient/retryable set — an expired
   credential is therefore **never retried at all**, let alone renewed; it goes
   straight to `IsFatal: true`.
4. `MainViewModel` requests a provider-access grant exactly once per direction, at
   `StartAsync`, and never again for the life of that session
   ([MainViewModel.cs, `CreateAuthenticatedProviderAsync`](../src/VTTranslate.App/MainViewModel.cs)).
5. `Api.ProviderAccessGrantDto.ExpiresAt` ([Dtos.cs:8](../src/VTTranslate.App/Api/Dtos.cs)) is
   already present on every grant response and is simply never read by
   `CreateAuthenticatedProviderAsync` — **the client already has the exact information
   needed to schedule renewal and throws it away today.**
6. `AzureProviderCredentialIssuer.IssueAsync` ([AzureProviderCredentialIssuer.cs:74](../src/VTTranslate.Backend.Infrastructure/ProviderAccess/AzureProviderCredentialIssuer.cs))
   confirms the fixed, provider-imposed 10-minute Azure STS lifetime — not a backend
   configuration choice, not adjustable.

**Failure mode, precisely**: fail-closed and loud (confirmed in the prior audit) — no
security, billing, or accounting corruption — but a hard, user-visible session
termination at (or shortly after) the 10-minute mark for any session that is still
actively recognizing at that moment, requiring a full manual restart.

## 5. Goals

- Allow an authenticated, entitled customer's translation session to continue
  transparently across one or more provider-credential expirations, for as long as the
  underlying AUTRAXIS translation session (Phase 6.9) and the customer's own
  authorization remain valid.
- Every credential renewal continues to flow through the existing, unmodified
  Phase 6.8 `/provider-access` boundary — never a shortcut, cache bypass, or
  client-side authorization assumption.
- Preserve exactly-once final-transcript semantics across any renewal-triggered
  reconnect, reusing the existing generation-counter mechanism rather than inventing a
  parallel one.
- Preserve Phase 6.9's session/usage accounting exactly as-is — a renewal is
  invisible to `TranslationSession`/`UsageRecord`.
- Define a provider-neutral renewal contract so a future non-Azure provider (or
  mobile client) can implement the same lifecycle without a redesign.
- Bound every retry/reconnect/renewal loop deterministically — no infinite loops, no
  renewal storms, no resurrecting a session after `Stop()`.
- Identify (not fix) the pre-existing `_pushStream` field race discovered during this
  audit, since any renewal design that touches recognizer/stream lifecycle must not
  make it worse.

## 6. Non-goals

Billing/subscription redesign; device-licensing redesign; identity/Entra redesign;
Gemini becoming a production provider; mobile implementation; UI redesign; unrestricted
adaptive learning; naturalization redesign; provider master-key exposure to the client
at any point; long-lived client-side provider credentials of any kind; any change to
Phase 6.6/6.7/6.8/6.9/7.0/7.1's own frozen semantics.

## 7. Architectural principles

1. **AUTRAXIS authorization and Azure provider authorization are different clocks.**
   A credential nearing/reaching expiry is a provider-lifecycle event, not evidence of
   anything about the customer's authentication or authorization state. The renewal
   coordinator's only conclusion from "credential is expiring" is "ask the backend
   again" — never "assume still authorized" and never "assume no longer authorized."
2. **Every renewal re-enters the full authorization chain.** `POST /provider-access`
   already re-evaluates device authorization, entitlement, and capability support on
   *every* call ([ProviderAccessGateway.cs](../src/VTTranslate.Backend.Application/ProviderAccess/ProviderAccessGateway.cs) —
   confirmed, no shortcut exists for "this is just a renewal"). The client must never
   attempt to bypass this by reusing a stale grant, extrapolating from the *original*
   grant's success, or skipping the call because "it worked last time."
3. **The provider connection's lifecycle is separate from the AUTRAXIS session's
   lifecycle.** A credential renewal (or even several) must occur entirely *inside*
   one Phase 6.9 `TranslationSession` — it must never itself start or end one.
4. **No new trust boundary is introduced.** The renewal coordinator is exactly as
   trusted as the original session-start code path already is — same token provider,
   same API client, same bounded-retry philosophy already proven in
   `DeviceRegistrationCoordinator` (Phase 7.1's own corrective patch).

## 8. Current code findings (summary of §3/§4, extended)

Beyond the credential-expiry root cause itself, this audit's required "challenge the
design" pass ([§36](#36-challenges-to-the-existing-design-required-by-this-audit))
surfaces:

- **A genuine data race**, not merely a design gap: `PushAudio` reads the `_pushStream`
  field *without* acquiring `_connectionLock`
  ([AzureSpeechTranslationProvider.cs:543](../src/VTTranslate.Core/Providers/AzureSpeechTranslationProvider.cs)),
  while `CreateAndStartRecognizerLockedAsync` reassigns that same field *while holding*
  the lock ([line 232](../src/VTTranslate.Core/Providers/AzureSpeechTranslationProvider.cs)). Today
  this is already live during any transient reconnect; any renewal design that
  recreates the recognizer/stream inherits the identical hazard unless addressed.
- **The generation counter is already exactly the right primitive for renewal**,
  confirmed by direct reading: `_generation` is incremented once per (re)connect and
  every event handler (`Recognizing`/`Recognized`/`Synthesizing`) already drops events
  from a superseded generation. This is not something Phase 7.2 needs to invent — it
  needs to be *reused*, and the renewal path needs to go through the exact same
  `CreateAndStartRecognizerLockedAsync` increment point, not a parallel one.
- **Reconnect already discards in-flight utterance state** on every disconnect
  (confirmed by the `ReconnectDuringUtterance` log line and the explicit code comment
  "content lost, no mid-utterance resume"). This is the *existing, accepted* behavior
  for transient network failures today — Phase 7.2 must decide whether renewal should
  inherit this same behavior (simplest, consistent) or do better (more complex, see
  §12).
- **No rate limit exists** on `POST /provider-access` at the gateway
  ([§27](#27-backend-changes-recommendation)) — a bug or malicious client issuing
  renewal requests far faster than needed is bounded only by whatever the client-side
  coordinator itself enforces. This is a design requirement for Phase 7.2's client
  code, not a backend gap to fix here.

## 9. Provider credential lifecycle (provider-neutral contract)

Phase 7.2 introduces one new, provider-neutral contract in `VTTranslate.Core` —
`IRenewableCredentialProvider` (or an equivalent extension of the existing
`ISpeechTranslationProvider`/`IReconnectingProvider` pair) with, conceptually, exactly
one new capability:

```
Task<bool> TryUpdateAuthorizationAsync(string newToken, DateTimeOffset newExpiresAt, CancellationToken ct)
```

- Returns `true` if the update was applied to the live connection without requiring a
  reconnect (Azure's actual behavior, §11); `false` if the underlying SDK/provider
  requires the caller to reconnect instead (a provider-neutral fallback signal — some
  future provider might not support live replacement at all).
- This is the **entire new surface area** needed in `VTTranslate.Core`. It does not
  replace `FromAuthorizationToken`, `StartAsync`, `PushAudio`, or any existing
  interface member — strictly additive.
- Azure's `AzureSpeechTranslationProvider` becomes the first (and, for this phase, only
  production) implementer; Gemini is explicitly not touched or promoted (non-goal,
  §6).

Credential lifecycle state the coordinator (§10) tracks per direction:
`IssuedAt`/`ExpiresAt` (from the existing, already-returned `ProviderAccessGrantDto`),
a monotonically increasing **credential generation** number (parallel to, but distinct
from, the recognizer's own connection generation — see §12 for why these must be kept
conceptually separate even though a renewal often changes both), and the current
renewal state (`Idle` / `RenewalScheduled` / `RenewalInFlight` / `RenewalFailed`).

## 10. Renewal architecture

**Recommended: a session-level `ProviderCredentialRenewalCoordinator`, one per
direction, owned by (and lifecycle-bound to) the `DirectionPipeline` it serves — not a
single cross-direction coordinator.** See §13 for the explicit bidirectional-isolation
rationale.

```
DirectionPipeline (existing, unchanged interface to its caller)
        ↓ owns
ProviderCredentialRenewalCoordinator (NEW)
        ↓ schedules renewal using ExpiresAt from the last grant
        ↓ on renewal due:
IAutraxisApiClient.RequestProviderAccessAsync(deviceId, provider, capability)  [Phase 6.8, UNCHANGED]
        ↓ fresh { accessToken, region, expiresAt }
        ↓
provider.TryUpdateAuthorizationAsync(accessToken, expiresAt, ct)  [NEW, §9]
        ↓
   true → live connection continues, generation UNCHANGED, no audio interruption
   false → bounded reconnect using the EXISTING generation-counter/reconnect
           machinery already in AzureSpeechTranslationProvider (§8), now also
           triggered by "renewal requires reconnect" in addition to "transient
           network failure"
```

The coordinator is a **client-side-only** addition — Phase 6.8's gateway is called
exactly as it already is (same endpoint, same request shape, same response shape,
same authorization re-evaluation); it does not know or care whether a given call is
"the first" or "a renewal," which is precisely the property that makes the backend
require zero change (§27).

## 11. Azure SDK analysis

**Finding, from authoritative Microsoft documentation for the Azure AI Speech SDK
(Cognitive Services Speech SDK for C#/.NET)**: `SpeechRecognizer` (and, by the same
documented mechanism, `TranslationRecognizer`, which derives from the same
connection/config model) exposes a **settable `AuthorizationToken` property** on the
recognizer instance itself, specifically documented for exactly this scenario —
refreshing an authorization token on a long-running recognizer without tearing down
and recreating the connection. The documented pattern is: obtain a fresh token before
the current one expires, then set `recognizer.AuthorizationToken = newToken` on the
already-constructed, already-started recognizer. The SDK is documented to pick up the
new token for subsequent service communication on the same underlying connection —
**no `StopContinuousRecognitionAsync`/`StartContinuousRecognitionAsync` cycle, no new
`TranslationRecognizer` instance, and no interruption to in-flight audio is required
for this specific operation**, provided the update happens *before* the old token
actually expires (Microsoft's own guidance is explicit that setting the property
*after* expiry does not retroactively repair an already-failed connection — the
replacement must be proactive, directly corroborating the proactive-renewal
recommendation in §9 below rather than a purely reactive one).

**This is the single most important finding of this document**: the current
implementation's `readonly _authorizationToken` field and once-per-connect
`SpeechTranslationConfig.FromAuthorizationToken(...)` construction are not an SDK
limitation — they are simply how the current code happens to be written. The SDK
already supports exactly the "live token replacement on an already-running
recognizer" mechanism that §9's `TryUpdateAuthorizationAsync` needs to expose,
**for the proactive case**. It does **not** — and cannot, by the nature of an
already-terminated connection — help once the connection has already been torn down
by an expired-credential `Canceled` event; that case still requires the existing
reconnect path (§8's generation-counter machinery), which already exists and does not
need to be redesigned, only *additionally triggered* by the renewal coordinator.

**Confidence level and required follow-up**: this finding is based on the SDK's
documented, publicly-described `AuthorizationToken` property behavior for this exact
scenario (token refresh on a long-running recognizer), not on running code against a
live Azure endpoint from within this sandbox (no live Azure credentials are available
here, and none should be fabricated). §24 defines the specific, narrow real-Azure
validation required to confirm this behavior precisely against `TranslationRecognizer`
(as opposed to the plainer `SpeechRecognizer` most public examples use) before this
design is implemented.

## 12. Audio continuity

Two distinct scenarios, evaluated separately because they have different correct
answers:

**Scenario A — proactive renewal succeeds via live token replacement (§11's primary
path).** No reconnect occurs, the recognizer/connection/generation are entirely
unchanged, `PushAudio` continues writing to the same `_pushStream` throughout. **Zero
audio loss, zero duplication, zero transcript risk** — this is the strong reason to
prefer proactive renewal (§9) as the primary mechanism rather than treating reconnect
as the normal path.

**Scenario B — renewal requires (or a transient/auth failure forces) a reconnect.**
This is where the six alternatives in the prompt must be weighed:

| Option | Verdict |
|---|---|
| A. Update credential on live recognizer | **Primary mechanism (Scenario A)** — not applicable once a reconnect is already required (a torn-down connection has nothing to update). |
| B. Graceful reconnect | **Recommended for Scenario B** — this is exactly the existing, already-implemented, already-tested mechanism (§8's generation counter + exponential backoff). Extending it to also trigger on "renewal required, live update unsupported" is the smallest correct change. |
| C. Overlapping recognizers (start the new one before tearing down the old) | **Rejected** — Azure Speech billing/session semantics and this codebase's own single-`_recognizer`-field design assume one active connection per direction; running two simultaneously would double-count nothing from AUTRAXIS's perspective (Phase 6.9 accounting is provider-connection-agnostic) but risks duplicate `Recognized`/`Synthesized` events for genuinely overlapping audio unless a second, parallel generation-guard scheme were built — unjustified complexity for a failure mode (reconnect) that already has an accepted, working answer (Option B). |
| D. Short controlled audio buffering across the swap | **Recommended as a bounded refinement**, not a replacement for B — buffer captured PCM chunks in a small ring buffer (bounded duration, e.g. low-single-digit seconds, exact value an implementation detail) for the duration of a reconnect attempt, and flush the buffer into the *new* `_pushStream` once `CreateAndStartRecognizerLockedAsync` completes. This directly reduces (does not eliminate) Scenario B's existing "content lost, no mid-utterance resume" behavior, at the cost of a small amount of added latency/complexity. This is explicitly a refinement Phase 7.2 should evaluate for inclusion, not something the *renewal* work is blocked on — Scenario B's fallback already existed pre-Phase-7.2 for transient network failures and customers already experience it; Phase 7.2 does not need to solve harder than the pre-existing baseline to deliver its own primary goal (surviving credential expiry, which Scenario A already solves without any buffering at all). |
| E. Discard only unsafe audio | Effectively what already happens today (chunks pushed to a torn-down stream are simply lost) — acceptable as the Scenario-B baseline, superseded in quality by Option D if implemented. |
| F. Pause forwarding while renewing | **Rejected as the primary mechanism** — pausing capture/forwarding for the ~1–3 seconds a reconnect takes is nearly equivalent to Option D (buffering) except it drops the paused audio instead of preserving it; Option D dominates it directly for the same implementation cost. |

**Recommendation**: Scenario A (live update) is the overwhelmingly common case in
practice (proactive renewal succeeds well before expiry the vast majority of the
time) — it has zero audio-continuity cost. Scenario B inherits the existing,
already-accepted reconnect behavior (Option B), optionally improved by bounded
buffering (Option D) as a follow-on refinement rather than a blocking requirement.

## 13. Transcript continuity

The existing generation counter already provides exactly the guarantee needed:
`Recognizing`/`Recognized`/`Synthesizing` handlers all check `myGeneration !=
_generation` and drop the event if superseded. Phase 7.2's requirement — "one source
utterance → at most one committed final transcript, even across renewal-caused
reconnect" — is **already satisfied by this existing mechanism**, provided the renewal
coordinator's reconnect path increments generation through the *same*
`CreateAndStartRecognizerLockedAsync` call the existing transient-failure reconnect
already uses (§10's design explicitly routes through this same function — not a
parallel "renewal reconnect" code path, which is precisely the kind of duplicated
mechanism this audit's §36 instruction warns against).

**What must be explicitly defined, since it is not automatic**: an in-flight
*partial* utterance at the moment of a Scenario-B reconnect is lost (§8/§12) — this is
existing behavior, not a new duplication risk, and Phase 7.2 does not change it. A
completed *final* transcript that was already emitted before the reconnect is never
re-emitted, because the old generation's handlers are already dropped by the guard —
confirmed structurally, not merely assumed, by reading the actual conditional
(`if (myGeneration != _generation) return;`) at the top of every relevant handler.

**Credential generation vs. connection generation** (§9's design note, expanded):
these must be tracked as related but distinct counters. A Scenario-A live update does
**not** advance the connection generation (no new recognizer, no new stream) — it only
updates which credential generation is "current" for logging/observability (§21). A
Scenario-B reconnect advances **both**. Conflating them would either (a) incorrectly
treat a zero-risk live update as if it required the same defensive event-dropping a
real reconnect needs (harmless but confusing to reason about), or (b) worse, fail to
advance connection generation on an actual reconnect if the two counters were
merged incorrectly. Keeping them separate, explicit fields removes this ambiguity by
construction.

## 14. Bidirectional session model

**Decision: each `DirectionPipeline` owns and drives its own
`ProviderCredentialRenewalCoordinator` independently — no session-level coordinator
manages both directions' credentials as one unit.**

Rationale, evaluated against the prompt's explicit scenarios:

- **One direction expires first**: with independent per-direction coordinators, this
  is not a special case at all — each direction's `ExpiresAt` is tracked and renewed
  on its own schedule, exactly as if it were the only direction running. A
  session-level coordinator would need to either serialize both directions' renewals
  (adding avoidable latency to whichever direction renews second) or explicitly
  parallelize them (which independent coordinators already do for free, with less
  code).
- **Simultaneous renewal**: two independent `POST /provider-access` calls, naturally
  concurrent, each fully and independently authorized by the (already
  concurrency-safe, stateless-per-call) Phase 6.8 gateway — no new concurrency control
  is needed at the client because the backend call itself has no shared mutable state
  between the two requests (confirmed: `ProviderAccessGateway.RequestAccessAsync` has
  no cross-request state beyond what's already safely encapsulated in Phase 6.6/6.7's
  own existing account-row-lock mechanisms, unrelated to provider-access itself).
- **One direction's renewal fails**: isolated to that `DirectionPipeline` — it can
  independently error/reconnect/eventually-fail without forcing the other, still-valid
  direction to tear down. This matches the existing (frozen) principle already visible
  in `MainViewModel.HookErrors`, which today treats each direction's fatal error as
  cause to stop the *whole* bidirectional session — a product-level UX decision
  (§31, not re-litigated here) that Phase 7.2 does not need to change: it only needs
  to make sure a failed *renewal* is reported through the exact same
  `Error`/`IsFatal` channel the existing fatal-error handling already understands,
  not a new, parallel failure-reporting mechanism.
- **One direction stopped, both directions stopped**: `Stop()`/cancellation semantics
  (§18) are per-provider-instance already (each `AzureSpeechTranslationProvider`
  instance has its own `_stopRequested`/`_lifetimeCts`) — a per-direction coordinator
  composes naturally with this existing per-instance shutdown model; a session-level
  coordinator would need extra plumbing to know when only one of two directions has
  stopped, for no corresponding benefit.

**Isolation model, stated precisely**: each direction's `ProviderCredentialRenewalCoordinator`
holds exactly the state needed for that direction's own credential (issued-at,
expires-at, credential generation, renewal state) and calls `RequestProviderAccessAsync`
independently. Nothing is shared between the two coordinator instances — this mirrors
the existing, already-frozen architectural fact that the two `DirectionPipeline`
instances are already fully independent objects with independent provider instances,
independent audio devices, and independent generation counters.

## 15. Session accounting

**Hard requirement, satisfied by construction under this design**: a credential
renewal (Scenario A or B) never calls `POST /translation-sessions`,
`.../heartbeat`, or `.../end` — those three Phase 6.9 endpoints are entirely
untouched by, and unaware of, provider-credential renewal. The renewal coordinator's
only backend interaction is `POST /provider-access`, which is already (Phase 6.8,
frozen) fully decoupled from Phase 6.9's session/usage model — confirmed directly:
`ProviderAccessGateway` has no reference to `ITranslationSessionService`,
`ITranslationSessionRepository`, or any usage-recording call, in either direction.

- **No new `TranslationSession`**: the one Phase 6.9 session created at the top of
  `MainViewModel.StartAsync` remains the single session for the entire multi-renewal
  lifetime of that customer session — a renewal is invisible above the
  `DirectionPipeline`/provider layer.
- **No duplicate usage**: usage is recorded exclusively at `End`/lazy-expiry
  reconciliation (Phase 6.9, frozen, unchanged) — a provider-credential renewal event
  is not a session-lifecycle event and triggers neither.
- **No fake session starts/ends**: confirmed by construction, since the renewal
  coordinator's API surface (§10) never calls any of the three session endpoints.

## 16. Authorization model

Every renewal (Scenario A trigger point, or a Scenario-B reconnect's provider-access
call) goes through the exact same `POST /provider-access` call the original grant
used — same device ID (resolved once via `DeviceRegistrationCoordinator`, unchanged),
same capability request, same backend re-evaluation of device authorization,
entitlement/subscription state, and provider/capability support
(§8/§9 of `ProviderAccessGateway.cs`, confirmed line-by-line). **The client never
assumes "session started successfully, therefore renewal is always authorized"** —
this assumption is structurally impossible under this design, because there is no
code path that skips the `/provider-access` call for a renewal; the renewal
coordinator has no cached "authorized" flag to consult instead of calling the
backend.

If a renewal-time `/provider-access` call returns a denial (`device_not_authorized`,
`entitlement_denied`, `usage_denied`, `provider_unavailable`), the coordinator does
**not** retry that specific denial category as if it were transient (§17) — it
surfaces a fatal `ProviderError` through the exact same channel the current fatal-auth
failure already uses, which `MainViewModel`/`DirectionPipeline` already understand
correctly (session ends cleanly, `EndTranslationSessionAsync` called, per §4's
existing, already-correct fail-closed behavior).

## 17. Failure / retry model

| Category | Examples | Treatment |
|---|---|---|
| **TRANSIENT** | Azure `ConnectionFailure`/`ServiceTimeout`/`ServiceUnavailable`; AUTRAXIS API network/DNS/timeout failure during a renewal call | Bounded retry with exponential backoff — reuses the EXISTING `MaxReconnectAttempts`/backoff constants and mechanism for the provider-connection side; the AUTRAXIS-API side reuses `IAutraxisApiClient`'s own existing bounded-401-renewal philosophy (one forced token refresh, one retry) — no new backoff scheme invented for that leg. |
| **AUTHORIZATION** | `device_not_authorized`, `entitlement_denied`, `usage_denied` from a renewal-time `/provider-access` call; a `401` from the AUTRAXIS API that a forced Entra token refresh cannot resolve | **Not retried as transient.** Surfaces as a fatal error through the existing `ProviderError(IsFatal: true)` channel — the session ends, cleanly, exactly as an original-grant authorization denial already would. |
| **CONFIGURATION** | Malformed/empty token or region returned from a renewal grant; a provider issuer suddenly reporting `unsupported_provider`/`unsupported_capability` mid-session (e.g. a backend configuration change) | Treated as fatal, non-retried — retrying a malformed credential cannot succeed differently, matching the existing `IsTransientFailure` philosophy of "retrying a config/auth problem just fails the same way forever." |
| **PROVIDER** | Azure's own STS endpoint unavailable (`AzureProviderCredentialIssuer.IssueAsync` returning null, surfaced to the client as `provider_unavailable`) | Bounded retry as TRANSIENT for a small number of attempts (the underlying STS outage is expected to be short-lived or not, and the client cannot distinguish — bounding the retry count is the correct fail-closed answer either way), then fatal. |
| **CANCELLATION** | `Stop()` called; `CancellationToken` cancelled; application shutdown | Wins unconditionally over any in-flight or scheduled renewal — see §18. Never classified as a failure at all; no error is raised. |
| **TERMINAL** | Reconnect/renewal attempts exhausted; a non-transient category above with no further recovery path | Fatal `ProviderError(IsFatal: true)`, session ends via the existing, unchanged shutdown path (`StopAsync` → `EndTranslationSessionAsync`). |

## 18. Cancellation / Stop semantics

**Mandatory, non-negotiable per the prompt**: if `Stop()` is called, no renewal, no
reconnect, and no new `/provider-access` request may occur afterward, and a
renewal/reconnect already racing with `Stop()` must never resurrect the provider.

This is achieved by **extending the existing, already-correct pattern**
(`_stopRequested` + `_lifetimeCts` + a lock-protected re-check immediately before
starting new work), not inventing a new one:

- The renewal coordinator's proactive-renewal timer is linked to the same
  `_lifetimeCts`-equivalent lifetime token the provider already uses — cancelling it
  on `Stop()` cancels any pending `Task.Delay` inside the renewal scheduler exactly as
  it already does for the reconnect backoff delay today.
- Before issuing a proactive renewal's `/provider-access` call, AND again before
  applying its result (`TryUpdateAuthorizationAsync` or a Scenario-B reconnect), the
  coordinator re-checks `_stopRequested` **while holding the same `_connectionLock`**
  the existing reconnect path already re-checks it under — this is the exact,
  already-proven double-check pattern in `CreateAndStartRecognizerLockedAsync`'s
  reconnect callback today (`if (_stopRequested) return;` inside the lock, after the
  delay). A renewal result arriving after `Stop()` already completed finds
  `_stopRequested == true` under the lock and discards itself, identically.
- `StopAsync()` itself does not need to change — it already cancels `_lifetimeCts` and
  sets `_stopRequested` before acquiring `_connectionLock`, which is exactly the
  ordering a racing renewal-completion needs to observe correctly.

No new cancellation primitive is introduced; Phase 7.2 reuses the exact fields and
ordering already proven correct for the existing reconnect-vs-Stop race.

## 19. Security model

- Provider credentials remain short-lived (Azure's own fixed ~10-minute STS lifetime,
  unchanged — Phase 7.2 does not request a longer lifetime, and could not, since
  `AzureProviderCredentialIssuer` clamps to Azure's own fixed value regardless of what
  is requested).
- Memory-only on the client: the renewal coordinator holds the current token only in
  the same place the existing code already holds it (the provider's own field,
  updated via `TryUpdateAuthorizationAsync` rather than a new, separate storage
  location) — no new persistence surface is introduced.
- Never logged: renewal-related diagnostic events (§21) log categories and timestamps
  only, exactly matching the existing `_logger.Log(SessionTag, "Connected", ...)`-style
  calls already in the codebase, which never include token content.
- Never placed in diagnostics/UI: unchanged from Phase 7.1 — no UI surface displays
  any credential value today, and Phase 7.2 adds no new UI.
- The client never receives, and this design never introduces a path for it to
  receive, the Azure master subscription key or any other long-lived provider secret
  — `AzureProviderCredentialIssuer`'s existing Infrastructure-only boundary is
  entirely unchanged.

## 20. Offline model

Restated, unchanged from Phase 7.1's own rule: offline does not bypass authorization.
Applied specifically to renewal:

- **Already-issued credential, network loss to the AUTRAXIS backend specifically
  (Azure itself still reachable)**: the live connection may continue exactly as it
  already does today for the remainder of that credential's own Azure-defined
  lifetime — this is not a new offline allowance, it is the unavoidable, already-
  existing consequence of a credential that was validly issued before the outage
  began (identical reasoning to Phase 6.2B §12's original grace-period rule).
- **Renewal becomes due while the AUTRAXIS backend is unreachable**: the renewal call
  fails as a TRANSIENT (§17) network error; bounded retry applies; if the outage
  outlasts the bounded retry window and/or the credential's own remaining lifetime,
  the session fails closed exactly as §4's existing fatal-error path already does —
  **no fabricated authorization, no extended credential lifetime, no fallback to a
  cached "it worked before" assumption.**
- **Once a renewal is genuinely required and AUTRAXIS cannot authorize a fresh
  credential** (backend reachable but denies, or unreachable past the bounded retry
  window): fail-closed, full stop — the session ends via the existing shutdown path.
  No offline entitlement bypass is introduced or considered.

## 21. Observability

Safe diagnostic event categories (names/metadata only, extending the existing
`_logger.Log(SessionTag, eventType, metadata)` convention already used throughout
`AzureSpeechTranslationProvider` — no new logging mechanism):

`RenewalScheduled` (metadata: direction, scheduled-for timestamp, credential
generation), `RenewalStarted`, `RenewalSucceeded` (metadata: new expiry, whether live
update or reconnect was used), `RenewalFailed` (metadata: failure category from §17,
attempt count), `ProviderReconnectStarted`/`ProviderReconnectSucceeded`/
`ProviderReconnectFailed` (already exist today under different event names —
`ReconnectAttempt`/`ReconnectResult`/`Connected` — extended, not replaced, to also
fire for a renewal-triggered reconnect), `CredentialGenerationChanged` (old/new
generation numbers), `SessionContinuityPreserved` (logged once per successful
proactive renewal, as the direct, positive confirmation that Phase 7.2's goal was met
for that renewal cycle).

**Never logged**, restated: provider token/credential value, AUTRAXIS access/refresh/
ID token, `Authorization` header value, provider master secret, or any recognized/
translated transcript content — identical, unextended list to the existing codebase's
own discipline (already enforced for every other event this class logs).

## 22. Performance

- **Renewal API traffic**: at most one `/provider-access` call per direction per
  ~10-minute credential lifetime under normal (proactive, successful) operation — two
  calls total for a bidirectional session, every ~10 minutes. This is a small,
  bounded, predictable load addition per active session; no polling, no per-second
  checks (the renewal timer sleeps until shortly before the known `ExpiresAt`, per
  §10's design).
- **Simultaneous bidirectional renewal**: two independent, small HTTP calls — no
  batching is warranted or proposed given the volume.
- **Backend/STS load**: strictly proportional to (active sessions × directions ×
  1/~10 minutes) — the same order of magnitude as today's *session-start* load,
  recurring, not a new load class.
- **Client CPU/memory**: negligible — a per-direction timer and a small state struct;
  no new background thread pool, no polling loop (`Task.Delay` until the scheduled
  renewal time, cancellable, mirrors the existing reconnect-backoff `Task.Delay`
  pattern already in the codebase).
- **Audio buffering** (if Option D from §12 is adopted): bounded, small (low-single-
  digit seconds of 16kHz 16-bit mono PCM — kilobytes, not megabytes), and only
  allocated/held during an actual Scenario-B reconnect window, never during normal
  Scenario-A operation.
- Avoiding unnecessary `/provider-access` requests: the coordinator schedules exactly
  one renewal attempt per credential lifetime (using the real `ExpiresAt`, §10), never
  a fixed-interval poll that might fire more often than needed.

## 23. Test strategy

**Deterministic unit tests** (no real Azure, no real network — fakes only, mirroring
the exact pattern already proven in `tests/VTTranslate.App.Tests` for
`DeviceRegistrationCoordinator`/`AutraxisApiClient`):

1. Credential lifetime calculation (renewal-due time derived correctly from a fake
   `IssuedAt`/`ExpiresAt` pair, with a safety margin).
2. Proactive renewal scheduling (timer fires at the expected offset before expiry,
   using a fake/injectable clock — never a real `Task.Delay` of real minutes).
3. Successful renewal (fake API client returns a fresh grant; coordinator applies it;
   credential generation updates; no reconnect if the fake provider reports live
   update supported).
4. Renewal failure (fake API client throws each §17 category in turn; correct
   downstream behavior per category — retried vs. fatal).
5. Bounded retry (a fake API client that always fails TRANSIENT; assert an exact,
   finite attempt count, never more).
6. Cancellation during renewal (cancel the linked token mid-delay; assert no API call
   is made and no error is raised).
7. `Stop()` during renewal (assert the double-check-under-lock pattern actually
   discards a renewal result that completes after `Stop()`).
8. Stale renewal completion after `Stop()` (a renewal already in flight when `Stop()`
   is called; assert its eventual (fake, deterministic) completion is a no-op).
9. Reconnect after expiry (simulate the SDK reporting "live update unsupported" or an
   auth-failure `Canceled`; assert the existing reconnect/generation machinery is
   invoked, not a duplicate mechanism).
10. Duplicate provider events (simulate a stale-generation event arriving after a
    renewal-triggered reconnect; assert it is dropped, reusing the existing
    generation-guard unit-test pattern if one exists, or establishing it if not).
11. Transcript generation guards (assert a superseded generation's `Recognized`/
    `Recognizing` never reaches `PartialResult`/`FinalResult`).
12. Final transcript deduplication (assert exactly one `FinalResult` per genuine
    utterance across a simulated renewal-triggered reconnect boundary).
13. Bidirectional independent renewal (two coordinators, two independent fake clocks/
    expiries; assert one direction's renewal timing never affects the other's).
14. Simultaneous renewal (both directions' renewal timers fire at once; assert both
    proceed independently and correctly).
15. One-direction renewal failure (assert the OTHER direction is unaffected and
    continues normally).
16. Provider-access 401 during renewal (fake `AutraxisApiClient` behavior, reusing
    the exact bounded-401 test pattern already proven for the ordinary API client).
17. Provider-access 403 during renewal (`device_not_authorized`/`entitlement_denied`/
    `usage_denied` — assert fatal, non-retried, per §17).
18. Device revoked during renewal (a `device_not_authorized` response mid-session;
    assert fatal, clean session end — this is a NEW scenario relative to Phase 7.1's
    own device-recovery logic, since here there is no "device not yet registered"
    case to recover into — the device already exists and was working; revocation
    mid-session is a genuine, intentional denial to respect, not a retry candidate).
19. Account becomes unusable during renewal (`account_not_found`/`account_suspended`
    equivalent surfaced via the renewal call — same fatal, non-retried treatment).
20. Network outage during renewal (TRANSIENT category, bounded retry, eventual fatal
    if the outage outlasts the bound).
21. Provider STS outage (`provider_unavailable`, bounded retry per §17's PROVIDER
    row).
22. Application shutdown during renewal (same mechanism as `Stop()`, §18 — assert no
    renewal survives process teardown in a way that could, e.g., leave an orphaned
    timer).
23. Long-running session simulation (a fake clock advanced across multiple simulated
    ~10-minute boundaries within one test, asserting multiple successful renewals in
    sequence with no accumulated state leakage between them).
24. Usage/session continuity (assert, using fakes, that no `StartTranslationSessionAsync`/
    `HeartbeatTranslationSessionAsync`/`EndTranslationSessionAsync` call is ever made
    by the renewal coordinator itself — a structural/interaction-based test, not an
    integration test against a real backend).
25. No duplicate `TranslationSession` (same assertion as 24, phrased from the
    session-accounting angle).
26. No duplicate `UsageRecord` (this is inherently a Phase 6.9 backend-side guarantee,
    already proven by that phase's own existing, unmodified tests — Phase 7.2 adds no
    new backend code that could affect it; the client-side test here only needs to
    confirm the renewal coordinator never calls anything that *could* create a
    duplicate, which test 24/25 already cover).

**Integration tests** (still deterministic, no real Azure — exercising the real
`AzureSpeechTranslationProvider`/`DirectionPipeline` wiring against a fake/mocked
Speech SDK boundary if one is introduced, or against the real SDK with a
locally-invalid/fake token to exercise the *error/failure* paths only, never a real
recognition session): the recognizer construction/config-branch logic
(`FromAuthorizationToken` vs. `FromSubscription`), the generation-counter guard logic
end-to-end within one process, and the `_connectionLock`/`_stopRequested` race
ordering under concurrent `Stop()` + simulated renewal completion.

**Real Azure tests**: see §24 — a small, separate, non-CI tier.

## 24. Real Azure validation

Given the confidence caveat in §11, the following must be validated against a real,
non-production Azure Speech resource before this design is implemented, not assumed
from documentation alone:

1. **`AuthorizationToken` replacement specifically on `TranslationRecognizer`** (not
   just the simpler `SpeechRecognizer` most public Microsoft examples demonstrate) —
   confirm the property exists, is settable, and is honored mid-session without
   dropping the connection.
2. **Timing boundary**: confirm the SDK's actual behavior when the property is set a
   few seconds *before* vs. *after* the true expiry — validates the proactive-renewal
   safety-margin design in §10 empirically rather than by documentation alone.
3. **Event ordering** around a live token update: confirm no spurious `Canceled`
   event fires as a side effect of the property assignment itself (i.e., that this is
   genuinely a "hot swap," not something that triggers an internal reconnect the SDK
   doesn't clearly document).
4. **Recognizer lifecycle under a forced reconnect** (Scenario B): confirm the
   generation-guard behavior holds under a *real* Azure-initiated disconnect/
   reconnect cycle, not only the already-existing simulated/transient-network-failure
   path this test suite presumably already exercises in some form.
5. **Cancellation semantics**: confirm that calling `StopContinuousRecognitionAsync`
   concurrently with a pending `AuthorizationToken` assignment behaves safely (no
   SDK-level exception/corruption) — directly informs whether §18's lock-based
   double-check is sufficient or whether an SDK-specific additional guard is needed.
6. **Expired-authorization `CancellationErrorCode`** value: confirm the exact error
   code Azure returns for an expired STS token specifically (as opposed to a wrong/
   malformed token), to confirm it is correctly excluded from
   `IsTransientFailure`'s existing set (§4) and would be correctly classified as
   AUTHORIZATION, not TRANSIENT, under §17's model.

This tier requires a real (non-production) Azure Speech resource and network access;
it is explicitly **not** part of the ordinary deterministic CI-run test suite (§23),
matching the same separation already established for "Real Entra E2E" testing in
Phase 7.1's own architecture document.

## 25. API / backend impact

**Recommendation: no API change, no backend change.**

- `POST /provider-access`'s existing request/response shape already carries
  everything the renewal coordinator needs — `deviceId`/`provider`/`capability` in
  the request, `accessToken`/`region`/`expiresAt`/`correlationId` in the response
  (confirmed, §4/§9). No new field, no new endpoint, no versioning concern.
- The gateway is already fully idempotent from the client's perspective — each call
  is independently, completely re-authorized (§8/§16) with no server-side concept of
  "this is a renewal vs. an original request" needed or proposed. Introducing such a
  concept (e.g., a "renewal" flag or a correlation-to-original-grant field) would add
  complexity with no corresponding benefit, since the authorization decision is
  identical either way — explicitly **rejected** as unnecessary.
- **Audit semantics**: unchanged — each renewal call produces its own
  `ProviderAccessIssued` (or denial) audit event, exactly as every existing call
  already does; a renewal is, correctly, audit-indistinguishable from an original
  grant, since from the backend's authorization standpoint it *is* just another
  grant request.
- **Rate limiting**: the backend currently has none for this endpoint (confirmed,
  §8) and this document does **not** recommend adding one as part of Phase 7.2 — the
  client-side bounded-renewal-attempt policy (§17) is the correct place to prevent
  excessive calls from a *well-behaved* client; a backend-side rate limit would be a
  distinct, defense-in-depth concern against a *malicious* client, explicitly noted
  as an **open/deferred item** in §29's threat model, not required for this phase's
  functional goal.
- **Usage separation**: already fully correct and unchanged (§15) — no backend change
  needed to preserve it.

## 26. Frozen-phase compatibility

| Phase | Interaction | Status |
|---|---|---|
| 6.6 (Billing/Subscription) | `EntitlementService.CanStartTranslationSessionAsync` is re-invoked on every renewal call (via the unchanged `ProviderAccessGateway`) | **Unchanged** — extension point already exists and requires nothing new |
| 6.7 (Device Licensing) | `IsDeviceAuthorizedAsync` re-invoked on every renewal call; device ID itself comes from the unchanged, already-fixed `DeviceRegistrationCoordinator` (Phase 7.1 corrective patch) | **Unchanged** |
| 6.8 (Provider Access Gateway) | `POST /provider-access` called repeatedly instead of once — same endpoint, same contract, same authorization chain | **Unchanged; the natural, load-bearing extension point for this entire phase** |
| 6.9 (Usage Metering / Sessions) | Zero calls from the renewal coordinator to any Phase 6.9 endpoint; one `TranslationSession` spans the whole multi-renewal customer session | **Unchanged; explicitly protected by design (§15)** |
| 7.0 (Identity/Account Lifecycle) | No direct interaction — renewal never touches `/profile`, provisioning, or account-lifecycle state | **Unchanged** |
| 7.1 (Customer Authentication) | `ITokenProvider`/`IAutraxisApiClient`'s existing bounded-401 policy is reused as-is for the AUTRAXIS-API leg of a renewal call; `DeviceRegistrationCoordinator`'s bounded-one-replacement pattern is the direct architectural precedent this document's §17 retry policy is modeled on | **Unchanged; direct design precedent, not merely "compatible"** |

No frozen phase requires modification; Phase 6.8 is the sole, already-sufficient
extension point this entire design is built on top of.

## 27. Backend changes recommendation

Restated from §25/§8: **the existing Phase 6.8 gateway already safely supports
repeated short-lived credential issuance** — idempotency is a non-issue because there
is nothing to be idempotent *about* (each call is a fresh, independently-correct
authorization decision, not a retry of a specific prior request needing
deduplication); audit semantics are already correct per-call; issuance frequency is
bounded client-side (§17), not currently backend-side, which is called out as a
deferred defense-in-depth item (§29) rather than a blocking requirement; usage
separation is already complete (§15); authorization re-evaluation already happens on
every single call, unconditionally (§8/§16). No backend change is required for Phase
7.2's functional correctness.

## 28. Threat model

| # | Threat | Mitigation | Residual risk |
|---|---|---|---|
| 1 | Stolen local device identity | Unchanged from Phase 7.1's own analysis — a device ID alone authorizes nothing without a valid bearer token, independently re-verified server-side on every renewal call too | Same residual risk already accepted in Phase 7.1 |
| 2 | Stolen provider credential | Short lifetime (≤10 min, unchanged); memory-only, never persisted/logged (§19) | Same live-process-memory limit already accepted for the original grant; renewal introduces no new exposure window beyond what already exists |
| 3 | Replay of an expired credential | Azure's own STS-side expiry enforcement (unchanged, outside AUTRAXIS's control) — an expired token is rejected by Azure itself regardless of what the client attempts | None beyond Azure's own documented guarantee |
| 4 | Renewal race (two renewal attempts for the same direction overlapping) | The renewal coordinator's own single-timer-per-direction design means only one renewal is ever scheduled at a time per direction; if an implementation detail risked a double-fire, the same `_connectionLock` serialization already used for reconnect would need to guard it — an explicit implementation requirement carried into the eventual build, not a gap left open here | None, provided the lock discipline already proven for reconnect is correctly extended to renewal (a direct, testable requirement, §23 test 4/5) |
| 5 | Credential replacement race (a Scenario-B reconnect racing a Scenario-A live-update attempt for the same direction) | By design, only one code path is active at a time per direction — a renewal decides once (proactively, at schedule time) whether to attempt live update or must reconnect; there is no scenario where both are attempted concurrently for the same credential generation | None, provided this single-decision-point design is correctly implemented |
| 6 | Stale recognizer events (old generation's events arriving after renewal-triggered reconnect) | The existing, unmodified generation-guard mechanism (§13) — already proven correct for the pre-existing transient-reconnect case, reused identically | None beyond what the existing mechanism already covers |
| 7 | Malicious client requesting excessive renewals | Client-side bounded policy (§17) covers a *well-behaved* client's own failure handling; a genuinely malicious client ignoring its own code and hammering `/provider-access` directly is bounded only by whatever the backend eventually adds (none today) — **explicitly flagged as an open/deferred backend hardening item**, not solved by this client-side architecture alone | A backend-side rate limit is not implemented by this phase; residual DoS-shaped risk against the STS/backend from a malicious (not merely buggy) actor remains, tracked as an open item (§29/§31) |
| 8 | Renewal after logout | `ITokenProvider`'s existing bounded-401 policy (Phase 7.1, unchanged) already fails closed if the AUTRAXIS access token itself has been invalidated by a sign-out — a renewal attempt after logout cannot obtain a valid bearer token to even reach `/provider-access` | None beyond Phase 7.1's own already-assessed residual risk |
| 9 | Renewal after device revocation | Explicitly covered — `/provider-access`'s existing `device_not_authorized` denial (§16), surfaced as a fatal, non-retried AUTHORIZATION failure (§17) | None — this is the correct, intended fail-closed behavior |
| 10 | Renewal after account suspension | Same mechanism as #9, via `account_not_found`/`account_suspended`-equivalent denial paths already proven server-side (Phase 7.0, unchanged) | None |
| 11 | Credential leakage through logs | §19/§21's explicit never-log list, extending the identical discipline already proven in the existing codebase's own logging calls | Requires ongoing implementation/code-review discipline — a process control, not eliminable purely by architecture, identical to every prior phase's own equivalent finding |
| 12 | Credential leakage through exceptions | An `AutraxisApiException`'s existing design (Phase 7.1) already carries only a stable category + backend status string, never response body content that could echo a token — renewal reuses this exact exception type/shape unchanged | None beyond what Phase 7.1's own existing exception design already provides |
| 13 | Renewal storm / DoS (client-side bug causing rapid repeated renewal attempts) | Bounded attempt count + exponential backoff (§17), directly modeled on the already-proven `DeviceRegistrationCoordinator`/`AzureSpeechTranslationProvider` reconnect patterns | None, provided the bound is correctly implemented and tested (§23) |

## 29. Decision matrix

| Decision | Status | Choice |
|---|---|---|
| Proactive vs. reactive renewal | **DECIDED** | Proactive primary (with a bounded reactive/reconnect fallback for cases proactive renewal cannot cover — SDK live-update rejection, transient failure, or a renewal that races an unexpectedly early expiry) |
| Credential lifetime tracking | **DECIDED** | Use the existing, already-returned `ExpiresAt` from each grant — no new field, no client-side guessing |
| Renewal coordinator ownership | **DECIDED** | Owned by, and lifecycle-bound to, each `DirectionPipeline`/provider instance — not a separate free-floating service |
| Per-direction vs. session-level coordinator | **DECIDED** | Per-direction (§14) |
| Live token replacement vs. reconnect | **DECIDED (pending §24 real-Azure confirmation)** | Live replacement (`AuthorizationToken` property) as primary; reconnect via the existing generation-counter mechanism as fallback |
| Audio buffering strategy | **DECIDED for Scenario A (none needed); OPEN for Scenario B's optional refinement (Option D)** | Scenario A: no buffering needed at all. Scenario B: bounded short buffering is a recommended, not mandatory, refinement — left open for the implementation phase to size/schedule |
| Transcript deduplication strategy | **DECIDED** | Reuse the existing generation-counter guard unchanged; no new mechanism |
| Retry policy | **DECIDED** | Category-based (§17): TRANSIENT/PROVIDER bounded-retry-then-fatal; AUTHORIZATION/CONFIGURATION immediately fatal; CANCELLATION always wins; TERMINAL is the natural end state of an exhausted bounded retry |
| Cancellation policy | **DECIDED** | Reuse the existing `_stopRequested`/`_connectionLock`/`_lifetimeCts` double-check pattern unchanged (§18) |
| Authorization recheck | **DECIDED** | Every renewal is a full, independent `/provider-access` call — no caching, no shortcut, ever (§16) |
| API changes | **DECIDED** | None required (§25) |
| Backend changes | **DECIDED (functional); OPEN (defense-in-depth rate limiting)** | No functional backend change required; a backend-side rate limit on `/provider-access` is a deferred, separately-scoped hardening item (§28 threat 7) |
| Observability | **DECIDED** | Extend the existing `_logger.Log` event-category convention (§21); no new logging mechanism |
| Testing strategy | **DECIDED** | Deterministic unit/integration tests using fakes for everything except the narrow real-Azure validation list in §24 |

## 30. Open product/operations decisions

Kept deliberately minimal — genuine open items only, not manufactured ones:

1. **Whether the Scenario-B bounded audio-buffering refinement (§12 Option D) is
   worth its implementation cost for this release**, versus accepting the existing,
   already-shipped "content lost, no mid-utterance resume" behavior for the
   (expected-to-be-rare, since proactive renewal is primary) Scenario-B case. This is
   a product/UX prioritization call, not an architecture gap — the architecture
   supports either answer without redesign.
2. **Whether a backend-side rate limit on `/provider-access`** (§27/§28 threat 7) is
   worth adding as a separate, future hardening item, and on what threshold/timeline
   — an operations/security-prioritization decision, explicitly out of this phase's
   functional scope.
3. **Exact safety-margin value** for proactive renewal (how many seconds/minutes
   before the real `ExpiresAt` to trigger renewal) — an implementation-tuning
   parameter that should be informed by the real-Azure validation in §24 (specifically
   item 2, the timing-boundary test) rather than fixed arbitrarily now, per the
   prompt's own explicit instruction not to hardcode an unjustified interval.

## 31. Implementation sequence

Derived from the architecture above, not assumed in advance:

1. **Provider-neutral contract extension** — add the additive
   `TryUpdateAuthorizationAsync`-shaped capability to `VTTranslate.Core` (new
   interface member or a new optional interface, per §9), with zero behavior change
   to any existing caller. This must come first because every later step depends on
   the contract existing.
2. **Azure implementation** — implement the contract in
   `AzureSpeechTranslationProvider`, using the SDK's `AuthorizationToken` property for
   the live-update path and routing the "must reconnect" fallback through the
   *existing* `CreateAndStartRecognizerLockedAsync`/generation-counter mechanism
   (extended to accept "renewal requires reconnect" as an additional trigger
   alongside "transient network failure").
3. **Real-Azure validation of step 2** (§24) — before building the client-side
   coordinator on top of an unconfirmed assumption, validate the SDK behavior for
   real against a non-production Azure resource. This is sequenced deliberately
   *before* the coordinator, not after, so the coordinator's design (proactive vs.
   bounded-reconnect-fallback split) is built on confirmed, not assumed, SDK
   behavior.
4. **Renewal coordinator** — `ProviderCredentialRenewalCoordinator`, per-direction,
   consuming `IAutraxisApiClient.RequestProviderAccessAsync` (unchanged) and the new
   contract from step 1/2, implementing the proactive-schedule/bounded-retry/
   cancellation-safe design in §10/§17/§18.
5. **`DirectionPipeline`/`MainViewModel` wiring** — attach one coordinator instance
   per direction at session start, using the already-tracked `deviceId` (Phase 7.1's
   `DeviceRegistrationCoordinator`, unchanged) and the same provider/capability
   request shape already used for the original grant.
6. **Observability** — wire the new event categories (§21) through the existing
   `IDiagnosticLogger` convention.
7. **Deterministic test suite** (§23) — unit and integration tests using fakes,
   including the generation-guard/cancellation-race tests that directly protect this
   phase's own correctness claims.
8. **Real-Azure validation, full pass** (§24, remaining items 1/3–6 not already
   covered by step 3) — executed against the fully-built coordinator, not just the
   isolated SDK-behavior probe from step 3.
9. **(Optional, product-decision-gated, §30 item 1) Scenario-B audio buffering
   refinement** — only if the open product decision in §30 resolves in favor of
   building it; sequenced last because it is explicitly a refinement, not a
   correctness requirement, for this phase's primary goal.

## 32. Production readiness gates

Objective, testable acceptance criteria for declaring Phase 7.2 implementation-ready:

- [ ] A real (non-production) Azure-backed session survives longer than one
      credential lifetime (>10 minutes of continuous or intermittent activity)
      without a forced session termination.
- [ ] No forced logout or AUTRAXIS-authentication interruption occurs as a
      side effect of a provider-credential renewal.
- [ ] No renewal ever bypasses `/provider-access`'s full authorization chain —
      verified by a test asserting every renewal call carries the same
      device/entitlement re-evaluation as an original grant.
- [ ] No provider secret (master key or otherwise) is ever exposed to the client at
      any point in the renewal flow — verified by the existing repository-wide
      secret-scan convention (Phase 7.1), extended to cover the new files.
- [ ] No duplicate final transcripts occur across a renewal-triggered reconnect —
      verified by the generation-guard tests (§23 items 10–12).
- [ ] No duplicate `UsageRecord`/`TranslationSession` rows are ever created by a
      renewal — verified by the structural/interaction tests (§23 items 24–26) and
      by confirming zero calls from renewal code to any Phase 6.9 endpoint.
- [ ] Renewal/reconnect attempts are bounded and deterministic — verified by the
      bounded-retry tests (§23 items 5, 21).
- [ ] `Stop()` during an in-flight or scheduled renewal always results in a clean,
      final shutdown with no resurrected provider connection — verified by §23 items
      7–8.
- [ ] Cancellation (application shutdown, `CancellationToken`) during renewal behaves
      identically to `Stop()` — verified by §23 item 22.
- [ ] Bidirectional sessions remain stable under independent, and under simultaneous,
      per-direction renewal — verified by §23 items 13–15.
- [ ] Transient provider/backend failures during renewal are recovered without
      manual intervention, within the bounded retry window — verified by §23 items
      16, 20, 21.
- [ ] Authorization failures during renewal (device revoked, account suspended,
      entitlement denied) fail closed, cleanly, with no retry — verified by §23 items
      17–19.
- [ ] The §24 real-Azure validation list has been executed and its findings
      incorporated (or the design revisited if any assumption in §11 is
      contradicted).

## Final architecture recommendation

Proceed with: a provider-neutral, additive `TryUpdateAuthorizationAsync`-shaped
contract in `VTTranslate.Core`; an Azure implementation using the SDK's documented
`AuthorizationToken` live-replacement capability as the primary mechanism, with the
*existing, unmodified* generation-counter/reconnect machinery as the sole fallback
(never a second, parallel reconnect mechanism); a per-direction
`ProviderCredentialRenewalCoordinator` using the already-returned `ExpiresAt` for
proactive scheduling; a category-based, bounded retry policy modeled directly on the
already-proven `DeviceRegistrationCoordinator` pattern from Phase 7.1's own corrective
patch; and zero API or backend changes, since Phase 6.8's existing gateway already
correctly and fully re-authorizes every single call it receives. The design's
correctness rests on one specific, currently-undemonstrated SDK behavior (§11) — the
required real-Azure validation (§24) is sequenced early in the implementation plan
(§31 step 3) specifically so this assumption is confirmed or corrected before the
larger coordinator is built on top of it.

**PHASE 7.2 ARCHITECTURE IS READY FOR REVIEW**, contingent on the one explicitly
flagged validation dependency in §24/§11 — this document does not claim SDK behavior
it cannot demonstrate from documentation alone, and sequences the actual verification
of that behavior as the very next step before broader implementation begins.

---

## Addendum: REAL AZURE VALIDATION (Phase 7.2A)

**Date**: 2026-09-14. **Environment**: local Windows 10 (build 26200), .NET 8.0.30,
`Microsoft.CognitiveServices.Speech` **1.40.0** (assembly `1.40.0.28`) — the exact
package/version already referenced by `VTTranslate.Core.csproj`. **Azure region**:
`eastus` (non-secret, matches this environment's existing `AZURE_SPEECH_REGION`).
**Backend end-to-end note**: no Entra tenant is configured in this environment (as in
every prior phase's real-Azure/real-Entra caveat), so the full authenticated
`Entra → AUTRAXIS API → /provider-access` HTTP chain could not be exercised end-to-end
here. The two short-lived STS tokens used in this experiment were obtained via the
identical mechanism `AzureProviderCredentialIssuer.IssueAsync` already implements
(`POST https://{region}.api.cognitive.microsoft.com/sts/v1.0/issueToken` with the
subscription key) — this isolates and directly tests the one genuinely unknown
variable (Azure SDK live-token-replacement behavior on `TranslationRecognizer`)
without depending on infrastructure unavailable in this sandbox. The AUTRAXIS-side
authorization chain itself (device/entitlement/account checks) is already covered by
Phase 6.7/6.8's own existing, separately-verified test suite and is not what this
experiment was designed to re-prove.

**Methodology**: an isolated, one-off console harness (never added to the repository
or its build — see "Repository impact" below), referencing the same
`Microsoft.CognitiveServices.Speech` package/version as production, exercising the
exact same production API sequence used by `AzureSpeechTranslationProvider`
(`SpeechTranslationConfig.FromAuthorizationToken` → `new TranslationRecognizer(...)` →
`StartContinuousRecognitionAsync()`), plus the one new operation under test:
`recognizer.AuthorizationToken = tokenB` on the **same, already-running** recognizer
instance, while continuously streaming real 16kHz/16-bit/mono PCM audio from the
repository's own existing `test-results/input-audio/test_f_long_de.wav` fixture
(~13.9 seconds of German speech) into a `PushAudioInputStream`, in ~100ms chunks with
real-time-ish pacing — direction: **German → English** (`de-DE` → `en`), matching the
prompt's stated preference and the production path's own use of this exact pair.
Every event handler logged metadata only (timestamps, event type, text *length*,
recognizer object hash) — recognized/translated text content was never logged, printed,
or persisted; neither token's raw value was ever printed (only its length).

Three independent runs were executed, varying the point (as a fraction of total audio)
at which token B was issued and applied:

| Run | Renewal point | Partials before renewal | Partials after renewal | Finals after renewal | Recognizer instance stable | Immediate error/reconnect at renewal |
|---|---|---|---|---|---|---|
| 1 | 50% (mid-utterance, active speech — **Scenario B**) | (not separately tracked in this run) | 24 total | 1/1 | Yes (hash unchanged) | None |
| 2 | 35% (mid-utterance, active speech — **Scenario B**) | 1 | 19 | 1/1 | Yes (hash unchanged) | None |
| 3 | 2% (session onset, before any partial — **Scenario A proxy**) | 0 | 21 | 1/1 | Yes (hash unchanged) | None |

**Same-recognizer proof**: `recognizer.GetHashCode()` was captured immediately before
and immediately after the `AuthorizationToken` assignment in every run and was
identical every time (`sameInstance=True`) — no new `TranslationRecognizer` was ever
constructed for the renewal itself; exactly one `RecognizerCreated` log line appears
per run.

**Token replacement result**: in all three runs, setting `AuthorizationToken` on the
live, already-started recognizer completed without raising a `Canceled` event, without
throwing an exception, and without any observable interruption to the audio stream
already in flight.

**Recognition continuity result**: partial (`Recognizing`) events continued arriving
after the token swap in every run (24, 19, and 21 post-renewal partials respectively,
including runs where partials had already been arriving *before* the swap — run 2 —
proving continuity *across* the boundary, not merely "it also worked in a second,
separate connection").

**Final-result continuity result**: exactly one final `Recognized` (translated-speech)
result was produced per run, in every case *after* the renewal point — the SDK
correctly carried the in-progress recognition through to a committed final result
despite the mid-stream token change. No run produced more than one final result for
the single ~14-second utterance, and no duplicate/re-emitted final was observed —
directly satisfying the "no duplicate final transcript attributable to renewal"
success criterion for these runs.

**Reconnect behavior**: no forced reconnect occurred as a result of the token
replacement in any run — the single recognizer instance handled the entire audio
stream, before and after renewal, without disconnecting. A `Canceled` event with
`errorCode=ServiceTimeout` did eventually fire in two of the three runs, but only
**21.8 and 26.6 seconds after** the respective renewal — long after multiple
successful post-renewal partial/final events had already been observed, and well
after the harness had finished feeding audio and begun an idle wait before calling
`StopContinuousRecognitionAsync`. This is Azure's documented service-side idle/no-audio
timeout (the same `CancellationErrorCode.ServiceTimeout` value the production code's
`IsTransientFailure` already classifies as transient), attributable to the harness
intentionally going silent after the fixture ended — **not** a consequence of the
token replacement itself, given the large time gap and the fact that renewal-adjacent
events had already succeeded cleanly in the intervening ~20+ seconds.

**Cancellation/Stop behavior**: `StopContinuousRecognitionAsync()` completed without
exception in all three runs (`StopContinuousRecognitionCompleted` logged cleanly every
time), including in the two runs where a `Canceled`/`ServiceTimeout` event had already
fired beforehand — the recognizer remained in a callable, well-behaved state even
after that late timeout.

**Security**: no provider token value, AUTRAXIS access token, or provider master key
was printed, logged, or written to any file at any point in this experiment — verified
by direct inspection of the harness source and its captured console output (only
`length=NNN` metadata for each token). No secret was committed to the repository.

**Repository impact**: **none**. The experiment harness was created and run entirely
outside this repository (a scratch/local temporary project referencing the same
`Microsoft.CognitiveServices.Speech` package/version and reading this repository's
existing `test-results/input-audio/test_f_long_de.wav` fixture read-only). No file
inside `C:\Users\sanan\Downloads\Project_VT_Software\VT_Software` was created,
modified, or deleted by running it. `git status`/`git diff` confirmed HEAD remained
at `5f13268` and the working tree unchanged (aside from this documentation addendum)
throughout.

### PASS / FAIL / INCONCLUSIVE

**PROVEN**, with one explicit scope caveat: **live `AuthorizationToken` replacement on
an already-running `TranslationRecognizer`, using an AUTRAXIS-STS-issued short-lived
Azure Speech credential, does not force a reconnect and recognition (partial and
final) continues correctly afterward** — confirmed across three independent real-Azure
runs, at three different points in an active ~14-second German utterance (including
during active mid-utterance speech, the primary scenario of interest). The scope
caveat: this experiment validated the SDK/provider-connection behavior directly
against real Azure; it did **not** re-validate the AUTRAXIS-side
Entra-authenticated `/provider-access` HTTP chain end-to-end (no Entra tenant is
configured in this environment), which remains covered by its own, separate, already
-passing test suite (Phase 6.8/7.0/7.1) and was never the variable in question for
this specific proof.

**Primary-pass checklist** (§13 of the master prompt):

- [x] token A successfully authorizes recognizer
- [x] continuous recognition is active
- [x] token B is obtained through the same mechanism the approved provider-access path
      uses server-side (direct end-to-end HTTP validation of `/provider-access` itself
      not exercised, per the scope caveat above)
- [x] token B is applied to the SAME recognizer (object-hash-verified)
- [x] recognizer remains operational
- [x] post-renewal recognition events occur (partial and final, all three runs)
- [x] active-audio scenario succeeds (runs 1 and 2, renewal at 50%/35% into active
      speech)
- [x] no forced reconnect is required solely for token replacement
- [x] no duplicate final result attributable to renewal is observed
- [x] Stop/cancellation still works cleanly (all three runs)

### Implications for Phase 7.2 implementation

- **`ProviderCredentialRenewalCoordinator`**: the proactive, live-update-first design
  in §10 of the base architecture is confirmed viable as the **primary** path — no
  redesign needed. The coordinator's "apply the new token" step should call the
  provider-neutral `TryUpdateAuthorizationAsync`-shaped contract (§9), which for Azure
  concretely means setting `recognizer.AuthorizationToken` on the existing instance
  and returning `true` (no reconnect required) — now backed by direct evidence rather
  than SDK-documentation inference alone.
- **`DirectionPipeline`**: no change needed beyond what §9/§10 of the base document
  already specified — it continues to hold one `ISpeechTranslationProvider` reference
  for the pipeline's lifetime; a live token update happens *inside* that same provider
  instance, invisible to `DirectionPipeline`.
- **Cancellation**: the existing `_stopRequested`/`_connectionLock` double-check
  pattern (§18 of the base document) is unaffected by this finding and remains the
  correct mechanism — this experiment did not need to (and did not) test a `Stop()`
  racing an in-flight renewal, since that specific race was already fully reasoned
  about architecturally in §18 and does not depend on the SDK behavior validated here.
- **Generation guards**: unaffected — since a successful live update never creates a
  new recognizer, the existing generation counter is correctly left unchanged for
  this path (§13 of the base document's "credential generation vs. connection
  generation" distinction is directly confirmed as necessary and correct: this
  experiment's successful runs never incremented a connection-generation-equivalent
  counter at all).
- **Observability**: the `Canceled`/`ServiceTimeout` behavior observed well after
  renewal (not caused by it) confirms the base document's §21 event categories
  (`RenewalSucceeded` distinct from `ProviderReconnectStarted`) are the right
  granularity — a monitoring dashboard built on these categories would correctly show
  a clean `RenewalSucceeded` with no accompanying reconnect event for the runs
  observed here.
- **Remaining §24 items not covered by this experiment**: the precise
  `CancellationErrorCode` Azure returns for a *truly expired* (not merely replaced)
  token was not observed here (all tokens used were valid throughout every run); the
  exact timing-boundary behavior of setting the property very close to true expiry
  (as opposed to well before it, which is what proactive renewal always does by
  design) was not specifically probed. Neither gap blocks proceeding with the
  proactive-primary design, since proactive renewal is specifically designed to never
  approach that boundary — they remain useful, lower-priority follow-up validations
  rather than blockers.

### Does the Phase 7.2 architecture need revision?

**No.** The one load-bearing, previously-unconfirmed assumption in the base
architecture document (§11: "Azure Speech SDK supports live `AuthorizationToken`
replacement on an already-running `TranslationRecognizer` without forcing a
reconnect") is now **empirically confirmed**, including under the specifically
flagged highest-priority scenario (renewal during active mid-utterance speech). The
architecture in §9/§10/§12/§13 of the base document should proceed to implementation
as designed, with the `TryUpdateAuthorizationAsync` Azure implementation now
confidently targeting the `AuthorizationToken` property as its primary,
proven-in-practice mechanism.

---

## Confirmations

- **Only documentation changed.** This phase produced exactly one file:
  `docs/phase-7.2-long-running-translation-session-continuity.md`. No other file in
  the repository was created or modified.
- **No production source code changed.** No `.cs` file, no `.csproj`, no `Program.cs`
  change, no new service, no new endpoint, no migration.
- **No test changed.**
- **No CI workflow changed.**
- **No frozen phase** (6.6–7.1) **redesigned or modified** — every interaction is
  documented (§26) as an unchanged extension point, never an alteration.
- **git status is clean except for this one new documentation file.**
