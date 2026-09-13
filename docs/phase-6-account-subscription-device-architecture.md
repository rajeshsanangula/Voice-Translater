# Phase 6 — AUTRAXIS Account, Subscription & Device Activation Architecture

**Status: DESIGN ONLY. No production code, translation/audio pipeline, existing UI, or provider behavior was modified in this phase. Nothing here is implemented. Wait for review before Phase 6.1 begins.**

---

## 0. How this document was produced

The entire repository was inspected before writing this proposal: `VTTranslate.Core` (Providers, Session, Audio, Config, Streaming, Diagnostics), `VTTranslate.App` (WPF UI, branding), `VTTranslate.Core.Tests` (399 tests), `VTTranslate.LiveTest`, and every prior design-notes document (`docs/architecture.md`, `docs/mvp-plan.md`, `docs/design-notes/*.md`, `docs/translation-naturalization-gemini-experiment.md`, `docs/ui-branding-and-design.md`). The proposal below is grounded in what actually exists today, not an idealized rewrite — see §15 for an explicit file-by-file impact map.

---

## 1. Current architecture summary

**What exists today** is a single-user, single-machine, developer-oriented Windows desktop MVP:

- **`VTTranslate.App`** (WPF, .NET 8): one `MainWindow` + `MainViewModel`, now AUTRAXIS-branded (Phase 5). No login, no accounts, no network calls except directly to Azure/Google. Settings (device selections only — never credentials) persist to a local JSON file (`%AppData%\VTTranslate\settings.json`) via `AppSettings.Load()/Save()`.
- **`VTTranslate.Core`**: the real product logic.
  - `Providers/AzureSpeechTranslationProvider` implements `ISpeechTranslationProvider` — one Azure `TranslationRecognizer` connection per direction, streaming ASR→MT→TTS in one call, with its own reconnect logic.
  - `Session/DirectionPipeline` wires one `IAudioInputSource` → provider → `IAudioOutputSink`, with mute, latency instrumentation (`LatencyBreakdown`), status/error events.
  - `MainViewModel` runs **two** `DirectionPipeline`s concurrently (EN mic→DE output, DE remote→EN output) — the app always translates both directions at once; there is no "direction switch."
  - `Config/AppSettings`: device IDs only. Azure credentials (`AZURE_SPEECH_KEY`/`REGION`) are read **only** from environment variables, at every layer — this convention is intentional and load-bearing for the design below (§5).
  - `Streaming/*`: a large, explicitly **experimental, shadow-only** research program (Steps 2, 5.5–5.14C) — `PrefixStabilityEngine`, `SemanticCompletionHeuristic`, `AzureTranslatorTextProvider`, `GeminiNaturalizationProvider`, `MeaningPreservationValidator`, `ConversationalStreamingPipelineExperiment`, etc. **None of this is wired into the production pipeline** (`AzureSpeechTranslationProvider`/`DirectionPipeline`) — confirmed repeatedly via `grep` checks across the whole Step 5.x series. It exists to answer "could a next-gen pipeline / naturalization layer work," not as shipped functionality.
- **Credentials**: `AZURE_SPEECH_KEY`, `AZURE_SPEECH_REGION`, `AZURE_TRANSLATOR_KEY`/`REGION`/`ENDPOINT`, `GEMINI_API_KEY` — all environment variables on the **developer's own machine**. There is currently no concept of a "customer" distinct from "the person who set these environment variables."
- **No backend.** No accounts. No subscriptions. No licensing. No usage metering. No mobile app.

**The single most important fact this design must respect**: the real-time translation pipeline (`ISpeechTranslationProvider` → `DirectionPipeline`) is a tightly-latency-bound, local, stateful, event-driven streaming component. It must **not** be routed through a request/response backend API in the hot path — see §12.

---

## 2. Target commercial architecture — overview

```
┌─────────────────────────────┐        ┌──────────────────────────────────────┐
│   AUTRAXIS Desktop App       │        │            AUTRAXIS Backend           │
│   (VTTranslate.App, evolved) │        │        (new — does not exist yet)     │
│                               │        │                                        │
│  ┌─────────────────────────┐ │  HTTPS │  ┌───────────┐  ┌───────────────────┐ │
│  │ Account / Session UI    │─┼───────►│  │   Auth    │  │  Account/Profile   │ │
│  ├─────────────────────────┤ │        │  └───────────┘  └───────────────────┘ │
│  │ Subscription/Device UI  │─┼───────►│  ┌───────────┐  ┌───────────────────┐ │
│  ├─────────────────────────┤ │        │  │Subscription│  │   Entitlement     │ │
│  │ (existing, UNCHANGED)   │ │        │  └───────────┘  └───────────────────┘ │
│  │ Real-time translation   │ │        │  ┌───────────┐  ┌───────────────────┐ │
│  │ pipeline — local only,  │ │        │  │  Device   │  │  Usage Metering   │ │
│  │ direct-to-provider,     │─┼──╳─────┤  │ Licensing │  └───────────────────┘ │
│  │ NEVER routed through    │ │  no    │  └───────────┘  ┌───────────────────┐ │
│  │ backend per-utterance   │ │  hot-  │                 │ Provider          │ │
│  └─────────────────────────┘ │  path  │                 │ Orchestration     │─┼──► Azure Speech
└─────────────────────────────┘  calls  │                 │ (issues short-    │─┼──► Azure Translator
                                          │                 │  lived, scoped   │─┼──► Gemini (future)
                                          │                 │  provider tokens) │ │
                                          │                 └───────────────────┘ │
                                          └──────────────────────────────────────┘
```

Key principle: the backend sits **between the customer's identity/entitlement and the ability to obtain provider access** — not between the customer and every audio frame. The desktop app authenticates once per session/token-refresh cycle, receives a **short-lived, scoped credential** (or a signed capability) for the provider it's entitled to use, and then runs the existing local pipeline unchanged, talking to Azure directly with that short-lived credential (see §5, §12 for exactly how this avoids both "backend in the hot path" and "raw long-lived secret in the client").

---

## 3. Account architecture

**Entity**: `Account` (the AUTRAXIS identity) + `Profile` (display data). One AUTRAXIS account can be used across Windows/Android/iOS (§7).

| Capability | Design |
|---|---|
| Registration | Email + password (or a placeholder-friendly "email + magic link", see §14 for the tradeoff). Creates `Account` (unverified) + `Profile` + a `Subscription` row defaulted to the Free/Trial plan (§4). |
| Email verification | Time-boxed verification token emailed on registration; unverified accounts can log in but are flagged `EmailVerified=false` — product decision needed (§UNRESOLVED) on whether translation use is gated on verification. |
| Login | Password grant (or passwordless) → issues an access token + refresh token pair (§10). Rate-limited (§10). |
| Logout | Revokes the current refresh token (and, optionally, all sessions — "sign out everywhere"). |
| Password reset | Time-boxed reset token via email; invalidates all existing refresh tokens for that account on successful reset (forces re-login everywhere — a deliberate security default). |
| Profile | Display name, email (change-email requires re-verification), locale/language preference (maps to the existing EN/DE direction defaults). No payment data stored directly (§14 — delegated to a billing provider). |
| Account deletion | Soft-delete with a grace window (e.g., 30 days, placeholder) before hard purge, honoring the eventual privacy/legal requirement to actually erase data; revokes all sessions, all device authorizations, and cancels any active subscription at the billing provider. |
| Session management | See `Session` entity (§8) — one row per (device, refresh-token-family), independently revocable ("sign out this device only" or "sign out everywhere"). |

This is standard SaaS account lifecycle — the design intentionally does not invent anything exotic here; see §14 for whether to build it or buy it (Auth0/Azure AD B2C/Firebase Auth/etc. vs. hand-rolled).

---

## 4. Subscription & entitlement architecture

### Subscription states

```
   ┌────────┐  upgrade   ┌────────┐  payment fails   ┌───────────────┐
   │  Trial │───────────►│ Active │─────────────────►│ Grace Period  │
   └───┬────┘            └───┬────┘                  └───────┬───────┘
       │ trial expires        │ user cancels                  │ grace expires
       ▼                      ▼                                ▼
   ┌────────┐            ┌───────────┐                   ┌─────────┐
   │ Expired│◄───────────│ Cancelled │◄──────────────────│ Expired │
   └────────┘  (no renew)└───────────┘                    └─────────┘
```

- **Trial**: time-boxed (placeholder: `{{TRIAL_DAYS}}` days) or usage-boxed (placeholder: `{{TRIAL_MINUTES}}` translated minutes) — **do not pick one without a product decision** (§UNRESOLVED). Default plan on registration.
- **Active**: paid, current billing period valid.
- **Grace period**: payment failed but service is not yet cut off (placeholder: `{{GRACE_DAYS}}` days) — standard SaaS dunning pattern, avoids hard-cutting a customer over a transient card decline. Whether to allow it at all is a product decision.
- **Expired**: no valid entitlement; translation features gated (exact gating behavior — hard stop vs. read-only/history-only — is a product decision, §UNRESOLVED).
- **Cancelled**: user-initiated; typically remains `Active` in behavior until the current paid period ends, then transitions to `Expired`.

### Plan → Entitlement → Usage limit

- `Plan` is the sellable SKU (e.g., `Free`, `Pro`, `Business` — placeholder names, **no pricing invented**, per instruction).
- `Entitlement` is what a plan actually grants — e.g., `MaxActiveDevices`, `MaxTranslationMinutesPerMonth`, `AllowedLanguagePairs`, `AllowedProviders` (e.g., could a `Business` plan get Gemini naturalization once that's ever promoted out of experimental — explicitly NOT now, §17). Entitlements are looked up per-request by the backend when issuing provider access (§5), never trusted from the client.
- `UsageRecord` accumulates metered consumption (translated seconds, per direction, per device) against the current billing period, checked against the plan's usage-limit entitlements before a provider-access grant is issued.

This is a standard three-tier model (Plan defines Entitlements, Entitlements are checked against Usage) — deliberately conventional, since inventing a novel licensing model has no benefit here and the described requirements (limits, grace period, entitlements) map directly onto it.

---

## 5. Provider security architecture

**This is the load-bearing security requirement of the whole phase**, stated explicitly in the prompt and consistent with a convention this repository *already* enforces informally (Azure/Translator/Gemini keys are environment variables, never hard-coded, never logged — see every `docs/design-notes/*.md` "Security" section from Steps 5.5 onward). Phase 6 formalizes and extends that convention from "the developer's own machine" to "no customer machine, ever."

```
Desktop App                     AUTRAXIS Backend                    Provider
    │                                   │                                │
    │ 1. Login (Phase 6.3)              │                                │
    │──────────────────────────────────►│                                │
    │◄── access + refresh token ────────│                                │
    │                                   │                                │
    │ 2. "I want to start a session"    │                                │
    │    (device-authenticated,         │                                │
    │     entitlement-checked)          │                                │
    │──────────────────────────────────►│                                │
    │                                   │  checks: subscription active?  │
    │                                   │  device authorized? under      │
    │                                   │  usage limit?                  │
    │                                   │                                │
    │◄── short-lived provider token ────│  (e.g., Azure Speech supports  │
    │    (minutes, scoped, NOT the      │   short-lived authorization    │
    │     account's Azure master key)   │   tokens issued from a key the │
    │                                   │   backend holds — the backend  │
    │                                   │   never gives the client the   │
    │                                   │   underlying subscription key) │
    │                                   │                                │
    │ 3. Existing DirectionPipeline / ISpeechTranslationProvider,        │
    │    UNCHANGED, talks directly to Azure using the short-lived token │
    │────────────────────────────────────────────────────────────────► Azure
```

Key points:
- **Provider secrets (`AZURE_SPEECH_KEY`, `AZURE_TRANSLATOR_KEY`, `GEMINI_API_KEY`) live only on the backend**, in its own secret store (§10) — never shipped in the installer, never in the client's config, never in a token the client can extract and replay indefinitely.
- Azure Speech already supports **short-lived authorization tokens** (10-minute validity, reissued from a subscription key) as a first-class SDK feature — this is not a novel mechanism the backend must invent; it's the officially supported pattern for exactly this "don't ship your key to the client" scenario. The design reuses it directly: the backend's "Provider Orchestration" component holds the real key and mints these tokens on demand, gated by the entitlement check.
- For providers without a built-in short-lived-token concept (Azure Translator's REST API, Gemini), the backend acts as a **thin authenticated relay only for the specific calls that are not latency-critical or not per-audio-frame** (e.g., a batch translation confirmation call, were naturalization ever promoted to production — it is explicitly not, §17) — never for the real-time audio stream itself.
- The existing `ISpeechTranslationProvider`/`AzureSpeechTranslationProvider` **constructor signature already takes a key+region as strings** — nothing about its internals needs to change for this model; only *where the key string comes from* changes (from an environment variable to a short-lived token fetched from the backend at session start). This is why §15 shows this as a small, additive change, not a rewrite.

---

## 6. Device activation architecture

| Concept | Design |
|---|---|
| Device identity | On first run, the desktop app generates a stable local device identifier (e.g., a GUID persisted alongside the existing `AppSettings` JSON) plus collects minimal, non-invasive device metadata (OS version, app version) for support/diagnostics — never hardware-fingerprint-based tracking beyond what's needed for licensing. |
| Registration | On login, the app registers `(AccountId, DeviceId, Platform=Windows, DisplayName, PublicKey?)` with the backend. The backend issues a **device credential** distinct from the user's own login credential — this is what lets a session survive a password change without silently logging out every device, and is what future mobile clients will also do. |
| Authorization | Device row has `Status: Pending → Authorized → Revoked`. Authorized devices count against the plan's `MaxActiveDevices` entitlement. |
| Maximum active devices | Enforced at registration time: if the account is already at its plan's device limit, registration is rejected with a clear "manage your devices" flow (client presents the current device list, lets the user revoke an old one). This is standard SaaS seat-limiting, not novel. |
| Revocation | User-initiated (from the desktop or, eventually, a web account-management surface) or backend-initiated (fraud, subscription fully lapsed past a hard-cutoff point). A revoked device's next attempt to refresh its session/provider token is rejected — it does NOT get to keep translating on a stale long-lived credential (this is precisely what makes the short-lived provider token in §5 also a revocation mechanism, "for free"). |
| Cross-platform identity | The `Account` is platform-agnostic; `Device` rows exist per platform (`Windows`, `Android` (future), `iOS` (future)) under the same account, all counted against the same `MaxActiveDevices` entitlement unless the product decides platforms should have separate sub-limits (§UNRESOLVED). |

---

## 7. Mobile (design-only, not implemented)

Android and iOS will authenticate against the **same AUTRAXIS backend** described in §9 (same `Auth`/`Account`/`Device` APIs) — there is nothing Windows-specific about the backend's account/subscription/device model. What differs per platform:

- **Audio capture**: Android (`AudioRecord`/Oboe) and iOS (`AVAudioSession`/CallKit constraints) each need their own native audio layer — this repository's `docs/architecture.md` already documents this as a distinct, deferred engineering effort per platform, and Phase 6 does not change that assessment. The `ISpeechTranslationProvider` abstraction itself is platform-agnostic (it only needs PCM bytes in and events out), so a future native mobile client re-implements audio capture/playback natively but can reuse the *same* provider-orchestration/token model from day one.
- **Device registration**: identical flow to §6, `Platform` field distinguishes them.
- **Subscription**: a mobile app store purchase (Apple/Google IAP) vs. a web/desktop billing-provider purchase (§14) both need to resolve to the *same* `Subscription`/`Entitlement` state server-side — this is a known hard problem (App Store server notifications / Google Play RTDN reconciled against one canonical subscription record) and is flagged as a specific risk in §16, not solved in this pass.
- **No mobile code, project, or dependency is added in this phase** — this section is purely so the backend/data-model design (§8–9) doesn't have to be redesigned when mobile actually starts.

---

## 8. Data model (proposed entities)

Only entities actually justified by the architecture above — no speculative tables.

```
Account
  Id (PK), Email (unique), PasswordHash (nullable if passwordless), EmailVerified,
  CreatedAt, Status (Active/SoftDeleted), DeletionRequestedAt (nullable)

Profile
  AccountId (PK/FK), DisplayName, PreferredLanguagePair, CreatedAt, UpdatedAt

Plan
  Id (PK), Name (placeholder: Free/Pro/Business), PriceHandle (opaque reference to the
  billing provider's price/product — see §14; NOT a hard-coded number in this system),
  IsPubliclyPurchasable

Entitlement
  Id (PK), PlanId (FK), Key (e.g. "MaxActiveDevices", "MaxMinutesPerMonth",
  "AllowedLanguagePairs"), Value

Subscription
  Id (PK), AccountId (FK), PlanId (FK), Status (Trial/Active/GracePeriod/Expired/Cancelled),
  CurrentPeriodStart, CurrentPeriodEnd, BillingProviderSubscriptionId (opaque handle —
  see §14), CreatedAt, UpdatedAt

Device
  Id (PK), AccountId (FK), Platform (Windows/Android/iOS), DisplayName,
  Status (Pending/Authorized/Revoked), DeviceCredentialHash, RegisteredAt, LastSeenAt,
  RevokedAt (nullable)

Session
  Id (PK), AccountId (FK), DeviceId (FK), RefreshTokenFamilyId, IssuedAt, ExpiresAt,
  RevokedAt (nullable), IpAddress (for audit, §10)

UsageRecord
  Id (PK), AccountId (FK), DeviceId (FK), Direction (EnToDe/DeToEn), SecondsTranslated,
  ProviderUsed, PeriodBucket (e.g. year-month, for fast rollup against plan limits),
  RecordedAt

ProviderConfiguration
  Id (PK), ProviderName (AzureSpeech/AzureTranslator/Gemini), Region, SecretRef (a
  REFERENCE into the backend's secret store — e.g. a Key Vault URI — never the secret
  value itself, and never present in any client-reachable table), IsEnabled,
  UpdatedAt — this is the internal/admin configuration surface that replaces the
  developer-facing "Azure Speech Provider" card (§13)

AuditEvent
  Id (PK), AccountId (nullable FK — some events are system-level), EventType
  (Login/Logout/PasswordReset/DeviceRevoked/SubscriptionChanged/ProviderTokenIssued/...),
  Metadata (JSON, no secrets, no translated speech content — consistent with this
  project's existing "diagnostic logs are metadata-only" convention), OccurredAt
```

Deliberately **not** proposed: a `PaymentMethod`/`Invoice` table (delegate to the billing provider, store only an opaque reference — §14), a `TranslationHistory`/transcript-storage table (no requirement for it was stated, and this project's existing convention is to never persist recognized/translated speech — extending that principle here rather than silently introducing a new data-retention surface).

---

## 9. API contract (proposed, REST/JSON, versioned e.g. `/api/v1/...`)

Grouped exactly as requested. All endpoints except `auth/register`, `auth/login`, `auth/refresh`, `auth/forgot-password`, `auth/reset-password` require a valid access token.

### Authentication
```
POST   /auth/register              { email, password }              → 201, verification email sent
POST   /auth/verify-email          { token }                        → 200
POST   /auth/login                 { email, password }              → 200 { accessToken, refreshToken, expiresIn }
POST   /auth/refresh               { refreshToken, deviceId }       → 200 { accessToken, refreshToken, expiresIn }
POST   /auth/logout                { refreshToken }                 → 204
POST   /auth/forgot-password       { email }                        → 202 (always, to avoid account enumeration)
POST   /auth/reset-password        { token, newPassword }           → 200 (revokes all existing sessions)
```

### Account
```
GET    /account/me                                                  → 200 { profile, emailVerified, ... }
PATCH  /account/me                 { displayName?, ... }            → 200
POST   /account/me/change-email    { newEmail }                     → 202 (verification email to new address)
DELETE /account/me                 { confirmation }                 → 202 (soft-delete, starts grace window)
GET    /account/sessions                                            → 200 [ { deviceId, lastSeenAt, ... } ]
DELETE /account/sessions/{id}                                       → 204 (sign out one device)
DELETE /account/sessions                                            → 204 (sign out everywhere)
```

### Subscription
```
GET    /subscription                                                → 200 { plan, status, currentPeriodEnd, ... }
GET    /plans                                                       → 200 [ { id, name, entitlements[] } ]   (public)
POST   /subscription/checkout      { planId }                       → 200 { checkoutUrl }   (redirect to billing provider — §14)
POST   /subscription/cancel                                         → 202
POST   /subscription/webhook       (billing-provider → backend, not client-facing; signature-verified, §10)
```

### Device
```
POST   /devices/register           { deviceId, platform, displayName } → 201 { status: Pending|Authorized }
GET    /devices                                                     → 200 [ { id, platform, status, lastSeenAt } ]
DELETE /devices/{id}                                                → 204 (revoke)
```

### Entitlement (mostly read-only, derived — small dedicated surface for the client to check "can I do X" cheaply)
```
GET    /entitlements                                                → 200 { maxActiveDevices, maxMinutesPerMonth, remainingMinutes, ... }
POST   /entitlements/provider-token   { provider: "AzureSpeech", direction }
                                                                     → 200 { token, expiresAt, region }
                                                                       (THE endpoint from §5's sequence diagram —
                                                                        checks subscription+device+usage before minting)
```

### Usage
```
POST   /usage/report                { deviceId, direction, secondsTranslated, provider }
                                                                     → 202 (client reports consumption at session end /
                                                                        periodic checkpoints — backend is still the
                                                                        source of truth for enforcement via the
                                                                        provider-token endpoint above, not this report
                                                                        alone, since a client-only report is not
                                                                        trustworthy for enforcement — see §10)
GET    /usage/summary                                                → 200 { periodStart, periodEnd, secondsUsed, limit }
```

---

## 10. Security model

- **Access tokens**: short-lived (placeholder: 15 minutes) signed JWTs (or opaque tokens validated server-side — a technology choice, §14), carrying `AccountId`, `DeviceId`, minimal claims. Never carry provider secrets.
- **Refresh tokens**: longer-lived, opaque, stored hashed server-side, **rotated on every use** (refresh-token rotation with family tracking) — if a refresh token is used twice (stolen + replayed), the whole family is revoked and the user is forced to re-authenticate. This is the standard, well-understood mitigation for refresh-token theft and directly satisfies the "replay protection" requirement.
- **Password security**: never stored in plaintext; a modern slow hash (Argon2id or bcrypt — technology choice, not re-litigated here) with per-password salt; minimum complexity rules are a product/UX decision, not a security architecture decision, and are deliberately left open (§UNRESOLVED).
- **Device authentication**: each device holds its own device credential (issued at registration, distinct from the user's password), so a compromised/shared password does not automatically extend to every device, and revoking one device doesn't require rotating the account password.
- **Authorization**: every backend endpoint checks (a) is the access token valid and unexpired, (b) does the token's device still have `Status=Authorized`, (c) for entitlement-gated actions, is the subscription state + usage within limits — checked server-side on every provider-token request, never trusted from client-reported state.
- **Subscription enforcement**: enforced exactly once, at the narrowest possible point — the `entitlements/provider-token` mint step (§5, §9). This is deliberate: rather than scattering "is this user allowed" checks across many endpoints, the one gate that actually matters (can this device get a working credential to talk to Azure right now) is centralized and cannot be bypassed by a modified client, because a modified client still cannot mint its own valid Azure token without the backend's cooperation.
- **Replay protection**: refresh-token rotation (above); billing-provider webhooks must be signature-verified (e.g., Stripe-style HMAC signature header) and idempotency-keyed so a replayed webhook can't double-apply a subscription change.
- **Rate limiting**: applied at minimum to `/auth/login`, `/auth/forgot-password`, `/auth/register` (brute-force/enumeration protection) and to `/entitlements/provider-token` (abuse/cost-control — a compromised device shouldn't be able to mint unlimited Azure tokens).
- **Audit logging**: every security-relevant event (`AuditEvent`, §8) — login, logout, password reset, device registration/revocation, subscription change, provider-token issuance — metadata only, consistent with this project's existing "no speech content in logs" convention, extended to "no credentials, no full tokens, ever, in logs."
- **Secret management**: `AZURE_SPEECH_KEY`/`AZURE_TRANSLATOR_KEY`/`GEMINI_API_KEY` move from developer environment variables to a proper backend secret store (Azure Key Vault, AWS Secrets Manager, or equivalent — §14) referenced by `ProviderConfiguration.SecretRef`, never returned by any API, never logged (directly extending, not inventing, this repo's existing "never log/print/hard-code a provider key" rule from every Step 5.x design-notes document).
- **Privacy**: recognized/translated speech text is never persisted server-side by this design (no `TranslationHistory` table, §8) — the backend's role is authentication/entitlement/licensing, not a transcript store, consistent with the existing local-pipeline's own "diagnostic logs are metadata-only, never speech content" rule. If a future product decision wants transcript history as a feature, that is a deliberate, separate, consent-gated design — not a byproduct of this phase.

---

## 11. Offline behavior

**Principle stated in the prompt, upheld throughout**: offline behavior must degrade functionality, never bypass entitlement enforcement.

| Scenario | Behavior |
|---|---|
| Internet temporarily disappears mid-session | The existing local pipeline already has its own reconnect logic for the Azure connection itself (`AzureSpeechTranslationProvider`'s reconnect handling, unchanged). A currently-active, already-issued short-lived provider token continues to work until it expires (its short lifetime — minutes — bounds how long a fully-offline session can keep translating, which is an acceptable, deliberate tradeoff, not a bug). |
| Backend unavailable at session **start** | The client cannot obtain a fresh provider token → cannot start a new translation session. The UI shows a clear "can't verify your subscription right now" state (§13) rather than silently failing or silently allowing unlimited use. |
| Subscription cannot be refreshed (backend reachable, but the check itself is stale/cached) | A short **bounded grace cache** (placeholder: e.g., 24–72 hours) of the *last known-good* entitlement check may be allowed for continuity (a common SaaS-desktop pattern — e.g., "you were entitled as of 2 hours ago, we'll let you keep going briefly while we can't reach the license server") — but this must be a deliberate, product-approved, time-boxed exception, not indefinite offline use, and must never be implemented as "if backend unreachable, assume entitled" without an explicit expiry and re-validation requirement. This is flagged as a product decision (§UNRESOLVED), not decided here.
| Device license cannot be validated at all (device revoked while offline) | On the next successful backend contact, the device's provider-token requests are rejected immediately — there's no path for a revoked device to keep obtaining tokens, online or offline, since it can't mint them locally. |

The load-bearing guarantee: **the provider token itself is the enforcement mechanism**, and it is short-lived by construction — so "offline" naturally bounds how long a session can run without a fresh check, without needing a separate, harder-to-verify "phone-home" heartbeat mechanism layered on top.

---

## 12. Real-time translation pipeline — exactly where it fits

**Nothing about the existing pipeline changes.** Concretely:

- `ISpeechTranslationProvider`, `AzureSpeechTranslationProvider`, `Session/DirectionPipeline`, `Audio/*` (capture/playback/device catalog), `MainViewModel`'s two-direction orchestration, `LatencyBreakdown` — **all remain local, unchanged, and stay in `VTTranslate.Core`/`VTTranslate.App`.**
- The **only** new interaction point is *how the key/region strings that `AzureSpeechTranslationProvider`'s constructor already takes* are obtained — today, directly from `AppSettings`/environment variables; after Phase 6, from a short-lived token fetched from the backend at session start (§5). The constructor signature and the entire streaming pipeline downstream of it are untouched.
- **What must move server-side eventually** (future phases, not this one): account/subscription/device state, usage metering, and the provider-token minting itself — i.e., exactly the four new backend components in §4. The *translation logic itself* (VAD, ASR, MT, TTS, audio routing) has no reason to ever move server-side — doing so would reintroduce exactly the "backend in the hot path" latency problem this document's architecture diagram (§2) explicitly avoids.
- The experimental `Streaming/*` components (Steps 5.5–5.14C: `ConversationalStreamingPipelineExperiment`, `GeminiNaturalizationProvider`, `MeaningPreservationValidator`, etc.) remain exactly as they are — isolated, unwired, UNVALIDATED-for-production research. Phase 6 does not promote, reference, or depend on any of them; if a future phase ever did promote naturalization to production, it would go through the exact same provider-orchestration/token model as Azure Speech (§5), never bypass it.

---

## 13. UI migration — what's customer-facing vs. developer-only vs. internal diagnostics

| Current UI element | Classification | Disposition |
|---|---|---|
| "Azure Speech Provider" card (shows region + masked key, environment-variable instructions) | **Developer-only** | Removed from the customer-facing window entirely. Becomes an internal/admin diagnostic surface (§8's `ProviderConfiguration` is backend-only; if a local diagnostic view is ever needed, it's a separate build configuration or a hidden admin panel, never shown to a paying customer). |
| Session controls (Start/Stop, the two EN→DE/DE→EN direction chips) | **Customer-facing** | Stays, unchanged in behavior — this IS the product. |
| Mute button | **Customer-facing** | Stays, unchanged. |
| Audio Devices card (Microphone/Remote/English Output/German Output) | **Customer-facing** | Stays — a customer absolutely still needs to pick their mic/speakers/virtual cable. This is product configuration, not provider configuration, and is not what the prompt means by "hide provider config." |
| Live Translation transcript | **Customer-facing** | Stays, unchanged. |
| Latency diagnostics strip (TOTAL/CAPTURE→PARTIAL/ASR+MT/→TTS/TTS→QUEUE) | **Internal diagnostics, currently customer-visible** | Product decision needed (§UNRESOLVED): keep as a small "Advanced/Diagnostics" collapsible section (useful for support tickets), or move fully behind a hidden diagnostics mode. Leaning toward keeping a simplified version, since "why is this slow" is a legitimate customer question — but the raw P90/breakdown terminology is support/engineering-flavored and should probably be simplified or relabeled for a paying customer. |
| About window | **Customer-facing** | Stays, extended (§6 below) with account/subscription info. |

**New customer-facing UI required** (design only, not built — this is what "Desktop application" in the prompt's §6 refers to):
- **Login screen** — shown before `MainWindow` if no valid session exists; email/password (or passwordless) fields, "forgot password" link, branded per the existing AUTRAXIS theme (`Themes/BrandTheme.xaml`, reused unchanged).
- **Account/profile menu** — a header-area menu (next to the existing status pill/About button) showing the signed-in email, linking to profile edit, sign-out, and (opening a browser to) subscription management.
- **Subscription status** — a small persistent indicator (e.g., "Pro · renews Mar 14" or "Trial · 4 days left") in the header, consistent with the existing status-pill visual language (§ui-branding-and-design.md's `AppStatusKind` pattern extends naturally to a `SubscriptionStatusKind`).
- **Device status** — shown in the account menu ("This device: Authorized") with a link to manage devices (full device list is likely a web surface, not a desktop dialog, per §14's "don't build what you can buy" bias).
- **Application status** — the existing `Status`/`StatusKind` pill, unchanged.
- **Settings** — the existing Audio Devices card, reorganized under a "Settings" concept if other settings accumulate later; no new settings invented now (per the original branding phase's own "don't add controls implying functionality that doesn't exist" rule, carried forward).
- **Sign out** — from the account menu, calls `/auth/logout`, returns to the login screen.
- **Subscription/entitlement presentation** — when entitlement is exhausted or expired, a clear, honest in-app message (not a silent failure, not a fake "success") directing the user to renew/upgrade.

---

## 14. Technology options (evaluated, one recommended — not blindly chosen)

### Authentication / Identity

| Option | Startup cost | Security | Win/Android/iOS | Scale | Complexity | Maintainability |
|---|---|---|---|---|---|---|
| **Hand-rolled** (own password hashing, own JWT issuance) | Low $ | Highest risk — easy to get subtly wrong (token revocation, rotation, password policy) | Full control, but you build every SDK | Fine at small scale | High effort now | High ongoing burden (security patching is now your job) |
| **Azure AD B2C** | Low $ (consumption-based), fits naturally since Azure is already the primary provider vendor here | Strong, Microsoft-managed | Good — has mobile SDKs | Scales well | Medium (B2C policy configuration has a learning curve) | Low — Microsoft maintains it |
| **Auth0 / Okta CIC** | Free tier then per-MAU cost | Strong | Excellent SDK coverage all platforms | Scales well | Low-medium | Low |
| **Firebase Auth** | Very low / free tier generous | Strong | Excellent, especially mobile | Scales well | Low | Low, but couples you to Google's ecosystem for auth specifically (not the rest of the stack) |

**Recommendation: Azure AD B2C**, specifically *because* this product already has an Azure billing relationship (Speech/Translator) and an Azure-first operational posture — one vendor for identity + the AI providers reduces the number of cloud relationships to manage, and B2C's custom-policy model can accommodate the account/device/subscription claims this design needs. Auth0 is the strong runner-up if B2C's policy XML complexity proves too heavy for the team's size — flagged as a fallback, not dismissed.

### Backend hosting/framework

| Option | Notes |
|---|---|
| **ASP.NET Core Web API (.NET 8)** | Same language/runtime as the entire existing codebase (`VTTranslate.Core` is already .NET 8) — lowest ramp-up cost, can share DTOs/validation logic patterns already established, easiest for the current team to maintain. |
| Node/Express, Python/FastAPI, Go | All viable, but introduce a second language/runtime into a currently single-stack (.NET) codebase for no functional benefit here. |

**Recommendation: ASP.NET Core Web API**, hosted on Azure App Service or Azure Container Apps (again, single-vendor operational simplicity, and native integration with Key Vault for §10's secret management and Azure AD B2C for auth).

### Database

| Option | Notes |
|---|---|
| **Azure SQL Database** | Relational fits this data model well (§8 is a classic normalized schema — accounts, subscriptions, entitlements, devices — not a document/graph problem); integrates with EF Core, which the team can pick up quickly given existing C# familiarity. |
| PostgreSQL (Azure Database for PostgreSQL) | Equally valid relational choice, slightly cheaper at small scale, strong tooling — a legitimate alternative if avoiding SQL Server licensing/ecosystem lock-in matters to the business. |
| Cosmos DB / NoSQL | Not recommended here — this data model is relational by nature (foreign keys, joins for entitlement checks); forcing it into a document model adds complexity without benefit. |

**Recommendation: Azure SQL Database** for the same single-vendor-simplicity reasoning, with PostgreSQL flagged as the cost-conscious alternative if Azure SQL's pricing is a concern at the target scale.

### Subscription billing

| Option | Notes |
|---|---|
| **Stripe Billing** | Industry-standard, excellent webhook model (maps directly to §9's `/subscription/webhook`), handles dunning/grace-period logic largely out of the box, well-documented SDKs. Does not natively unify with mobile app-store purchases (§7's flagged risk still applies). |
| Paddle / Chargebee | Merchant-of-record alternatives (handle sales tax/VAT compliance for you) — worth evaluating once the business's tax situation is known; not recommended over Stripe by default, since that's a business/legal decision outside this design's scope. |
| Roll-your-own billing | Not recommended — payment processing, PCI compliance, and dunning logic are a solved problem; reinventing it is pure risk with no product benefit. |

**Recommendation: Stripe Billing** as the default, with the explicit caveat that mobile IAP reconciliation (§7) is a separate integration regardless of which web billing provider is chosen.

### Secure token/session management

Standard OAuth2/OIDC patterns (access token + rotating refresh token, §10) via whichever identity provider is chosen above (Azure AD B2C or Auth0 both implement this natively) — **not a separate technology decision**, it falls out of the identity-provider choice.

---

## 15. Existing code impact map

**Untouched, by design (confirmed against the actual files in this repo):**
- `VTTranslate.Core/Providers/ISpeechTranslationProvider.cs`, `AzureSpeechTranslationProvider.cs`
- `VTTranslate.Core/Session/DirectionPipeline.cs`, `TranslationSession.cs`, `LatencyBreakdown.cs`, `DirectionLabel.cs`
- `VTTranslate.Core/Audio/*` (capture/playback/device catalog)
- `VTTranslate.Core/Streaming/*` (the entire experimental research program — remains isolated/unwired)
- All existing 399 unit tests

**Would eventually change (future phases, not now):**
- `VTTranslate.Core/Config/AppSettings.cs` — would gain fields for the cached account/session state (e.g., last-known device-authorization status for the offline-grace behavior in §11) alongside the existing device-ID settings; the `AzureSpeechKey`/`AzureSpeechRegion` properties would eventually be sourced from a fetched short-lived token instead of environment variables — but note **this can be done additively** (keep the env-var path as a `--dev-mode`/internal fallback for exactly the kind of local experimentation this whole Step 5.x series has relied on, rather than deleting it).
- `VTTranslate.App/MainViewModel.cs` — would gain account/subscription/device state and a dependency on a new backend-client component; the existing `StartAsync`/`StopAsync`/mute/latency logic is otherwise unaffected.
- `VTTranslate.App/MainWindow.xaml` — the "Azure Speech Provider" card is removed from customer view (§13); new header elements (account menu, subscription status pill) are added following the exact visual patterns already established in Phase 5 (`Themes/BrandTheme.xaml`, `AppStatusKind`-style pills).
- `VTTranslate.App.csproj` — would gain a reference to a new `VTTranslate.Client` (or similarly named) project for backend API calls, and an authentication SDK package (whichever is chosen in §14).

**New projects required (future phases):**
- `VTTranslate.Backend` (ASP.NET Core Web API) — implements §9's endpoints.
- `VTTranslate.Backend.Tests` — following this repo's existing strong testing convention (xUnit, deterministic fakes for external dependencies — the same pattern already used throughout `VTTranslate.Core.Tests`).
- A thin `VTTranslate.Client` (or embed directly in `VTTranslate.App`) for the desktop app's HTTP calls to the new backend — kept deliberately separate from `VTTranslate.Core` so the core translation library's zero-network-dependency property (useful for the very testing strategy this whole project has relied on) is preserved.

**New services required (future phases, external to this repo):**
- Azure AD B2C tenant (or chosen alternative).
- Azure SQL Database (or chosen alternative).
- Azure Key Vault (or chosen alternative) for `ProviderConfiguration.SecretRef`.
- Stripe (or chosen alternative) account + webhook endpoint.

---

## 16. Migration plan (phased)

| Phase | Scope |
|---|---|
| **6.1 Architecture** | This document. Review and sign-off before any code. |
| **6.2 Backend foundation** | Stand up `VTTranslate.Backend` skeleton, database schema (§8) via EF Core migrations, no real endpoints yet beyond health-check — establishes the deployable shape before building on it. |
| **6.3 Authentication** | Wire the chosen identity provider (§14); implement `/auth/*` (§9); no client integration yet. |
| **6.4 Account/profile** | Implement `/account/*`; still backend-only, testable via the same xUnit conventions as the rest of the repo. |
| **6.5 Subscription** | Implement `Plan`/`Subscription` entities and `/subscription/*`, including the billing-provider webhook — **can be built and tested against Stripe's test mode without any real charges**, satisfying "do not implement payment yet" for THIS phase while making 6.5 itself the phase where it's finally built for real, deliberately sequenced after review of this document. |
| **6.6 Entitlements** | Implement `Entitlement`/`/entitlements` including the critical `provider-token` mint endpoint (§5, §9) — this is the phase where Azure Speech short-lived-token issuance is actually wired up server-side. |
| **6.7 Device licensing** | Implement `Device`/`/devices/*`, max-device enforcement. |
| **6.8 Desktop integration** | **First and only phase that touches `VTTranslate.App`/`VTTranslate.Core`** — add the login screen, account menu, and swap `AppSettings`'s credential source from environment variables to the backend-issued token (additively, per §15). This is where `AzureSpeechTranslationProvider`'s callers change what they pass in, not where the provider itself changes. |
| **6.9 Usage metering** | Implement `/usage/*`, wire `UsageRecord` creation from the desktop client at natural checkpoints (session stop, periodic heartbeat during long sessions). |
| **6.10 Mobile identity foundation** | Backend-only: verify `/auth/*`/`/devices/*` work correctly for a non-Windows `Platform` value and design (not build) the App Store/Play Store purchase-reconciliation flow flagged in §7/§16 risks — no mobile app code. |

Each phase should land with its own tests and its own STOP-for-review, following the exact discipline this project has already used throughout the Step 5.x series.

---

## 17. Risks

- **Mobile purchase reconciliation** (§7) — App Store/Play Store subscription events vs. this backend's own `Subscription` record is a well-known source of subtle bugs (refunds, upgrades/downgrades, family sharing) industry-wide; needs its own dedicated design pass when mobile actually starts, not solved by this document.
- **Offline grace window abuse** (§11) — any bounded "keep working briefly without a fresh check" mechanism is inherently a small, deliberate compromise between UX and strict enforcement; the exact bound and whether it exists at all is a product decision this document flags but does not make.
- **Short-lived-token latency overhead at session start** — fetching a fresh provider token adds one network round-trip before a translation session can begin; should be measured against the existing pipeline's own startup latency once built (Phase 6.8) to confirm it's not perceptible, rather than assumed.
- **Azure Speech token lifetime (10 minutes) vs. long meetings** — a long-running translation session may need the desktop client to silently refresh its provider token mid-session; this is a real implementation detail for Phase 6.8, not a blocker, but should be explicitly tested (a long-session integration test), since this project's own culture is "never claim it works without testing it."
- **Single point of failure**: if the AUTRAXIS backend is down, no NEW sessions can start anywhere (§11) — this is an accepted tradeoff of "provider secrets never touch the client," not an oversight, but it does mean backend availability/on-call posture becomes a real product-quality dependency in a way it never was for the current MVP.
- **Scope creep risk into the existing experimental `Streaming/*` work** — Phase 6 must NOT become a backdoor for promoting `GeminiNaturalizationProvider`/naturalization into production; §12 explicitly forecloses this, and it's called out again here as a risk to watch for in review, given how much prior momentum exists around that experimental line of work.

---

## 18. Unresolved product decisions (explicitly not decided in this document)

1. Trial: time-boxed vs. usage-boxed vs. both (§4).
2. Exact grace-period length, and whether a grace period is offered at all (§4).
3. Behavior on `Expired`: hard stop vs. degraded/read-only mode (§4).
4. Whether device limits are pooled across platforms or per-platform (§6).
5. Email verification: required before any translation use, or allowed-but-flagged (§3).
6. Password policy specifics (length/complexity rules) — a UX decision layered on top of the security architecture, not part of it (§10).
7. Whether the offline bounded-grace-cache (§11) is offered at all, and its exact duration.
8. Whether the latency diagnostics strip stays customer-visible in simplified form or moves fully behind a hidden diagnostics mode (§13).
9. Final plan names/tiers and all pricing — explicitly out of scope per instruction; placeholders only (`{{TRIAL_DAYS}}`, `{{GRACE_DAYS}}`, plan names) used throughout this document.
10. Merchant-of-record vs. direct Stripe billing (tax/VAT handling) — a legal/business decision (§14).

---

## Findings summary

- The existing repository is well-suited to this evolution: the provider abstraction (`ISpeechTranslationProvider`) and the environment-variable-only credential convention already in place mean the core security requirement of Phase 6 (provider secrets never in the client) is a **natural extension of an existing pattern**, not a foreign concept being bolted on.
- The real-time pipeline's local, event-driven, low-latency design is fully compatible with a backend that handles identity/entitlement/licensing but never sits in the per-audio-frame hot path — confirmed by mapping the exact mechanism (§5's short-lived provider token) onto Azure Speech's own officially-supported token-issuance feature, not an invented workaround.
- The experimental `Streaming/*` research program (Steps 5.5–5.14C) remains fully isolated and is explicitly excluded from this phase's scope.

## Confirmations

- **No production code was changed.** No file under `src/VTTranslate.Core/Providers`, `src/VTTranslate.Core/Session`, `src/VTTranslate.Core/Audio`, or `src/VTTranslate.App` (beyond this being a documentation-only commit) was modified in this phase.
- **No existing tests were affected.** The full 399-test suite was not re-run as part of this design-only phase since no code changed; the last confirmed state (Phase 5, UI branding) was 399/399 passing with a clean build, and nothing in this document alters any file that suite covers.
- **No authentication libraries, payment integrations, backend projects, or cloud resources were added or created.** This document is the only artifact produced.

**STOP — Phase 6 architecture proposal complete. Waiting for review before Phase 6.1 (or any implementation) begins.**
