# Phase 6.7 — Device Licensing Policy & Pooling Decision

**Status: DESIGN ONLY.** No production code, tests, migrations, or API implementation were created or modified in this phase. This document resolves the open **D6** decision (`docs/phase-6.2-architecture-decision-matrix.md` §D6) and the `DeviceStatus.Pending` question flagged in `docs/phase-6.3-backend-foundation.md` §18, and specifies everything needed to implement Phase 6.7 once approved. Labeling convention, matching prior phase documents: **EXISTING DECISION** (already approved, Phase 6.1–6.6), **PROPOSED DECISION** (this document's recommendation, not yet approved), **UNRESOLVED PRODUCT DECISION** (requires your sign-off), **IMPLEMENTATION RECOMMENDATION** (an engineering approach, not a product question).

---

## 1. D6: Pooled vs. Per-Platform Device Licensing

### 1.1 The two models, defined precisely

**Model A — Pooled (single limit across all platforms).** `MaxActiveDevices` is one number that counts every non-revoked `Device` row for the account, regardless of `Platform`. A customer with a limit of 3 could have 3 Windows devices, or 1 Windows + 1 Android + 1 iOS, or any other combination summing to 3.

**Model B — Per-platform (separate sub-limits).** Each `Platform` value has its own limit — e.g., `MaxActiveDevices:Windows = 2`, `MaxActiveDevices:Android = 1`, `MaxActiveDevices:iOS = 1`. A customer could have 2 Windows devices AND 1 Android AND 1 iOS simultaneously (4 total), but never 3 Windows devices even if they have zero mobile devices registered.

### 1.2 Concrete customer examples

| Scenario | Model A (pooled, limit=3) | Model B (per-platform: Win=2, Android=1, iOS=1) |
|---|---|---|
| Customer has 1 desktop PC, wants a laptop too, no phone | Allowed (2/3 used) | Allowed (2/2 Windows used) |
| Customer has 2 Windows PCs, wants to add their phone | Allowed (3/3 used) | Allowed (phone is a different platform bucket) |
| Customer has 3 Windows PCs (work, home, laptop), no phone ever | Allowed (3/3 used) | **Denied** — the 3rd Windows device exceeds the Windows sub-limit even though no mobile device exists |
| Customer has 1 Windows PC + 1 Android + 1 iOS (e.g., testing both mobile OSes) | Allowed (3/3 used) | Allowed (each within its own bucket) |
| Customer wants 2 Android phones (personal + work), no desktop | Allowed (2/3 used) | **Denied** — Android sub-limit is 1 |

### 1.3 Tradeoffs

**Model A (pooled):**
- Simpler to explain to customers ("you get 3 devices, any kind").
- Simpler to implement and test — one counter, one comparison.
- Risk: a customer who registers many desktop instances (e.g., multiple VMs, family members sharing a Windows license) can exhaust the pool before ever installing a mobile app, then be surprised when mobile registration is denied.
- Matches Phase 6.1/6.2B's original, undifferentiated `Device` model exactly as designed — no new concept needed.

**Model B (per-platform):**
- Guarantees mobile access is never crowded out by desktop usage (or vice versa) — predictable per-platform availability.
- More complex: requires either multiple entitlement keys or a structured value, more edge cases (what if a plan defines a Windows limit but no Android/iOS limits — does that mean 0, or unlimited, or "same as Windows"?), more test surface.
- Harder to explain to customers ("you get 2 Windows + 1 Android + 1 iOS" is a less intuitive pitch than "you get 3 devices").
- No current product requirement or customer feedback (this project has zero paying customers yet — Phase 6.6 froze billing before any real subscriber existed) justifies the added complexity today.

### 1.4 Windows/Android/iOS implications

Today, **only Windows ships** (`VTTranslate.App`). Android and iOS are unbuilt (Phase 6.10+ per `docs/phase-6.2-architecture-decision-matrix.md` §9). This means:
- Any per-platform sub-limit for Android/iOS today would be **pure speculation** — there is no mobile client to generate real registration patterns, no data on how customers will actually use multiple devices across platforms.
- Model A requires zero mobile-specific configuration to ship Phase 6.7 today; Model B requires deciding Android/iOS sub-limits now, for platforms that don't exist yet, which risks locking in an uninformed number the same way Phase 6.2B already warned against for trial/grace values ("do not hard-code... configure per environment/plan").
- Switching from A to B later (once mobile ships and real usage data exists) is a strictly **additive** entitlement-key change (§3 below) — it does not require a schema or `Device` entity change either way, since `Platform` is already a first-class field on `Device`. Switching later costs nothing structurally.

### 1.5 Recommendation — **PROPOSED DECISION**

**Adopt Model A (pooled) for Phase 6.7.** Rationale: no mobile client exists to justify per-platform numbers today; pooled is simpler to implement, test, and explain; the current code's de-facto behavior already IS pooled (§1.6), so this ratifies rather than reverses existing behavior; and the migration path to Model B later is additive, not a rewrite. **This recommendation requires your explicit approval — see §A/B below** — it is not self-approving merely because it matches the current code.

### 1.6 Explicit non-silent-treatment of current behavior

**IMPORTANT**: `DeviceRegistrationService.GetMaxActiveDevicesAsync` today counts `existing.Count(d => d.Status != DeviceStatus.Revoked)` — i.e., it already behaves as Model A (pooled), with zero `Platform` filtering. **This was never an approved product decision** — it is an unexamined implementation default from Phase 6.3, written before D6 existed as a named question. This document does not treat that default as authoritative; §A below asks you to approve Model A as a **decision**, not merely notes that the code happens to already do it.

---

## 2. `DeviceStatus.Pending`

### 2.1 Current state

`DeviceStatus` enum (`Enums/DeviceStatus.cs`) defines `Pending | Authorized | Revoked`. `DeviceRegistrationService.RegisterDeviceAsync` **never produces `Pending`** — every registration under the limit is immediately set to `Authorized`. No code path anywhere reads or transitions a `Pending` device. It exists in the enum, unused, since Phase 6.3.

### 2.2 Does it have a legitimate future purpose?

A manual-approval device flow (admin/super-admin must approve a new device before it's usable) is a real pattern used by some licensing products (e.g., "a new device requires approval within 24 hours" for fraud/abuse resistance). However:
- No Phase 6.1–6.6 document ever proposed this flow. It was not designed, only left as an unused enum placeholder.
- No admin API exists to approve a `Pending` device (the admin/super-admin billing scope was deliberately kept minimal in Phase 6.6 — no generic admin-mutation endpoints).
- Building this now would be inventing new product scope during a phase whose job is to resolve an *existing* open question, not add a new one.

### 2.3 Recommendation — **PROPOSED DECISION**

**Do not build a `Pending`-approval flow in Phase 6.7.** Two sub-options for the enum member itself:
- **(a) Retain, unused, documented as reserved** — zero migration cost (it's a string-converted enum value in Postgres already; an unused enum member costs nothing at the database level), keeps the option open if a future phase decides device-approval is worth building, and avoids a needless enum-value removal (which would require a migration touching the `devices.Status` check, however trivial).
- **(b) Remove `Pending` entirely** — slightly cleaner code, but requires a migration statement (even if just documentation, since Postgres stores it as a string via `HasConversion<string>()`, so no `CHECK` constraint exists to alter — removing the C# enum member is the only change needed) and forecloses the option without a documented reason to do so.

**Recommend (a): retain, unused, documented as reserved for a possible future manual-device-approval phase — not built now.** This costs nothing and matches this project's established pattern of leaving genuinely undecided things open rather than foreclosing them (e.g., how PO-D4/PO-D5 left trial/grace *values* configurable rather than picking one). **Requires your approval — see §A/B.**

---

## 3. Entitlement Representation (Model A, pooled)

### 3.1 Existing decision, unchanged

`MaxActiveDevices` (already defined in `EntitlementKeys`) continues to mean exactly what it means today: the pooled maximum count of non-revoked devices for an account, read from the account's current subscription's plan via `IPlanRepository.GetEntitlementsAsync`. **No new entitlement key is introduced for the pooled model** — Model A requires zero schema or entitlement-key change; this is the primary reason it's the lower-cost choice for this phase.

### 3.2 If Model B is ever approved instead (documented for completeness, NOT proposed now)

Per-platform limits would use additional, distinctly-named keys following the existing flat key/value shape — e.g., `MaxActiveDevicesWindows`, `MaxActiveDevicesAndroid`, `MaxActiveDevicesIOS` — never a structured/JSON value in a single key, to stay consistent with every other entitlement (`GracePeriodDays`, `PastDueGraceDays`, etc. are all single scalar values). This is documented here only so a future Model-B migration has a concrete, consistent naming convention ready — **it is not part of this phase's implementation scope.**

### 3.3 Behavior for missing, malformed, zero, and negative values — **PROPOSED DECISION, extends existing pattern**

`DeviceRegistrationService.GetMaxActiveDevicesAsync` already implements fail-closed behavior for the *missing subscription* and *missing entitlement* cases (returns `1`, never unlimited). This document proposes making the full fail-closed contract explicit and exhaustive, exactly mirroring `EntitlementService`'s Phase 6.6 exhaustiveness fix:

| Condition | Behavior |
|---|---|
| No subscription exists | `1` (existing behavior, unchanged) |
| Entitlement key missing from plan | `1` (existing behavior, unchanged) |
| Value present, parses as a positive integer | Use that value (existing behavior, unchanged) |
| Value present, fails `int.TryParse` (malformed) | `1` — fail closed, never unlimited (existing behavior, unchanged — `int.TryParse` returning false already falls to the `return 1` branch) |
| Value present, parses as `0` | **New, explicit**: treat as a genuine "no devices permitted" entitlement (e.g., an account fully suspended from device use without suspending the whole account) — `0` is a valid, deliberate configuration, not an error. `RegisterDeviceAsync` denies every registration attempt (`activeCount >= maxDevices` is true at `0 >= 0`). |
| Value present, parses as negative | **New, explicit**: treat identically to malformed — fail closed to `1`. A negative device limit has no legitimate meaning; silently allowing it to make the `>=` comparison always true (denying everything) would be accidentally-correct-by-coincidence, not a deliberate design — safer to explicitly clamp to the same fail-closed default as malformed input. |

This is presented as a **proposed clarification of already-fail-closed behavior**, not a new risk — no case above was ever able to produce an *open* (unlimited) outcome, even before this document. It only makes the `0` and negative cases explicit and intentional rather than incidental.

---

## 4. Device API Contract

All four Phase 6.6-style principles apply unchanged: account derived exclusively from `AccountResolutionMiddleware`, never from client input; no endpoint returns provider/internal secrets; every write is auditable; every route sits behind the existing `RequireAuthorization()`.

| Method/Route | Purpose | Request | Response (200/201) | Status codes | Notes |
|---|---|---|---|---|---|
| `GET /devices` | List the caller's own devices | none | `[{ id, platform, displayName, status, registeredAt, lastSeenAt, revokedAt }]` | 200 | Never includes another account's devices (§5). Empty array, not 404, if the account has none. |
| `POST /devices` | Register a new device | `{ platform: "Windows"\|"Android"\|"iOS", displayName?: string }` | `201` `{ id, platform, displayName, status, registeredAt }` | 201 (created), 403 (`device_limit_exceeded`, existing `DeviceLimitExceededException`), 400 (malformed platform value) | Device `Id` is server-generated (`Guid.NewGuid()`, existing behavior) — **never client-supplied**, preserving the existing "device identity is app-generated, not hardware-derived" decision (§5). |
| `POST /devices/{id}/revoke` | Revoke the caller's own device | none | `200` `{ id, status: "Revoked", revokedAt }` | 200, 404 (`device_not_found` — device doesn't exist OR belongs to another account; **same response for both**, see §5), idempotent 200 if already revoked | Reuses existing `DeviceRegistrationService.RevokeDeviceAsync`, already idempotent (`if (device.Status == DeviceStatus.Revoked) return;`). |

**Validation rules:**
- `platform` must be one of the three known `DevicePlatform` enum values (case-insensitive match acceptable); anything else → `400`.
- `displayName` is optional, free-form, length-capped (recommend reusing `Device.DisplayName`'s existing `HasMaxLength(200)` as the validation bound — no new constant needed).

**Idempotency:**
- `POST /devices` is **not** idempotent by design (each call registers a genuinely new device) — this matches how `POST /subscription/cancel` in Phase 6.6 was the only mutating endpoint and was likewise not given an idempotency key, since the underlying operation (create vs. cancel) has no natural retry-safe key without inventing a client-supplied device identifier, which would violate the "device ID is server-generated" rule.
- `POST /devices/{id}/revoke` **is** naturally idempotent (revoking an already-revoked device is a no-op 200, not an error) — this already matches `RevokeDeviceAsync`'s existing implementation.

**Account isolation** — see §5.

---

## 5. Authorization / Security

**EXISTING DECISION, reused unchanged**: every route above sits behind `RequireAuthorization()`; the account is derived exclusively from `AccountResolutionMiddleware`'s resolved `Account` (`HttpContext.Items[AccountResolutionMiddleware.AccountItemsKey]`), never from a route parameter, request body field, or query string — identical to every Phase 6.6 customer endpoint (`GET /subscription`, `POST /subscription/cancel`, etc.). **No second identity or account-resolution mechanism is introduced.**

**Cross-account access prevention**: `GET /devices` filters by the resolved account's ID via the existing `IDeviceRepository.ListByAccountAsync(accountId, ct)` — structurally cannot return another account's rows since the query itself is scoped. `POST /devices/{id}/revoke` must load the device via `GetOwnedDeviceAsync` (already implemented in `DeviceRegistrationService`, throws `DeviceNotOwnedException` if `device.AccountId != accountId`) — the API layer must map this exception to a `404`, **not** a `403`, so a customer cannot distinguish "this device ID doesn't exist" from "this device ID belongs to someone else" (preventing device-ID enumeration by response-code side-channel — the same non-enumeration discipline Phase 6.4/6.6 already applied to account/subscription lookups).

**Hardware fingerprinting** — **EXISTING DECISION, reconfirmed, unchanged**: device `Id` remains `Guid.NewGuid()`, generated server-side at registration; no MAC address, disk serial, machine name, or any hardware-derived value is ever read, stored, or accepted from the client (Phase 6.2B §6's explicit, deliberate exclusion). Phase 6.7 introduces nothing that touches this boundary.

---

## 6. Concurrency and Licensing Correctness

### 6.1 The race, precisely

`DeviceRegistrationService.RegisterDeviceAsync` performs a **read-then-write** sequence: `ListByAccountAsync` (count current devices) → compare to `MaxActiveDevices` → `SaveAsync` (insert). Two concurrent registration requests for the same account, both reading the count *before either has written*, can both observe `activeCount < maxDevices` and both proceed to insert — resulting in `maxDevices + 1` (or more, with more concurrent callers) active devices. This is a classic **check-then-act** race, not a single-row update conflict — `Device`'s existing `xmin` optimistic-concurrency token (already configured, Phase 6.5) does **not** protect against this, because the two concurrent inserts are two different rows; neither write conflicts with the other at the row level.

### 6.2 Required invariant

**The effective device limit must never be bypassable through concurrent registration attempts, regardless of how many requests arrive simultaneously for the same account.**

### 6.3 Candidate mechanisms — **IMPLEMENTATION RECOMMENDATION, not decided yet, not implemented in this phase**

| Mechanism | How it would work | Assessment |
|---|---|---|
| Serializable transaction isolation | Wrap count+insert in a `SERIALIZABLE` PostgreSQL transaction; a conflicting concurrent transaction fails and must be retried | Correct, but requires application-level retry-on-serialization-failure logic (PostgreSQL surfaces this as a `40001` SQLSTATE) — real but bounded complexity, consistent with the retry pattern already established for optimistic concurrency in Phase 6.6's `ConcurrentUpdateException` translation. |
| Explicit row-level locking (`SELECT ... FOR UPDATE`) on a per-account "device count" anchor row | Lock the `Account` row (or a dedicated per-account counter row) for the duration of the count+insert | Correct and simpler to reason about than full serializable isolation; requires locking a row that isn't otherwise part of the device write itself (likely the `Account` row, already loaded during account resolution) — some added lock contention on that row for any concurrent device operation on the same account, but device registration is not a high-frequency operation, so this is an acceptable, low-risk cost. |
| Database CHECK constraint / trigger enforcing max count | A Postgres trigger or constraint that rejects an insert if it would exceed the count | Structurally the most bulletproof (enforced even against a bug in application code), but couples the *entitlement value itself* (which is per-plan, dynamic, stored in `Entitlement` rows, not a compile-time constant) to a database constraint — awkward, since the limit isn't a fixed schema-level number, it varies by plan. Not recommended for a *dynamic, per-plan* limit; would be appropriate only for a fixed structural invariant (which this isn't). |
| Application-level distributed lock (e.g., advisory lock keyed by account ID) | PostgreSQL `pg_advisory_xact_lock(accountId)` for the duration of the operation | Functionally similar to row-level locking above, without needing an existing row to lock; PostgreSQL-native, no new infrastructure. A reasonable alternative to the `Account`-row-lock approach. |

**Recommendation for implementation (not yet built)**: row-level locking via `SELECT ... FOR UPDATE` on the `Account` row (or a PostgreSQL advisory lock keyed by `AccountId`, functionally equivalent) around the count-then-insert sequence in `DeviceRegistrationService.RegisterDeviceAsync`. This is the smallest change that closes the race, requires no new entity/table, and mirrors the transaction-boundary discipline already established in Phase 6.6's `IUnitOfWork`/`EfUnitOfWork` (a new caller of that same abstraction, not a new mechanism). **This is a recommendation for the implementation phase — nothing is implemented now.**

---

## 7. Existing Data and Migration

No devices have ever been registered against a real deployment — Phase 6.3–6.6 never shipped a production database with real customer data (Phase 6.5's PostgreSQL persistence is verified but no production tenant exists yet; Phase 6.6 froze before any billing went live). **There is no existing customer device data to migrate or reinterpret.** Under Model A (pooled), the interpretation of any device rows that do exist in a development/test database is unchanged — they were already being counted without platform filtering, so adopting Model A as the explicit decision changes nothing about how existing rows are read. No migration script, backfill, or data transformation is required for Phase 6.7 regardless of which model is approved, precisely because Model A requires no schema change and Model B (if ever chosen later) is additive-only (§3.2).

---

## 8. Test Strategy (implementation test matrix — not built yet)

| Category | Cases |
|---|---|
| Normal registration | Register 1st device for a fresh account; verify `Authorized` status, server-generated ID, correct `AccountId`/`Platform`/`DisplayName`. |
| At-limit registration | Register exactly up to `MaxActiveDevices`; the Nth registration (at the limit) succeeds. |
| Over-limit registration | The (N+1)th registration is rejected with `DeviceLimitExceededException` / `403 device_limit_exceeded`; no row is created. |
| Revoked devices | A revoked device does not count toward the limit; registering a new device after revoking one (at a previously-full limit) succeeds. |
| Repeated registration | Registering multiple devices in sequence for the same account each gets a distinct ID; no accidental de-duplication or overwrite. |
| Concurrent registration | N parallel registration attempts against an account with `MaxActiveDevices = k` (k < N) result in exactly `k` `Authorized` devices and `N-k` rejections — never more than `k` — proving §6's invariant under real concurrency (requires the locking mechanism from §6.3 to be implemented first; this test is expected to FAIL without it, which is itself a valid regression test to write before the fix). |
| Account isolation | Account A cannot list, revoke, or read Account B's devices via any endpoint; `POST /devices/{idBelongingToB}/revoke` from Account A's token returns `404`, not `403` (§5). |
| Malformed/missing entitlements | Missing subscription → limit `1`; missing `MaxActiveDevices` key → limit `1`; malformed (non-numeric) value → limit `1`; `0` value → limit `0` (denies all registrations); negative value → limit `1` (§3.3). |
| `Pending` behavior | If retained per §2.3(a): a explicit test asserting `RegisterDeviceAsync` NEVER produces `Pending` (regression guard, since no code path is meant to reach it) — proves the enum member stays inert as designed. |
| Platform-specific behavior | Registering one device of each of `Windows`/`Android`/`iOS` for the same account, under Model A, all count toward the single pooled limit identically — proving no accidental per-platform behavior leaked in. |

All new tests follow this project's established conventions: in-memory repositories for pure application-logic tests, `WebApplicationFactory`-based HTTP tests for the API boundary (mirroring Phase 6.6's `BillingCustomerEndpointsTests.cs`), and `Testcontainers.PostgreSql` + `[SkipIfNoDockerFact]` for the concurrency test specifically, since a real database transaction/lock is the thing under test — an in-memory fake cannot meaningfully prove a database-level concurrency guarantee.

---

## 9. Dependency Boundary

Phase 6.7 depends on **none** of the following: Gemini, GPT-5.4, any naturalization model or provider, `Streaming/*` experimental components, or any decision about which naturalization vendor (if any) is ever selected. Device licensing is a pure account/entitlement/backend concern (`Account` → `Device`/`Subscription`/`Entitlement`), structurally isolated from the translation/naturalization research track exactly as `docs/phase-6-account-subscription-device-architecture.md` §15 describes for the whole of Phase 6. **Phase 6.7 can begin immediately, independent of any naturalization-track outcome.**

---

## 10. Phase 6.8 Readiness

`docs/phase-6-account-subscription-device-architecture.md` (§462-463) identifies Phase 6.8 as desktop/session integration — the point where the real translation client actually consumes device authorization to gate provider-token issuance. Before that can safely happen, Phase 6.7 must have delivered:

1. **D6 resolved and implemented** (§1) — Phase 6.8's session-start flow needs a single, unambiguous answer to "is this device allowed to start a session," which requires the pooling model to be settled, not still an open question.
2. **The concurrency fix implemented** (§6) — Phase 6.8 introduces the first real-world scenario where concurrent device registration is plausible (a customer installing on two machines around the same time); shipping session integration before this race is closed would let a real customer exceed their device limit in production.
3. **`/devices` API real and tested** (§4) — Phase 6.8's desktop client needs a real endpoint to register itself and check its own authorization status; today's `501` placeholder cannot support it.
4. **`Pending` question settled** (§2) — even though "no approval flow" is the recommendation, Phase 6.8 needs to know definitively that every registered device is immediately usable, not conditionally pending — this must be a closed question, not deferred further.

Everything else Phase 6.8 needs (provider-token minting, session-start entitlement gating) is already handled by the existing, unrelated `EntitlementService`/`IEntitlementService` boundary (Phase 6.3, still correct) — Phase 6.7's job is only to make the device-authorization *input* to that boundary trustworthy under concurrency and unambiguous in policy.

---

## A. Decisions Requiring Product-Owner Approval

1. **D6** — pooled (Model A) vs. per-platform (Model B) device licensing.
2. **`DeviceStatus.Pending`** — retain as reserved/unused, or remove.
3. **Zero/negative `MaxActiveDevices` handling** — confirm the fail-closed clarification in §3.3 (treat `0` as a deliberate full lock-out, negative as malformed/fail-closed) is the intended behavior.

## B. Recommended Decision for Each

1. **Model A (pooled)** — §1.5.
2. **Retain, unused, documented as reserved** — §2.3(a).
3. **Confirm §3.3's table as specified** — no change proposed beyond making existing implicit fail-closed behavior explicit and intentional for the `0`/negative cases.

## C. Exact Implementation Scope for Phase 6.7 (after approval)

1. Confirm/document Model A explicitly in `DeviceRegistrationService`'s own doc comments (behavior itself is unchanged — this closes the "silently pooled" gap from §1.6).
2. Extend `GetMaxActiveDevicesAsync` to explicitly handle `0` and negative parsed values per §3.3 (currently negative values fall through `int.TryParse` successfully and would NOT be caught — this is a genuine small correctness gap this phase must close, distinct from the already-correct malformed-string handling).
3. Implement the concurrency fix (§6.3's recommended row-lock/advisory-lock mechanism) inside `RegisterDeviceAsync`.
4. Implement the real `GET /devices`, `POST /devices`, `POST /devices/{id}/revoke` endpoints in `Program.cs`, replacing the current placeholder, following the exact contract in §4.
5. Write the full test matrix from §8, including the concurrency test against real PostgreSQL.
6. Update `docs/phase-6.3-backend-foundation.md`'s §18 "unresolved decisions" list to mark D6 and the `Pending` question as resolved, cross-referencing this document.

## D. Files Expected to Change During Implementation

- `src/VTTranslate.Backend.Application/Devices/DeviceRegistrationService.cs` (concurrency fix, §3.3 clamp)
- `src/VTTranslate.Backend.Application/Devices/IDeviceRegistrationService.cs` (doc-comment clarification only, likely no signature change)
- `src/VTTranslate.Backend.Api/Program.cs` (real `/devices` routes, replacing the placeholder)
- `src/VTTranslate.Backend.Infrastructure/Persistence/EfCore/EfRepositories.cs` and/or a new small locking-support method on `IAccountRepository`/`IDeviceRepository` if the chosen mechanism needs a new repository call (e.g., a `LockAccountForDeviceRegistrationAsync`-style method) — exact shape depends on which §6.3 mechanism is approved
- `tests/VTTranslate.Backend.Tests/DeviceRegistrationServiceTests.cs` (extended)
- A new `tests/VTTranslate.Backend.Tests/DeviceCustomerEndpointsTests.cs` (HTTP boundary, mirroring `BillingCustomerEndpointsTests.cs`)
- A new or extended real-Postgres concurrency test (mirroring `EfPostgresPersistenceTests.cs`'s pattern)
- No changes anticipated to: `Device.cs` entity, `DeviceStatus.cs` enum, `EntityConfigurations.cs` (Device's existing indexes/FKs/xmin token are already sufficient), any migration file (no schema change under Model A).

## E. Explicit Non-Goals

- No per-platform device limits (Model B) — deferred, only documented for future reference (§3.2).
- No manual device-approval (`Pending`) workflow.
- No hardware fingerprinting of any kind.
- No mobile app implementation (Android/iOS remain unbuilt — Phase 6.10+).
- No changes to billing/subscription code from Phase 6.6.
- No Gemini/naturalization/provider-gateway work of any kind.
- No admin-facing device-management UI or bulk-operations API.
- No production deployment or CI workflow changes.

---

## Review for Contradictions with Phase 6.1–6.6 Architecture

Checked against: `docs/phase-6-account-subscription-device-architecture.md`, `docs/phase-6.2-architecture-decision-matrix.md`, `docs/phase-6.2b-resolved-architecture-decisions.md`, `docs/phase-6.3-backend-foundation.md`, `docs/phase-6.3-review-gate.md`, `docs/phase-6.4-entra-authentication.md`, `docs/phase-6.5-database-persistence.md`, and the Phase 6.6 design record in this conversation.

- **No contradiction with Phase 6.2B §6** (account-based, server-authorized devices, explicitly not hardware-fingerprint-primary) — §5 above reconfirms this unchanged.
- **No contradiction with Phase 6.4's authorization model** (`AccountResolutionMiddleware` as sole account-resolution mechanism) — §5 reuses it verbatim, introduces nothing new.
- **No contradiction with Phase 6.3's entitlement architecture** ("every configurable numeric limit... is an Entitlement row, never a compiled-in constant") — §3 preserves this exactly; Model A requires no new key, Model B (if ever chosen) would follow the same flat key/value convention already used by every other entitlement.
- **No contradiction with Phase 6.5's persistence architecture** — no schema/migration change proposed for Model A; the `Device` table's existing `xmin` concurrency token, FK to `Account` (`Cascade`), and `ix_devices_account` index are all already sufficient and are not modified by this design.
- **No contradiction with Phase 6.6's authorization/audit patterns** — the proposed `/devices` API contract (§4) and 404-not-403 non-enumeration rule (§5) directly reuse the exact patterns Phase 6.6 established for `/subscription`; no second convention is introduced.
- **One genuine, newly-surfaced gap, not a contradiction**: `GetMaxActiveDevicesAsync`'s current `int.TryParse` handling does not explicitly special-case `0` or negative values — §3.3/§C.2 identifies this as a small correctness clarification needed during implementation, not a design contradiction (the existing fail-closed default of `1` already applies to negative values today via the `!int.TryParse` path being false for a valid negative integer, meaning a negative value currently WOULD parse successfully and be used as-is, which — if negative — makes every `activeCount >= maxDevices` comparison true, denying all registrations by accident rather than by design. This is flagged in §3.3 as needing to become an intentional, tested behavior rather than an accidental one.)

No decision in this document silently overrides or reopens a previously-approved Phase 6.1–6.6 decision. D6 and the `Pending` question were both explicitly left open by those phases for exactly this document to resolve.
