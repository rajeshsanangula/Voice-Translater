# Phase 7.3 — Customer Subscription, Entitlement & Usage Visibility

**Status: ARCHITECTURE ONLY.** No source code, migration, API, test, CI workflow, or
client code was created or modified. This document is the sole deliverable of this
pass.

---

## 1. Executive Summary

Phases 6.6–7.2 built a complete, tested, production-authoritative backend model of a
customer's commercial relationship with AUTRAXIS: what plan they are on
(`GET /subscription`), what that plan entitles them to (`GET /entitlements`), how much
of it they have used (`GET /usage`), and how to end that relationship
(`POST /subscription/cancel`). All four endpoints are implemented, authorized via the
same `AccountResolutionMiddleware` boundary as every other endpoint, and exercised by
the backend test suite.

Direct inspection of the shipped Windows client confirms none of this is reachable by
a customer. `IAutraxisApiClient` — the client's entire abstraction over the AUTRAXIS
backend — has no method for `/subscription`, `/entitlements`, `/usage`, or
`/subscription/cancel` at all; not a stub, not a TODO, nothing. `MainWindow.xaml` and
`MainViewModel.cs` contain zero references to plan, entitlement, or usage. The only
customer-visible signal about their commercial state today is a generic string —
`"Your plan does not currently allow this."` — surfaced only at the moment a session
fails to start, with no prior visibility and no path to act on it.

This means a paying customer currently has no way, anywhere in the product, to see
what plan they are on, how much of their monthly allowance they have used, when their
subscription renews, or to cancel it without contacting support directly. This is not
a nice-to-have — it is a gap in the minimum feature set expected of any commercial
subscription software, and it sits entirely client-side: the backend capability this
phase needs already exists, is already authorized correctly, and is already frozen
and tested (Phase 6.6/6.9). Phase 7.3 is a client-only integration phase that surfaces
data the backend already computes.

This is distinct from, and does not touch, the deferred payment/checkout/billing-
provider integration (`NotImplementedBillingProvider`, still explicitly a placeholder)
or the future commercial website — per the standing project decision, this phase adds
core in-product functionality to the software itself, not to a website, and does not
implement or require any payment processing.

## 2. Current Baseline

Verified directly at the start of this pass: branch `master`, HEAD `2d58678`, working
tree clean. This is the confirmed, frozen Phase 7.2 state (provider credential
renewal, verified and frozen per the Phase 7.2 runtime-risk audit).

## 3. Repository / Architecture Assessment

Structure confirmed by direct inspection, not assumed:

```
src/
  VTTranslate.App/                    WPF customer client (net8.0-windows)
  VTTranslate.Core/                   Translation pipeline, Azure provider, diagnostics
  VTTranslate.Backend.Api/            Minimal-API host, Program.cs endpoint map
  VTTranslate.Backend.Application/    Application services (Billing, Devices,
                                       Entitlements, Identity, Profiles, ProviderAccess,
                                       Sessions, Usage)
  VTTranslate.Backend.Domain/         Entities (Account, Subscription, Plan,
                                       Entitlement, Device, TranslationSession,
                                       UsageRecord, BillingEvent, AuditEvent, Profile)
  VTTranslate.Backend.Infrastructure/ EF Core persistence, Billing (placeholder
                                       provider), Identity, ProviderAccess (Azure STS)
tests/
  VTTranslate.Core.Tests/             404 tests, deterministic + gated real-Azure
  VTTranslate.Backend.Tests/          312 tests (271 run, 41 Docker-gated Postgres)
  VTTranslate.App.Tests/              Client-side unit/integration tests
```

**Backend endpoint inventory** (from `Program.cs`, confirmed exhaustive):

| Endpoint | Purpose | Client consumes it today? |
|---|---|---|
| `GET /health/live`, `/health/ready` | Ops health checks | N/A (not a client concern) |
| `GET /devices`, `POST /devices`, `POST /devices/{id}/revoke` | Device licensing (6.7) | Yes |
| `GET /subscription` | Subscription status/plan/period/cancel flag | **No** |
| `POST /subscription/cancel` | Customer-initiated cancellation | **No** |
| `GET /entitlements` | Plan's configured limits/grants | **No** |
| `GET /usage` | Server-derived + client-reported usage for current period | **No** |
| `POST /webhooks/billing` | Billing provider webhook ingestion | N/A (server-to-server) |
| `POST /provider-access` | Short-lived Azure credential issuance (6.8) | Yes |
| `POST /translation-sessions`, `/heartbeat`, `/end` | Session accounting (6.9) | Yes |
| `GET /profile`, `PUT /profile` | Customer profile (7.0/7.1) | Yes |

Four of the ten customer-facing endpoints are fully built, authorized, and tested, and
are simply never called by the only client that exists. This is the gap this phase
closes.

**Client architecture** (`VTTranslate.App`): `MainViewModel` is the single view-model
driving `MainWindow.xaml`, already following an established pattern —
`IAutraxisApiClient` for all backend calls, `AutraxisApiException`/`ApiErrorCategory`
for uniform error handling, `Application.Current.Dispatcher.Invoke` for UI-thread
marshaling from background event handlers, and `ICommand`/`RelayCommand` for user
actions (see `SignOutCommand` at `MainViewModel.cs:187`). This phase's client work
fits directly into that existing pattern; it does not need a new architectural style.

**Known deferred items surfaced by earlier phases, explicitly checked against this
proposal:**
- Phase 6.6/6.2B: billing-provider integration (`NotImplementedBillingProvider`) —
  explicitly not decided/not implemented; **this phase does not touch it** (§8).
- Phase 7.2 §28 threat 7: backend-side rate limiting on `/provider-access` — an open,
  separately-scoped hardening item; unrelated to this phase's endpoints, not addressed
  here.
- Phase 7.0 §31 decision 2: trial gate parameters (rate limits, email verification) —
  a product-owner decision, unrelated to this phase.
- No installer/packaging (MSIX/WiX/ClickOnce), no production deployment
  configuration (Dockerfile/IaC) exist anywhere in the repository. Real gaps, but
  operational/distribution concerns, not a missing product capability — out of scope
  for this phase (§8), and each is a poor fit for "the next engineering phase" because
  neither changes what the software *does* for a customer, which is the gap this
  document is scoped to close first.

## 4. Production Gaps Identified

1. **No subscription visibility.** A customer cannot see their plan name, status
   (`Active`/`PastDue`/`GracePeriod`/`Canceled`/`Expired`), or renewal date anywhere
   in the product.
2. **No usage visibility.** A customer cannot see how many minutes/seconds of
   translation they have used this period, or how much their plan allows
   (`EntitlementKeys.UsageLimitSecondsPerPeriod`).
3. **No self-service cancellation.** `POST /subscription/cancel` exists and is
   tested, but nothing in the client can call it; a customer must go outside the
   product entirely to cancel.
4. **Opaque entitlement denial.** When a session is refused for
   `ApiErrorCategory.EntitlementDenied`, the customer sees only "Your plan does not
   currently allow this" — no indication of *why* (usage exhausted? device limit
   reached? subscription lapsed?) or what to do about it, even though `/entitlements`
   and `/usage` together already contain the answer.
5. **No device-limit visibility tied to entitlement.** `GET /devices` is already
   consumed by the client (device list/registration), but nothing correlates it with
   `EntitlementKeys.MaxActiveDevices` from `/entitlements` to explain *why* a device
   was denied registration.

Gaps 1–3 are the core of this phase. Gap 4 is a direct, low-risk consequence of having
1–3 in place (the same data that answers "what's my plan" also answers "why was I
denied") and is included as part of the same client surface. Gap 5 is flagged but
scoped as an explicit non-goal (§8) — it requires correlating two already-fetched
resources in the UI, not new backend or protocol work, and is deferred to avoid
scope creep into device-management UI redesign.

## 5. Recommended Phase 7.3

**Phase 7.3 — Customer Subscription, Entitlement & Usage Visibility**: a client-only
integration phase that adds the missing `IAutraxisApiClient` methods for the four
existing, frozen backend endpoints, and a new read-mostly UI surface (a "My Account"
panel/tab in the existing WPF shell) that displays subscription status, plan,
entitlements, current-period usage, and a cancel-subscription action — reusing the
exact API-client/error-handling/UI-threading patterns already proven in
`MainViewModel`.

### Why this is the correct next phase (not the next number, not an invented feature)

1. **It is the smallest, most direct closure of a confirmed customer-facing gap.**
   Every other candidate considered (§25) either duplicates already-decided-against
   scope (billing-provider integration, explicitly deferred), is not yet a product
   decision (mobile clients), or is an operational concern with no dependency
   ordering forcing it now (deployment/installer — can be done at any point before
   general availability, does not block any other engineering work).
2. **Zero backend risk.** No endpoint changes, no schema changes, no changes to any
   frozen phase (6.5–7.2). The work is additive on the client only.
3. **It directly reduces support burden and account-state confusion** the moment
   the product has real paying customers — visibility into "why was I denied" and
   "how do I cancel" is table-stakes before wider commercial release, independent of
   whether the commercial website/checkout exists yet (a customer can be on a plan
   today via the backend's existing trial/manual-provisioning paths without any
   website).
4. **It builds entirely on frozen, already-tested architecture** — `Account`
   resolution, `Subscription`/`Entitlement`/`UsageRecord` domain model, the existing
   `IAutraxisApiClient`/`ApiErrorCategory` client pattern, and the existing
   `AccountResolutionMiddleware` authorization boundary — with no new concepts
   introduced anywhere in the stack.
5. **It resolves the single largest gap between "what the backend can prove about a
   customer's account" and "what the customer can see about their own account,"**
   which is the same class of gap Phase 7.1 closed for authentication (backend could
   authenticate; client didn't use it correctly) and Phase 7.2 closed for session
   continuity (backend could reissue credentials; client never asked).

## 6. Problem Statement

The AUTRAXIS Windows client is production-ready for translation itself (Phases
6.5–7.2) but exposes no way for a signed-in customer to see or manage their own
subscription, entitlements, or usage — despite the backend already computing and
authorizing access to all of that data. This blocks any real commercial usage of the
product beyond a fully manual, out-of-band support relationship with every customer.

## 7. Goals

- Add `IAutraxisApiClient` methods for `GET /subscription`, `GET /entitlements`,
  `GET /usage`, and `POST /subscription/cancel`, following the exact DTO/error-mapping
  pattern already used for `GetProfileAsync`/`GetDevicesAsync`.
- Add a "My Account" view (new WPF view/tab, reachable from the existing shell)
  showing: plan name, subscription status, current period end date, cancel-at-period-
  end flag, a cancel-subscription action (with confirmation), and current-period usage
  (`serverDerivedSeconds`) against the plan's `UsageLimitSecondsPerPeriod` entitlement
  where present.
- Improve the existing `EntitlementDenied` error message path to reference the
  now-available "My Account" view rather than a bare generic string.
- Cover all new client logic with unit tests using the existing `FakeAutraxisApiClient`
  pattern (already present in `tests/VTTranslate.App.Tests`), consistent with Phase
  7.1/7.2's testing conventions.

## 8. Non-Goals

- **No payment/checkout/billing-provider integration.** `NotImplementedBillingProvider`
  remains untouched; this phase reads existing subscription state, it does not create
  or modify how a subscription is purchased or paid for.
- **No commercial website work.** Per standing project decision, website/payment work
  does not interrupt the software engineering sequence; this phase is entirely inside
  the existing WPF client and backend.
- **No backend endpoint, DTO, or schema changes.** All four endpoints already exist
  with the exact response shapes needed (§13/§14 confirm no gaps).
- **No changes to any frozen phase's behavior** (6.5 Persistence, 6.6 Billing
  lifecycle logic itself, 6.7 Device licensing, 6.8 Provider access, 6.9 Usage/session
  accounting, 7.0 Identity, 7.1 Authentication, 7.2 Credential renewal) — this phase
  only adds new client-side callers of already-frozen, already-authorized endpoints.
- **No device-limit/entitlement correlation UI** (gap 5, §4) — deferred as a distinct,
  smaller follow-up once this phase's core surface exists (§28).
- **No plan upgrade/downgrade/purchase flow** — `Plan.IsPubliclyPurchasable` and
  `PriceHandle` remain unset/unused; selecting or changing a plan requires a billing
  provider (explicitly out of scope, §8) and is a website/checkout concern per the
  standing project decision.
- **No backend-side rate limiting** on any endpoint — remains Phase 7.2 §28's open,
  separately-scoped hardening item, untouched here.
- **No mobile (Android/iOS) work** — no architectural dependency exists between this
  phase and a mobile client; proposing mobile now would be premature per the standing
  guidance to only introduce it when the roadmap actually calls for it.
- **No installer/packaging or production deployment work** — a real, separately
  identified gap (§3), deliberately out of scope here since it has no dependency
  relationship with this phase and does not block it.
- **No push/webhook-driven live update of subscription state in the client** — the
  client fetches on-demand (view opened / after a relevant action); a real-time
  subscription-changed notification channel is not built (§28).

## 9. Architecture

```
MainWindow (WPF shell)
    ↓ new: "My Account" navigation entry (alongside existing session/settings UI)
AccountViewModel (new, sibling to MainViewModel — not a MainViewModel God-object addition)
    ↓ on view-activation (and after Cancel action)
IAutraxisApiClient (existing interface, extended)
    ├─ GetSubscriptionAsync()      → GET /subscription
    ├─ GetEntitlementsAsync()      → GET /entitlements
    ├─ GetUsageAsync()             → GET /usage
    └─ CancelSubscriptionAsync(immediate) → POST /subscription/cancel
        ↓ (existing AutraxisApiClient implementation — same HttpClient, same bearer-
           token acquisition, same 401-bounded-retry, same ApiErrorCategory mapping
           already proven in Phase 7.1)
AUTRAXIS Backend (frozen, Phase 6.6/6.9 — zero changes)
    ├─ GET  /subscription      → AccountResolutionMiddleware → SubscriptionLifecycleService.ReconcileTimeBasedTransitionsAsync → ISubscriptionRepository
    ├─ POST /subscription/cancel → SubscriptionLifecycleService.ApplyCustomerCancellationAsync
    ├─ GET  /entitlements      → IPlanRepository.GetEntitlementsAsync(subscription.PlanId)
    └─ GET  /usage             → IUsageService.GetSummaryAsync(accountId, periodBucket)
```

`AccountViewModel` is deliberately a new, separate view-model rather than an addition
to the already-substantial `MainViewModel` (674 lines) — it has its own lifecycle (
load-on-activate, not load-on-session-start) and no coupling to the translation
pipeline, mirroring the separation already present between `MainViewModel` and
`AuthenticationService`.

## 10. Component Responsibilities

| Component | Responsibility | New or existing |
|---|---|---|
| `IAutraxisApiClient` / `AutraxisApiClient` | Four new methods; same HTTP/error/auth machinery | Existing, extended |
| `SubscriptionDto`, `EntitlementsDto`, `UsageSummaryDto` | Client-side DTOs mirroring the existing anonymous JSON response shapes | New (thin, data-only) |
| `AccountViewModel` | Loads and exposes subscription/entitlement/usage state; owns the cancel command | New |
| `AccountView` (XAML) | Renders the "My Account" panel | New |
| `MainViewModel.MapApiErrorToMessage` | Extended message for `EntitlementDenied` referencing "My Account" | Existing, extended (message text only) |
| Backend (`Program.cs`, `SubscriptionLifecycleService`, `IUsageService`, `IPlanRepository`) | Unchanged | Existing, frozen |

## 11. Data Flow

1. Customer navigates to "My Account" (or `EntitlementDenied` error offers a link to
   it) while signed in.
2. `AccountViewModel.LoadAsync()` calls `GetSubscriptionAsync()`,
   `GetEntitlementsAsync()`, `GetUsageAsync()` concurrently (three independent,
   already-authorized GETs — no new coordination logic needed, each already resolves
   the account from the bearer token server-side).
3. Each response is mapped to its DTO and bound to the view (status, plan, period end,
   cancel flag, usage-vs-limit).
4. If the customer invokes Cancel: confirmation dialog (existing WPF pattern) →
   `CancelSubscriptionAsync(immediate)` → re-fetch `GetSubscriptionAsync()` to reflect
   the authoritative post-cancel state (never assume the client's optimistic view is
   correct — same "server is the source of truth" principle already used throughout
   this codebase for entitlement/session state).
5. No data from this flow is ever written back to `TranslationSession`/`UsageRecord`
   — this phase is strictly read-plus-cancel against existing backend state; it
   introduces no new write path into the accounting model that Phase 6.9 owns.

## 12. API / Contract Changes

**None.** All four endpoints, their request/response shapes, and their authorization
model are confirmed unchanged and sufficient as inspected in `Program.cs` (§3, §13).

## 13. Client Changes

- `IAutraxisApiClient`: add `GetSubscriptionAsync`, `GetEntitlementsAsync`,
  `GetUsageAsync`, `CancelSubscriptionAsync` — following the exact signature/DTO
  pattern of `GetProfileAsync`/`UpdateProfileAsync`.
- New DTOs in `VTTranslate.App/Api/Dtos.cs` (alongside `ProviderAccessGrantDto` etc.):
  `SubscriptionDto(string Status, Guid PlanId, DateTimeOffset CurrentPeriodStart, DateTimeOffset CurrentPeriodEnd, bool CancelAtPeriodEnd)`,
  `EntitlementsDto(string SubscriptionStatus, IReadOnlyDictionary<string, string> Entitlements)`,
  `UsageSummaryDto(string PeriodBucket, double ServerDerivedSeconds, double ClientReportedSeconds)`.
- New `AccountViewModel` + `AccountView.xaml`, wired into `MainWindow`'s existing
  navigation/shell.
- `MainViewModel.MapApiErrorToMessage`: extend the `EntitlementDenied` case's message
  text only (no behavior change to the switch itself).
- `FakeAutraxisApiClient` (test double, `tests/VTTranslate.App.Tests`): extend with
  scriptable responses for the four new methods, mirroring the existing
  `ProviderAccessResponses` pattern added in Phase 7.2.

## 14. Backend Changes

**None.** This is the central architectural property of this phase — confirmed by
direct inspection of `Program.cs` that all four required endpoints already exist,
already authorize via the unchanged `AccountResolutionMiddleware`, and already return
everything the client-side design (§9–§13) needs.

## 15. Persistence / Migration Impact

**None.** No entity, repository, or migration changes — `Subscription`, `Entitlement`,
`Plan`, `UsageRecord` are read-only from this phase's perspective (cancellation writes
through the existing, unchanged `SubscriptionLifecycleService.ApplyCustomerCancellationAsync`,
already covered by Phase 6.6's own persistence tests).

## 16. Security Model

- All four endpoints already require authorization (`RequireAuthorization()` in
  `Program.cs`) and resolve the account exclusively from
  `AccountResolutionMiddleware`'s server-side-resolved `Account` — never from any
  client-supplied identifier. This phase introduces no new trust boundary; it is a
  new caller of an already-correct boundary.
- The cancel action is customer-initiated and account-scoped identically to every
  other authenticated write in this client (e.g., `RevokeDeviceAsync`) — no new
  authorization concept, no new risk of cross-account action, since the account is
  never client-supplied.
- No provider tokens, payment details, or credentials are displayed or handled by this
  phase — subscription status/plan/usage data is not payment-sensitive information
  (no card numbers, no billing-provider secrets ever reach the client, consistent
  with `NotImplementedBillingProvider` remaining the only billing-provider surface,
  entirely server-side).
- Usage/entitlement values are display-only in the client — this phase does not, and
  must not, introduce any client-side entitlement enforcement or caching that could be
  treated as authoritative; every session-start/provider-access decision continues to
  be re-verified server-side exactly as today (Phase 6.8/6.9, unchanged).

## 17. Failure Modes

| Scenario | Behavior |
|---|---|
| `GET /subscription` returns 404 (`no_subscription`) | View shows "No active subscription" state, not an error — a valid, expected account state (e.g. a newly provisioned account before any plan assignment) |
| Any of the three GETs fails transiently (network) | View shows a retry affordance; does not block the rest of the application (translation session start/stop is entirely independent of this view) |
| `POST /subscription/cancel` fails (`AutraxisApiException`) | Existing `ApiErrorCategory`-based message mapping surfaces the failure; subscription state is re-fetched regardless (never assume the cancel succeeded client-side) |
| Customer is offline / AUTRAXIS backend unreachable while viewing "My Account" | Same `NetworkUnavailable`/`ServiceUnavailable` categories already defined; no new failure category needed |
| `GET /entitlements` returns a plan with no `UsageLimitSecondsPerPeriod` key | Usage view shows "used: Xs" with no limit denominator, rather than assuming a default — the entitlement dictionary is deliberately open-ended (§Entitlement domain doc comment) and the client must not invent a default limit |

## 18. Concurrency / Lifecycle Considerations

- `AccountViewModel` has its own load/refresh lifecycle, entirely decoupled from
  `MainViewModel`'s translation-session lifecycle (`_cts`, `StartAsync`/`StopAsync`) —
  it must not share state, locks, or cancellation tokens with the translation pipeline,
  matching this codebase's established pattern of strict separation between concerns
  (e.g., Phase 7.2's per-direction isolation).
- The three read calls (`GetSubscriptionAsync`/`GetEntitlementsAsync`/`GetUsageAsync`)
  are independent and safely concurrent (each a separate authenticated GET; no shared
  mutable client-side state between them).
- No background polling loop is introduced (§8 non-goal) — data is fetched on
  view-activation and after the cancel action only, avoiding any new long-running
  timer/loop class to reason about alongside the existing heartbeat/renewal loops.

## 19. Observability

- Reuses the existing `IDiagnosticLogger` — logs view-load attempts/results and the
  cancel action's outcome (category-level only: succeeded/failed/category), never the
  subscription/plan/usage values themselves (consistent with this codebase's existing
  "log metadata, not content" convention already applied to translation text and
  provider tokens).
- No new logging infrastructure, sink, or telemetry pipeline — out of scope; the
  existing `FileDiagnosticLogger`/`NullDiagnosticLogger` pair is sufficient for this
  phase's scope.

## 20. Testing Strategy

- Unit tests for the four new `IAutraxisApiClient` methods' DTO mapping and
  `ApiErrorCategory` translation, following the exact pattern already used for
  `GetProfileAsync`/`GetDevicesAsync` tests.
- `AccountViewModel` tests using `FakeAutraxisApiClient` (extended per §13): loading
  states, no-subscription state, cancel-success/cancel-failure paths, entitlement
  dictionary with and without a usage-limit key.
- Regression: full existing `VTTranslate.App.Tests`, `VTTranslate.Core.Tests`,
  `VTTranslate.Backend.Tests` suites must remain green — no backend or Core change is
  expected to touch either of the latter two at all.
- No new real-Azure or real-Postgres validation tier is needed — this phase touches
  neither Azure Speech nor the database schema.

## 21. CI / Verification Strategy

- No new workflow required. The existing `phase-7.1-client-verification.yml` already
  builds and runs `VTTranslate.App.Tests` (which will include the new tests) on a
  clean `windows-latest` runner, and its Release `/warnaserror` build already covers
  any new client code added under `src/VTTranslate.App`.
- `phase-6.5-db-verification.yml` continues to cover `VTTranslate.Backend.Tests` and
  `VTTranslate.Core.Tests`, unaffected since neither changes.
- If desired, the existing "no secrets in client source" CI security-scan step
  (`phase-7.1-client-verification.yml`) naturally extends to the new files without any
  workflow edit, since it already globs `src/VTTranslate.App` recursively.

## 22. Backward Compatibility

Fully additive — no existing endpoint, DTO, view, or command is removed or changed in
shape. A customer on an older client build is unaffected; a customer on the new build
simply gains a new view. No versioning concern (mirrors Phase 6.8's own "no versioning
concern" reasoning for `/provider-access`, since nothing existing changes shape).

## 23. Rollout / Migration Strategy

No migration. No feature flag is architecturally required (the new view is additive
and inert until navigated to), though the product owner may choose to gate its
navigation-entry visibility if a staged rollout is desired — that is a product
decision, not an architectural requirement (§30).

## 24. Risks

| Risk | Mitigation |
|---|---|
| Client-side entitlement/usage display drifts from server truth if cached improperly | No caching — always fetch fresh on view-activation and after cancel; no persisted local copy |
| Customer confusion if `/entitlements`' key set changes without a client update (new entitlement keys added server-side) | Client renders only well-known keys it explicitly understands (e.g. usage limit) and does not attempt to enumerate/display arbitrary unknown keys — degrades gracefully, matching the entitlement model's own "open-ended, not exhaustive" design intent |
| Scope creep into plan-change/purchase UI | Explicitly a non-goal (§8); flagged here to keep implementation bounded |
| Adding a second, parallel "API client extension" pattern instead of extending the existing one | Mitigated by following `IAutraxisApiClient`'s existing method/DTO shape exactly, as this document specifies (§13) |

## 25. Alternatives Considered

| Alternative | Why rejected / deferred |
|---|---|
| **Billing-provider (Stripe/similar) integration next** | Explicitly deferred by standing project decision ("software completed before commercial website"); `NotImplementedBillingProvider` is a deliberate placeholder, not an oversight — implementing it now would front-load payment-processing risk before the product itself is feature-complete |
| **Mobile (Android/iOS) client next** | No architectural dependency pulls it forward; per standing guidance, mobile should only be proposed when the roadmap calls for it — nothing in the current gap analysis does |
| **Backend rate-limiting hardening (Phase 7.2 §28 threat 7) next** | A real, valid open item, but it is a defensive hardening task with no customer-facing capability gain and no dependency relationship forcing it ahead of a confirmed customer-facing functional gap |
| **Deployment/installer/packaging work next** | A real, valid gap (§3), but purely operational — does not change what the software does for a customer and has no ordering dependency with this phase; better sequenced closer to actual release readiness |
| **Device-limit/entitlement correlation UI (gap 5) as its own phase** | Folded in as a §8 non-goal/follow-up rather than its own phase — it is a small UI enhancement on top of data this phase already fetches, not large enough to justify a separate phase number |
| **A generic "customer diagnostics/telemetry upload" phase** | No evidence this is currently blocking anything; `FileDiagnosticLogger` already provides local diagnostics; server-side telemetry ingestion is a larger, separate architectural decision (new endpoint, new storage, new privacy review) not justified by any concrete gap found in this assessment |

## 26. Acceptance Criteria

- A signed-in customer can view their subscription status, plan, current period end
  date, and cancel-at-period-end flag without contacting support.
- A signed-in customer can view their current-period usage, and — where the plan
  defines `UsageLimitSecondsPerPeriod` — see it against that limit.
- A signed-in customer can cancel their subscription (immediate or at-period-end) from
  within the product, with confirmation, and see the resulting state reflected
  immediately after.
- An `EntitlementDenied` session-start failure surfaces a message that points the
  customer toward the "My Account" view rather than a dead-end generic string.
- Zero changes to any endpoint contract, any frozen-phase behavior, or any persisted
  schema — verified by diff review against the exact same "frozen file" discipline
  used in the Phase 7.2 audit.
- Full existing test suites remain green; new tests cover the new client methods and
  view-model per §20.

## 27. Definition of Done

- Implementation complete per §9–§13.
- All new and existing tests passing (Core/Backend/App), matching or exceeding the
  Phase 7.2 baseline counts with the new tests added.
- Full solution build clean (Debug + Release), WPF Release `/warnaserror` clean.
- Secret scan clean (no new secret-shaped values — none expected, since this phase
  introduces no credentials).
- No frozen-phase (6.5–7.2) file exhibits an unintended behavioral change — diff
  review confirms changes are confined to `VTTranslate.App` (plus its tests) and, if
  DTOs are added there, `VTTranslate.App/Api/Dtos.cs` only.
- CI (`phase-7.1-client-verification.yml`) green on the exact implementation commit.
- Final report documents baseline, files changed, tests, security, and regression
  exactly as the Phase 7.2 report did.

## 28. Future Follow-Up Work

- Device-limit ↔ `MaxActiveDevices` entitlement correlation UI (gap 5, §4) — small,
  natural follow-on once this phase's data-fetching plumbing exists.
- Real-time subscription-state change notification (e.g., a webhook-driven push to an
  already-open client) — explicitly deferred (§8); no evidence of customer need yet
  without a live commercial customer base to observe.
- Once a billing provider is eventually integrated (separate, explicitly-deferred
  phase), this view is the natural place to add a plan-change/purchase entry point —
  not built now, but this phase's "My Account" surface is the correct future host for
  it, avoiding a second, competing UI surface later.
- Installer/packaging and production deployment/hosting (§3, §25) remain open,
  real gaps tracked for a future, separately-scoped phase — not blocked by, and not
  blocking, this one.

## 29. Decision Matrix

| # | Decision | Options | Recommendation | Rationale | Owner |
|---|---|---|---|---|---|
| 1 | Whether "My Account" is a new window/dialog or an in-shell tab/panel | Modal dialog / new top-level window / in-shell navigation panel | In-shell navigation panel | Matches the single-window WPF shell already in place; avoids introducing a second window-lifecycle to manage alongside the existing one | Engineering (implementation detail) |
| 2 | Whether to gate the new view behind a feature flag for staged rollout | Flag / no flag | No flag required architecturally; optional per product timing | The view is inert until navigated to and touches no shared state — a flag adds complexity with no correctness benefit, only a possible go-to-market timing benefit | Product owner |
| 3 | Whether `EntitlementDenied` messages should deep-link directly into "My Account" or just mention it | Deep link / text mention only | Text mention only for this phase; deep-link as a fast-follow | Deep-linking requires wiring a cross-view navigation command not yet established in this shell; keeping this phase's client-navigation surface minimal avoids scope creep | Engineering |

## 30. Open Product/Architecture Decisions

1. **Exact wording/copy for subscription-status display** (e.g., how to phrase
   `PastDue`/`GracePeriod` to a customer without alarming or confusing them) — a
   product-owner/UX decision, not an engineering one; not invented here.
2. **Whether "My Account" should be reachable pre-sign-in or only post-sign-in** —
   given every one of the four endpoints requires authentication, this document
   assumes post-sign-in-only, but confirms this is a product decision worth an
   explicit yes before implementation.
3. **Whether staged rollout (§29 decision 2) is wanted** — timing decision, not an
   architecture blocker either way.

None of these block starting implementation of the core, endpoint-integration
capability described in this document — they affect presentation/timing details only.

## 31. Implementation Sequence

1. Extend `IAutraxisApiClient`/`AutraxisApiClient` with the four new methods and DTOs
   (§13), with unit tests against a fake `HttpMessageHandler` (or equivalent, matching
   the existing test pattern for `AutraxisApiClient`).
2. Extend `FakeAutraxisApiClient` with scriptable responses for the four methods.
3. Build `AccountViewModel` (load/refresh/cancel logic) with unit tests using the
   extended fake.
4. Build `AccountView.xaml` and wire navigation into `MainWindow`.
5. Extend `MainViewModel.MapApiErrorToMessage`'s `EntitlementDenied` case text.
6. Full regression: Core/Backend/App test suites, Debug+Release build,
   `/warnaserror`, secret scan, frozen-phase diff review (mirroring the exact Phase
   7.2 verification matrix).
7. Manual verification in a real running client against a real (or locally hosted)
   backend instance with a test account in each subscription state
   (`Active`/`PastDue`/`GracePeriod`/`Canceled`/no-subscription), since this phase is
   UI-facing and automated tests alone do not confirm visual/UX correctness.
8. Final report, commit, push — same discipline as Phase 7.2's commit gate (only
   after every prior step genuinely passes).

## 32. Production Readiness Gate

Phase 7.3 should be considered ready to implement now — it has no blocking product
decision (§30 items are presentation/timing refinements, not gates), no backend
dependency beyond what is already frozen and tested, and directly closes a confirmed,
evidence-backed customer-facing gap. It should be considered **done and frozen** only
once §26/§27's acceptance criteria and definition of done are fully met and verified
with the same rigor as the Phase 7.2 runtime-risk audit — a follow-up audit of this
phase, once implemented, is recommended before declaring it frozen, consistent with
the project's established two-step "implement, then independently audit" discipline.
