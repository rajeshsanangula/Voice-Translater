# Phase 7.0 — Production Identity, Account Provisioning & Customer Lifecycle

**Status: ARCHITECTURE / DESIGN ONLY.** No production source code, migration, API endpoint,
UI, or configuration was created or modified in this phase. This document is the sole
deliverable.

---

## 1. Executive summary

The backend already has a sound, tested authentication and authorization *foundation*
(Phase 6.4): standards-based Entra External ID JWT validation, a Domain-owned
`IIdentityProvider` boundary, backend-authoritative `Account.Role`, and fail-closed
account resolution. What it does **not** have — and what real customers cannot use the
product without — is any way for a new customer to end up with an `Account` in the first
place, a defined `Account` lifecycle beyond `Active`/`Suspended`, any wiring for the
`Profile` entity that already exists in the schema, or a documented production Entra
tenant/environment strategy. Phase 7.0 designs all of that, without touching any of the
five frozen commercial layers (billing, device licensing, provider access, usage
metering) or the translation engine.

The central open decision is **account provisioning strategy** (§7): today,
`AccountResolutionService` deliberately returns `AccountNotFound` for any Entra identity
with no existing `Account` row, and nothing creates one. This was an explicit, correct
Phase 6.4 scope boundary — but it means the product currently has no path from "customer
signs up" to "customer has an account" at all. This document recommends **hybrid
just-in-time provisioning with restrictions** (Option D), but this is a product-owner
decision, not an engineering one, and is called out as such.

## 2. Current-state findings

Inspected directly (file paths, not assumptions):

- **[Account.cs](../src/VTTranslate.Backend.Domain/Entities/Account.cs)** — `Id`, `Email`,
  `EmailVerified`, `ExternalIdentityProvider`, `ExternalSubjectId`, `Role`, `Status`
  (`AccountStatus.Active`/`Suspended` only — **two states, not five**), `CreatedAt`,
  `DeletionRequestedAt` (soft-delete only), `IsSoftDeleted`, `IsUsable`. No `Pending`,
  `Disabled`, or `Closed` state exists today.
- **[Profile.cs](../src/VTTranslate.Backend.Domain/Entities/Profile.cs)** — `AccountId`,
  `DisplayName`, `PreferredLanguagePair`, `CreatedAt`, `UpdatedAt`. `IProfileRepository`
  and both its implementations (`InMemoryProfileRepository`, `EfProfileRepository`) exist
  and are registered in `Program.cs`, but **no application service, no API endpoint, and
  no test file reference `Profile` beyond the repository layer itself** — it is
  persisted-but-unwired. Confirmed via repo-wide grep: zero `ProfileService`,
  zero `/profile` route.
- **[Role.cs](../src/VTTranslate.Backend.Domain/Enums/Role.cs)** — exactly
  `Customer`/`Admin`/`SuperAdmin`, ordinal-comparable, matching PO-D9
  (`docs/phase-6.2b-resolved-architecture-decisions.md` §8). No additional roles exist or
  are implied anywhere in the codebase.
- **[AccountStatus.cs](../src/VTTranslate.Backend.Domain/Enums/AccountStatus.cs)** —
  `Active`, `Suspended` only. The doc comment explicitly states no code path changes this
  yet ("only ever changed by trusted backend/admin logic, none of which is implemented
  yet").
- **[IIdentityProvider.cs](../src/VTTranslate.Backend.Domain/Abstractions/IIdentityProvider.cs)** —
  `AuthenticatedPrincipal(Provider, ExternalSubjectId, Email, EmailVerified, DisplayName,
  IssuedAt, ExpiresAt)`. Deliberately carries no role, no `AccountId`, no device
  information. A reflection test (`NeverProducesARoleOrAnyAuthorizationInformation`)
  enforces this structurally.
- **[EntraIdentityProvider.cs](../src/VTTranslate.Backend.Infrastructure/Identity/EntraIdentityProvider.cs)** —
  the only `IIdentityProvider` implementation. Reads `sub`/`oid`, `email`/
  `preferred_username`, `email_verified`, `name`, `iat`/`exp` from **already-verified**
  claims (cryptographic verification happens upstream in the standard
  `Microsoft.AspNetCore.Authentication.JwtBearer` middleware — this class does not parse
  or verify tokens itself). Fails closed (`null`) if `sub`/`oid` or `iat`/`exp` are
  missing.
- **[AccountResolutionMiddleware.cs](../src/VTTranslate.Backend.Api/Security/AccountResolutionMiddleware.cs)**
  and **[IAccountResolutionService.cs](../src/VTTranslate.Backend.Application/Identity/IAccountResolutionService.cs)** —
  resolves `Account` by `(ExternalIdentityProvider, ExternalSubjectId)`, **never** by
  email. Three outcomes only: `Resolved`, `AccountNotFound` (403 `account_not_found` —
  confirmed: **no auto-provisioning exists today**, by explicit design comment: *"NOT
  auto-provisioned... do not silently create accounts unless the existing approved
  architecture explicitly requires automatic provisioning — it does not"*),
  `AccountSuspended` (403 `account_suspended`).
- **[AuthorizationService.cs](../src/VTTranslate.Backend.Application/Authorization/IAuthorizationService.cs)** —
  `HasAtLeastRole`/`RequireAtLeastRole` operate only on `Account.Role` and
  `Account.IsUsable`; never reads a claim.
- **[AuditEvent.cs](../src/VTTranslate.Backend.Domain/Entities/AuditEvent.cs)** —
  metadata-only, free-form `EventType` string, optional `AccountId`. No identity/account
  lifecycle events are emitted anywhere today (confirmed: no `AccountProvisioned`,
  `AccountSuspended`, `ProfileCreated`, etc. exist in the codebase — audit events exist
  only for device/billing/provider-access/session actions from Phases 6.6–6.9).
- **Program.cs** (`Api/Program.cs`) — `AddJwtBearer` configured against
  `Identity:Authority`/`Identity:Audience` (both committed empty, non-secret,
  environment-supplied), `MapInboundClaims = false` (correctness fix so `sub`/`iat` are
  read as-is), standard `ValidateIssuer`/`ValidateAudience`/`ValidateLifetime`/
  `ValidateIssuerSigningKey` all left at framework defaults (no custom cryptography).
  Middleware order: `UseAuthentication()` → `UseAutraxisAccountResolution()` →
  `UseAuthorization()`.
- **EF model / migrations** — `accounts`, `profiles` tables exist since the
  `InitialCreate` migration (Phase 6.5); no migration since has altered either table's
  shape. `Account.ExternalIdentityProvider` + `ExternalSubjectId` are the unique
  identity-mapping key (confirmed by `Account_DuplicateExternalIdentity_RejectedByUniqueConstraint`
  in `EfPostgresPersistenceTests.cs`).
- **Tests** — `AccountResolutionServiceTests.cs`, `AuthenticationIntegrationTests.cs`,
  `AuthorizationServiceTests.cs`, `EntraIdentityProviderTests.cs`. No `ProfileTests`,
  no `AccountProvisioningTests`, no `AccountLifecycleTests` exist — confirming these are
  genuinely unbuilt, not merely under-tested.
- **VTTranslate.App (WPF client)** — no Entra/OAuth/MSAL/login code exists anywhere in
  the client today (confirmed via repo-wide search). The client remains the pre-backend
  MVP UI; production Windows authentication (§11) is entirely prospective.
- **docs/phase-6.2b-resolved-architecture-decisions.md** — the governing prior decision
  record. PO-D1 (Entra External ID), PO-D9 (three roles) are already **APPROVED** and
  implemented. PO-D7 (email/password registration) is **APPROVED** as a direction but its
  provisioning mechanics were never specified — this is precisely the gap Phase 7.0 fills.
  §19 of that document already flags "password policy specifics," "device pooling," and
  "federated-provider timeline" as unresolved; those remain unresolved here too and are
  out of this phase's scope except where they intersect account provisioning.
- **docs/phase-6.4-entra-authentication.md** §18 ("Known limitations") explicitly names
  account provisioning as deferred: *"Account provisioning... is out of scope for this
  phase... the provisioning flow itself is future work."* Phase 7.0 is that future work,
  at the design level.

## 3. Goals

- Define exactly how a real customer acquires an AUTRAXIS `Account`, without silently
  deciding the provisioning-policy product question.
- Extend `AccountStatus` only as far as architecturally justified, and define what every
  other layer (device, subscription, entitlement, provider access, session, usage,
  audit) does at every state transition.
- Define the `Profile` lifecycle now that it's clear the entity exists but is unwired.
- Establish a production Entra External ID environment/tenant strategy generalizable to
  Windows, Android, and iOS without platform-specific backend logic.
- Define the admin/customer identity boundary precisely enough to plan Phase 7.x admin
  work later, without building it now.
- Produce a threat model, audit model, and test matrix an implementation phase can build
  directly from.

## 4. Non-goals

- No source code, API endpoint, migration, or UI change (per explicit instruction).
- No Paddle/checkout/billing UI/refund/tax logic (Phase 6.6 stays frozen).
- No device-licensing redesign (Phase 6.7 stays frozen).
- No provider-access or credential-issuance redesign (Phase 6.8 stays frozen).
- No usage-metering/session-accounting redesign (Phase 6.9 stays frozen).
- No Android/iOS implementation — only cross-platform-compatible design.
- No admin API design/implementation — architecture and boundary only.
- No legal/pricing/tenant-ID/credential decisions — flagged as product-owner or
  legal/business items instead of invented.

## 5. Proposed architecture

No structural change to the existing layering or request pipeline. Phase 7.0 fills gaps
*within* the existing shape:

```
Entra External ID (production tenant, per-environment)
        ↓ (OIDC/OAuth2, standard JwtBearer validation — UNCHANGED)
AuthenticatedPrincipal (UNCHANGED shape)
        ↓
AccountResolutionService
        ↓
   ┌────┴─────────────────────────────┐
   │ NEW: on AccountNotFound, an       │
   │ explicit, policy-gated            │
   │ provisioning decision point       │
   │ (§7) — still a controlled,        │
   │ observable outcome, never a       │
   │ silent side effect buried in      │
   │ resolution itself                 │
   └────┬─────────────────────────────┘
        ↓
Account (extended status model, §8)  ──→ Profile (now wired, §9)
        ↓
AuthorizationService (UNCHANGED)
        ↓
Subscription / Entitlement / Device / Provider Access / Translation Session / Usage
(ALL UNCHANGED — Phases 6.6–6.9 frozen)
```

The only new *component* proposed is an explicit **`IAccountProvisioningService`**
seam (named here for the eventual implementation phase to target — not built now),
invoked from `AccountResolutionMiddleware` only on `AccountNotFound`, and only if the
product-owner decision in §7 authorizes it. If the decision is "no auto-provisioning,"
this seam is never wired and the current `AccountNotFound` behavior is simply
documented as final, not a gap.

## 6. Identity architecture (production Entra External ID)

**Tenant strategy**: one Entra External ID tenant *per environment* (Development,
Staging, Production) — never one tenant shared across environments, and never a
customer tenant conflated with an AUTRAXIS-staff tenant (see §14 for the admin
boundary). This is the standard, Microsoft-recommended isolation model and matches the
existing `appsettings.{Environment}.json` configuration boundary already established in
Phase 6.4/6.5 (`Identity:Authority`/`Identity:Audience` are environment-supplied, never
committed).

**Customer user flows** (native to Entra External ID, not custom-built):
- **Sign-up**: Entra External ID's own hosted user-flow (email/password per PO-D7, with
  email verification as a built-in step of that flow — not a custom email-sending
  service AUTRAXIS operates).
- **Sign-in**: same hosted flow, standard authorization-code + PKCE.
- **Password reset/recovery**: Entra External ID's native self-service password reset
  flow — no AUTRAXIS-built password-reset endpoint or email template. This avoids
  AUTRAXIS ever handling a raw password or reset token.
- **MFA**: Entra External ID supports optional/conditional MFA per user flow or
  Conditional Access policy. Whether to require it for customers is a **product/security
  decision** (see §26); the architecture imposes no obstacle either way, since MFA
  enforcement lives entirely in the Entra tenant configuration, not in AUTRAXIS code.
- **Email verification**: Entra External ID emits `email_verified` on some but not all
  flows (`EntraIdentityProvider` already treats its absence as `false`, never `true` by
  default — this fail-closed behavior is correct and needs no change).
- **Supported identity providers**: local (email/password) per PO-D7 today; the
  `IIdentityProvider` abstraction (unchanged) already isolates the backend from ever
  needing to know if a future federated provider (Google/Apple/enterprise SSO) is added
  at the Entra-tenant level — no backend code change would be required for that, only
  Entra tenant configuration and possibly a new claim-mapping check inside
  `EntraIdentityProvider` if the federated provider surfaces claims under different
  names.

**Redirect URIs / client registration**: one Entra *application registration* per client
platform (Windows, Android, iOS), each with its own platform-appropriate redirect URI
(a custom URI scheme or loopback for Windows, an app-specific redirect for
Android/iOS), all pointed at the same tenant's user flow and the same backend API
audience. Registrations differ **per environment** too — a Staging Windows client must
never be able to authenticate against the Production tenant, and vice versa. No
redirect URI, client ID, or tenant ID is committed to source control at any point (this
is already the pattern for `Identity:Authority`/`Identity:Audience` — extend it, don't
invent a new pattern).

**API audience / scopes**: the backend API remains **one** registered audience
(`Identity:Audience`, unchanged shape) that all client platforms request tokens for.
No platform-specific scope or audience is introduced — the existing single-audience
model (already proven in Phase 6.4) generalizes to Android/iOS without change, exactly
as `docs/phase-6.2b-resolved-architecture-decisions.md` §15/§16 already predicted.

**Claims / issuer validation / authority metadata / key rotation**: entirely handled by
the existing standard `Microsoft.AspNetCore.Authentication.JwtBearer` configuration —
`ValidateIssuer`, `ValidateAudience`, `ValidateLifetime`, `ValidateIssuerSigningKey` all
remain at framework defaults; JWKS/signing-key rollover is handled automatically by the
framework's own `ConfigurationManager` metadata refresh, exactly as documented in
`docs/phase-6.4-entra-authentication.md` §14. **Nothing here needs to change for
production** — the existing configuration-boundary pattern (empty-committed,
environment-supplied) already anticipates real values being substituted per
environment. This is a configuration/operations task (see §23), not an architecture
change.

**Token lifetime / clock skew**: Entra External ID's default access-token lifetime
(typically ~60–90 minutes, tenant-configurable) and refresh-token rotation are used
as-is — no custom token issuance is proposed (see §13). Clock skew uses the framework's
default `ClockSkew` (300 seconds) unless a specific production incident justifies
tightening it; no change proposed without evidence.

**Logout**: client-side token/cache clearing plus, where the platform's browser
component supports it, Entra's federated sign-out endpoint. No backend session to
invalidate exists today because the backend holds no server-side session state
(§13) — logout is purely a client and Entra-tenant concern.

**Account disablement / compromised identity**: if a customer's Entra identity is
compromised (credential-stuffing detection, customer report, etc.), the response is
**two independent actions**, not one: (1) at the Entra tenant level, force a password
reset / revoke the Entra sessions for that identity (an Entra-tenant operation, outside
AUTRAXIS code); (2) at the AUTRAXIS level, an admin sets `Account.Status = Suspended`
(existing mechanism, unchanged) if there's reason to believe the AUTRAXIS account itself
(not just the Entra credential) needs to be locked out immediately, independent of
whichever action completes first at the Entra tenant. These are deliberately separate
controls — a compromised Entra credential does not require touching `Account`, and a
suspended `Account` does not require touching Entra — matching the existing "identity
proves who; AUTRAXIS decides what" separation.

## 7. Account provisioning decision (product-owner decision required)

This is the central unresolved question this phase exists to surface, not resolve.

**Option A — No automatic provisioning** (today's actual behavior).
- *Security*: strongest — an AUTRAXIS `Account` only ever exists because a trusted
  backend/admin process created it deliberately.
- *Abuse prevention*: excellent — no unauthenticated-signup surface to abuse at all.
- *Operational complexity*: highest ongoing burden — every single customer requires a
  manual or semi-manual account-creation step by AUTRAXIS staff or a separate
  provisioning tool.
- *UX*: poor for self-serve commercial signup — a customer who authenticates with Entra
  for the first time hits `403 account_not_found` and has no path forward from the app
  itself.
- *Commercial readiness*: effectively blocks self-serve purchase entirely; only viable
  for an invitation-only/enterprise-sales motion.
- *Account enumeration risk*: none — there is no signup endpoint to enumerate against.
- *Device licensing / billing integration*: irrelevant until an account exists, so no
  interaction.
- *Support burden*: highest — every signup is a support/ops ticket.

**Option B — Just-in-time (JIT) provisioning, unrestricted**.
- First successful Entra authentication for a never-seen `(Provider, ExternalSubjectId)`
  creates the `Account` automatically, no gate.
- *Security*: weakest of the four — anyone who can complete Entra's own sign-up flow
  (which, per PO-D7, is just email/password with Entra's own email verification) gets an
  AUTRAXIS `Account` immediately, with no AUTRAXIS-side check at all.
- *Abuse prevention*: poor on its own — trivially scriptable mass-account creation
  (disposable emails, credential-stuffing farms), each landing straight on a `Trial`
  subscription (Phase 6.6) and consuming trial `Entitlement` allowance and device slots
  (Phase 6.7) for free.
- *Operational complexity*: lowest — zero manual steps.
- *UX*: best — completely frictionless signup-to-usable-account.
- *Commercial readiness*: good, but only if trial abuse is otherwise controlled (rate
  limiting, fraud signals) — which this option alone does not provide.
- *Account enumeration risk*: low for the *provisioning* action itself (creating an
  account reveals nothing about whether one already existed, since it always
  "succeeds"), but see §16 for a related timing-side-channel note.
- *Support burden*: low, until trial abuse generates a different kind of support/ops
  load (chargeback-equivalent: wasted provider-access/usage-metering capacity, not
  billing fraud, since Trial is free — but still a real cost via Azure Speech usage).

**Option C — Invitation-based provisioning**.
- A customer must already hold an AUTRAXIS-issued invitation (a token or pre-created
  `Account` in a `Pending` state, see §8) before they can complete Entra sign-up and
  have it linked to a real, usable account.
- *Security*: strong — no anonymous path to a usable account at all.
- *Abuse prevention*: excellent — provisioning volume is bounded by how many invitations
  AUTRAXIS actually issues.
- *Operational complexity*: moderate — requires an invitation-issuance mechanism
  (email-based token, admin-initiated) that does not exist today and would itself need
  design/implementation.
- *UX*: adds friction (a customer must first request/receive an invitation before they
  can sign up) — workable for an early-access or enterprise motion, poor for a
  self-serve consumer product.
- *Commercial readiness*: good fit for a controlled early-access rollout; poor fit for
  a mass-market self-serve SaaS motion, unless paired with an automated
  "request access" → "invitation auto-issued" flow (which is itself close to Option D).
- *Account enumeration risk*: low.
- *Support burden*: moderate (invitation issuance/tracking becomes a new operational
  surface).

**Option D — Hybrid: controlled JIT provisioning with restrictions (RECOMMENDED)**.
- First successful Entra authentication for an unknown identity **is allowed to**
  create an `Account`, but only after passing backend-side, server-controlled gates
  that do not depend on anything the client asserts: e.g. a per-IP/per-time-window
  provisioning-rate limit, an email-domain block/allow list if the product ever needs
  one, a "trial accounts require `email_verified = true` from Entra before becoming
  usable" rule (already directly checkable — `AuthenticatedPrincipal.EmailVerified`
  already exists and is unused for this purpose today), and a mandatory `AuditEvent`
  (`AccountProvisioned`, §15) on every creation so provisioning volume/velocity is
  observable and alertable (§21) from day one.
- *Security*: strong — closes Option B's open-abuse-surface concern without
  reintroducing Option A's all-manual burden or Option C's separate invitation
  subsystem.
- *Abuse prevention*: good — rate limiting and `email_verified` gating meaningfully
  raise the cost of mass fake-account creation, though (correctly) not to zero; this is
  the same trade every self-serve SaaS product makes and is normally paired with
  product-level fraud monitoring (out of scope here).
- *Operational complexity*: moderate — requires the provisioning-rate-limit and audit
  wiring (a real, scoped implementation task for a future phase) but no separate
  invitation subsystem.
- *UX*: as good as Option B for the overwhelming majority of legitimate signups; only
  degrades for someone tripping a rate limit, which is the intended behavior.
- *Commercial readiness*: the best fit for a self-serve consumer/prosumer SaaS product,
  which is what every other frozen phase (billing, device pooling, provider access,
  usage metering) is already built to serve.
- *Account enumeration risk*: same as Option B — low, with the same caveat in §16.
- *Tenant administration / device licensing / future mobile*: unaffected either way —
  provisioning creates exactly one `Account` row; everything downstream (Phase
  6.6–6.9) already works from `AccountId` alone, regardless of how that `Account` came
  to exist.

**Recommendation: Option D.** **Why**: it is the only option that is both viable for a
self-serve commercial launch (ruling out A and, largely, C) and does not leave an
unbounded automated-abuse surface open on day one (ruling out unrestricted B). It also
requires the least new standing infrastructure of the three viable-for-launch shapes,
since it extends the existing account-resolution control point rather than building a
parallel invitation system.

**Product-owner decision required**: which option (A/B/C/D) is approved for launch;
if D, the specific gate parameters (rate-limit thresholds, whether `email_verified`
is a hard gate or a soft one, whether any email-domain policy applies) — **none of
these values are invented here**, matching the same "structure approved, values not
yet decided" pattern already used for trial length/grace-period length in Phase 6.2B.

## 8. Account lifecycle

**Recommended states** (extends today's `Active`/`Suspended`; new states are **proposed,
not implemented**):

| State | Meaning | Entry | Auth allowed? | API access |
|---|---|---|---|---|
| `Pending` *(new — only needed if Option C or D's "unverified" case is approved)* | Provisioned but not yet usable (e.g. awaiting email verification under a stricter JIT gate) | Provisioning gate did not immediately grant `Active` | Entra auth succeeds; `AccountResolutionMiddleware` still denies (new outcome, analogous to today's `AccountSuspended`) | None — same 403 pattern as `Suspended` today |
| `Active` *(existing)* | Normal usable account | Provisioning success, or admin reactivation from `Suspended` | Full | Full, per `Role` |
| `Suspended` *(existing)* | Administrative hold (abuse, fraud, chargeback, support hold) | Admin action, or an automated trust/fraud signal (future, unspecified) | Entra auth succeeds; AUTRAXIS denies | None — matches today's exact behavior, unchanged |
| `Disabled` *(new — distinct from Suspended)* | Longer-term deactivation, e.g. subscription permanently lapsed past grace, or customer-requested deactivation short of full closure | Admin action or a defined automated policy (not yet specified — flagged, not invented) | Denied, same shape as `Suspended` | None |
| `Closed` *(new)* | Terminal — customer- or admin-initiated account closure | Explicit closure action only | Denied permanently — `(Provider, ExternalSubjectId)` unique constraint means the same Entra identity can never silently re-provision a *different* `Account`; a genuinely new signup from the same identity must be a deliberate, auditable reactivation-or-new-account decision, not automatic | None |

**Why not more states**: `AccountStatus` today is intentionally minimal (Phase 6.4
comment: "only ever changed by trusted backend/admin logic, none of which is
implemented yet"). `Pending`/`Disabled`/`Closed` are proposed only because they answer
concrete architectural questions this phase must answer (§7's gate outcome, and §6/§18
below); no additional state (e.g. a separate "trial-expired" account state) is proposed,
because that distinction is already fully and correctly represented one layer down, on
`Subscription.Status` (Phase 6.6, frozen) — conflating it onto `Account.Status` would
duplicate state that already exists and risk the two disagreeing.

**Who transitions each state**: every transition is an explicit backend/admin action —
`Account.Status` is never client-settable and never inferred from an Entra claim, exactly
matching the existing doc-comment discipline on the field. `Pending → Active` may be a
system-triggered transition (e.g., on email verification callback) rather than a human
admin action, but it is still backend-triggered, never a client PATCH to
`Account.Status` directly.

**Effect on each downstream layer, per state** (this is the architecturally important
part — every frozen layer's *existing* behavior is preserved unchanged; only the trigger
condition, `Account.IsUsable`, needs to also cover the new states):

- **Authentication**: unaffected in all states — Entra authentication itself always
  succeeds or fails on its own terms; `Account.Status` is checked *after* authentication,
  inside `AccountResolutionMiddleware`, exactly as `Suspended` is handled today.
- **API access**: `Pending`/`Suspended`/`Disabled`/`Closed` all deny at the exact same
  `AccountResolutionMiddleware` gate that `Suspended` denies at today — no new gate
  location, only new outcome values. `Account.IsUsable` becomes `Status == Active &&
  !IsSoftDeleted` unchanged in shape, just evaluated against the wider enum.
- **Devices** (Phase 6.7, frozen — behavior unaffected, described for completeness):
  a non-`Active` account cannot register or heartbeat a device (blocked at the same
  `AccountResolutionMiddleware` gate before any device-service code runs); already-
  registered devices are neither auto-revoked nor auto-un-revoked by an `Account` state
  change alone — device revocation remains its own explicit action (Phase 6.7,
  unchanged). This matters specifically for `Closed`: closing an account does **not**
  itself need to loop over and revoke every device row, because a closed account can no
  longer authenticate at all, which is a strictly stronger guarantee than per-device
  revocation.
- **Subscriptions/Entitlements** (Phase 6.6, frozen): unaffected in shape — a
  `Suspended`/`Disabled`/`Closed` account's `Subscription` row is not itself force-
  transitioned by this design (that remains a billing-lifecycle concern, Phase 6.6,
  untouched); the account-level gate alone already prevents any use of that
  entitlement, which is sufficient and avoids two systems racing to change the same
  fact.
- **Provider access** (Phase 6.8, frozen): unreachable for a non-`Active` account, for
  the same reason as devices — the request never gets past `AccountResolutionMiddleware`.
- **Active translation sessions** (Phase 6.9, frozen): an `Account` transition to
  `Suspended`/`Disabled`/`Closed` does **not** retroactively force-terminate an
  already-Active `TranslationSession` in this design — Phase 6.9's own lease/heartbeat
  mechanism (unchanged) will naturally expire it once the now-locked-out client can no
  longer heartbeat or end it (since it can no longer authenticate at all). This is
  intentionally *not* a new forced-termination code path, to avoid adding an
  account-status-triggered write into the frozen Phase 6.9 service — the existing lazy
  expiry reconciliation already closes this out correctly and for free.
- **Usage accounting** (Phase 6.9, frozen): unaffected — already-recorded `UsageRecord`
  rows are historical fact and are never deleted or altered by an account-state
  transition, in any state including `Closed` (see §17/§19 on why deletion must not
  reach usage/billing history).
- **Audit history**: `Closed` must **never** delete or redact prior `AuditEvent` rows —
  this is stated explicitly because it is the exact abuse this section's opening
  instruction warns about ("account closure cannot accidentally become a way to bypass
  billing or audit requirements"). The `Closed` transition itself is audited
  (`AccountClosed`, §15), additively, alongside everything that came before it.

## 9. Profile lifecycle

**Current state**: `Profile` entity + repository exist and are DI-registered; nothing
creates, reads, or updates a `Profile` anywhere in the codebase today. This section
designs the missing lifecycle without proposing schema changes (§17).

- **Creation**: a `Profile` row is created at the same moment an `Account` becomes
  usable — i.e., as part of the same provisioning transaction as §14's account-creation
  flow (one row each, one transaction, so a `Profile`-less usable `Account` never
  exists, and vice versa). This mirrors the "no partially created account" requirement
  in §14 by construction rather than by a separate repair job.
- **Required fields**: none beyond what the entity already requires (`AccountId`) —
  `DisplayName` and `PreferredLanguagePair` both remain optional, matching the entity's
  current nullable shape. This is a deliberate minimization: nothing about using the
  translation product requires a display name up front.
- **Optional fields**: `DisplayName` (customer-editable, free text, reasonable length
  cap — a specific cap is an implementation-time detail, not an architecture decision),
  `PreferredLanguagePair` (already exists; the entity's own doc comment flags its exact
  business meaning as still undecided against the real Windows client — unchanged by
  this phase). **Locale/timezone**: not proposed as new fields — nothing in the current
  product (server-side usage aggregation is UTC-based via `IClock`, per Phase 6.6/6.9)
  requires a stored timezone; a client can compute a locally-displayed time from a UTC
  timestamp without the server storing the client's timezone. If a future notification
  or scheduling feature needs it, it should be added then, not speculatively now.
  **Marketing preferences**: explicitly not proposed — no marketing/email-campaign
  feature exists anywhere in this codebase to justify it; adding the field ahead of the
  feature would be exactly the kind of speculative-schema-expansion this document is
  instructed to avoid.
- **Updates**: customer-initiated, `AccountId`-scoped only (a `Profile` update must load
  by the *authenticated* account's own ID, never a client-supplied `AccountId>` — the
  same account-isolation discipline already proven for `Device`/`ProviderAccessGrant`/
  `TranslationSession` in Phases 6.7–6.9). `UpdatedAt` is already present and
  server-set.
- **Validation**: `DisplayName` — reasonable length/character validation, no PII beyond
  what a display name inherently is; `PreferredLanguagePair` — validated against the
  set of directions the product actually supports (the same validation the existing
  `Direction` string already implicitly needs wherever it's consumed downstream, e.g.
  `TranslationSession.Direction`, Phase 6.9).
- **Account isolation**: identical pattern to every other entity in this system —
  `Profile.AccountId` is looked up only via the authenticated account from
  `AccountResolutionMiddleware`, never from a client-supplied ID.
- **Deletion/retention**: a `Profile` is deleted (hard delete is acceptable here,
  **unlike** `Account`/`UsageRecord`/`AuditEvent`) only when its owning `Account` is
  hard-purged (a separate, not-yet-designed process per the existing `IsSoftDeleted`
  doc comment) — never on `Account.Status` merely becoming `Closed`, since a closed
  account may still need its `Profile` readable during any legally-required retention
  window (§19) or for support investigation. This keeps `Profile` deletion strictly
  *after*, never *instead of*, `Account` soft-deletion.

## 10. Identity vs. Account boundary

**Already correctly separated today** — this section documents the existing boundary
precisely, since Phase 7.0 must not weaken it:

- An **External Identity** (`Provider` + `ExternalSubjectId`) proves *who authenticated*.
  It carries no subscription, entitlement, device, billing, or usage information at
  all — `AuthenticatedPrincipal` structurally cannot, since none of those fields exist
  on it.
- An **AUTRAXIS Account** is the sole anchor for every other entity: `Subscription`,
  `Device`, `ProviderAccessGrant`, `TranslationSession`, and `UsageRecord` all reference
  `Account.Id` and nothing else. None of them reference `ExternalSubjectId` directly —
  confirmed by inspection of every entity added in Phases 6.5–6.9.
- **One identity maps to at most one account**: enforced today by a real PostgreSQL
  unique constraint on `(ExternalIdentityProvider, ExternalSubjectId)` (confirmed:
  `Account_DuplicateExternalIdentity_RejectedByUniqueConstraint`,
  `EfPostgresPersistenceTests.cs`). This remains the authoritative mechanism; Phase 7.0
  proposes no change to it.
- **Account reassignment prevention**: because the identity mapping is `init`-only on
  `Account` (`ExternalIdentityProvider`/`ExternalSubjectId` are `required ... { init; }`),
  an `Account` row's identity mapping cannot be silently repointed at a different Entra
  identity by any application-layer update path — only a deliberate, audited
  "identity-linking" administrative operation (not built, and explicitly flagged as
  **not proposed in this phase** — no product requirement for it has been stated) could
  ever change it, and even that would need to go through raw persistence, not the normal
  `Account` update surface.
- **Duplicate/conflicting records**: the unique constraint above makes a true duplicate
  (same identity, two `Account` rows) structurally impossible at the database level; the
  scenario that *can* occur is a customer creating two *different* Entra identities
  (e.g., two different email addresses) that a human recognizes as "the same person" —
  this is a support/product question (a manual account-merge process), not an
  architecture gap, and is explicitly **not designed here** as no requirement for
  account merging has been stated anywhere in this codebase's history.

## 11. Customer authorization

Unchanged from Phase 6.4/6.2B, restated for completeness (no new roles proposed):

- `AccountId` always comes from `AccountResolutionMiddleware`'s server-side lookup —
  never a client-supplied value, in any endpoint, at any phase, including the account-
  provisioning path itself proposed in §7/§14 (the *identity* being provisioned is
  server-verified via the already-authenticated Entra token; the resulting `AccountId`
  is server-generated, never client-supplied).
- `Role` always comes from `Account.Role`, resolved server-side — never an Entra claim.
- Email is never an authorization identity, in any new component proposed here (§7's
  `email_verified` check is a **provisioning eligibility gate**, not an identity key —
  it never substitutes for `(Provider, ExternalSubjectId)` lookup).
- Unknown roles fail closed — unchanged, `AuthorizationService` requires an exact
  ordinal match against the known `Role` enum.
- **Role model stays exactly `Customer`/`Admin`/`SuperAdmin`** — no new role is proposed.
  `CUSTOMER`-only operations: profile self-service (§9), device self-service (Phase
  6.7, unchanged), subscription self-service (Phase 6.6, unchanged), translation-session
  usage (Phase 6.9, unchanged). Administrative-authority operations (not built, but
  bounded by the existing PO-D9 role table in `docs/phase-6.2b-resolved-architecture-decisions.md`
  §8): viewing/suspending a customer account, viewing audit logs, and (SuperAdmin only)
  overriding entitlements or editing `ProviderConfiguration` — none of this changes in
  Phase 7.0; it is restated here only because §8's new account-lifecycle *transitions*
  will eventually need an authorization gate, and that gate is this exact existing role
  model, not a new one.

## 12. Admin boundary

**Design-only, per instruction — no admin API proposed.**

- **Customer tenant/user identities** vs. **AUTRAXIS administrative identities** should
  be **separate Entra tenants or, at minimum, separate Entra External ID user flows**
  within the same tenant that are never reachable via the customer-facing sign-up flow.
  The cleanest, least-ambiguous option is a **separate Microsoft Entra ID (workforce)
  tenant** for AUTRAXIS staff, distinct from the Entra **External ID** (customer) tenant
  — this is in fact the standard Microsoft-recommended split (Entra ID for workforce/
  internal identities, Entra External ID for customer-facing identities), and it
  structurally prevents a customer signup flow from ever producing a token that could
  satisfy an admin audience check, because the two are different token issuers
  entirely.
- **Privileged admin authentication**: an admin's access token is issued by the
  *workforce* tenant, validated against a **separate API audience** than the customer
  audience (two `Identity:Audience`-equivalent values, not one) — this is a natural,
  small extension of the existing single-audience `JwtBearer` configuration (register a
  second `AddJwtBearer` scheme, or a second audience-validation policy) and requires no
  change to `IIdentityProvider`'s Domain-level contract, since "which token issuer
  authenticated this request" remains an infrastructure-boundary fact the Domain never
  sees.
- **Separation of duties**: `Admin` (support-level: view accounts, revoke a customer's
  device on their behalf, view audit logs — per PO-D9's existing table) vs.
  `SuperAdmin` (can edit `ProviderConfiguration`, override entitlements, manage other
  admin accounts) remains the correct minimum split; Phase 7.0 does not add a role
  between or above these.
- **Break-glass access**: not designed here — flagged as a genuine open question (§26)
  requiring a specific incident-response/security decision (e.g., a sealed emergency
  `SuperAdmin` credential process) that has product/security ownership, not an
  engineering default.
- **Admin impersonation**: **not recommended** unless a specific, currently-unstated
  support requirement justifies it; if ever built, it must be its own explicitly
  audited action (`AuditEvent` with both the admin's and the customer's `AccountId`),
  never a silent side effect of an admin viewing a customer record. No impersonation
  mechanism is proposed in this phase.
- **Support access**: `Admin`'s existing "view (not silently modify)" scope, per PO-D9,
  is sufficient for the account-lifecycle transitions this phase introduces (e.g.
  suspending/disabling an account) without needing a new access tier.

## 13. Session / token model

Four distinct token/session concepts already exist or are proposed; none should be
merged:

| Concept | Authoritative for | Issued by | Lifetime |
|---|---|---|---|
| Entra identity token (access + refresh) | "who authenticated" | Entra External ID (customer tenant) | Entra-tenant-configured (unchanged) |
| AUTRAXIS authenticated request | "which Account, which Role, right now, for this one HTTP call" | Derived per-request by `AccountResolutionMiddleware` from the Entra token — **not a separate issued token at all** | Exactly the duration of one request |
| Provider access credential (Phase 6.8) | "may this device call Azure Speech right now" | `AzureProviderCredentialIssuer`, frozen | Minutes (Azure STS-controlled) |
| Translation session (Phase 6.9) | "how much authorized usage to record" | `TranslationSessionService`, frozen | Lease-bounded, server-tracked |

**Recommendation: the backend does not need, and should not build, its own
application-session token.** Every protected request already re-derives `AccountId`/
`Role` fresh from the Entra access token on every call via `AccountResolutionMiddleware`
— this is stateless, requires no server-side session store, and is exactly why
`Account.Status` changes (e.g., a mid-session suspension) take effect on the customer's
very next request without any session-invalidation mechanism needing to exist. Building
a second, AUTRAXIS-issued session token would only reintroduce a *second* revocation
problem (now needing to invalidate two credentials instead of one) for no corresponding
benefit, since Entra's own refresh-token rotation already provides standard,
Microsoft-supported revocation. The client should continue presenting the Entra access
token directly on every request, refreshing it via Entra's standard refresh flow — no
change to this pattern is proposed.

## 14. Windows client authentication flow (production)

Design only — no code. Must be reusable, in principle, by Android/iOS (§12 of Phase
6.2B already establishes this is achievable via standards-based OIDC; this section
makes the Windows specifics concrete).

- **Login initiation**: the WPF client initiates an OIDC authorization-code flow.
- **System browser, not embedded browser**: use the OS-native system browser (or
  Windows' `WebAuthenticationBroker`/an equivalent), never an in-app `WebBrowser`/
  `WebView2` control dedicated solely to capturing credentials — this is both a
  Microsoft-recommended practice (avoids the app ever seeing raw credentials, and
  benefits from the system browser's own saved-session/passkey/autofill support) and a
  direct mitigation for several threats in §16 (credential harvesting via a
  compromised or malicious embedded WebView).
- **Authorization-code flow with PKCE**: mandatory, no exceptions — a public desktop
  client cannot hold a client secret, so PKCE is the only safe variant of the
  authorization-code flow for this client type. No implicit-flow fallback.
- **Token acquisition**: standard OIDC token exchange, off the system browser's
  redirect callback.
- **Token cache**: encrypted at rest using **Windows DPAPI**
  (`ProtectedData`/`CryptProtectData`, user-scope) — never plaintext in a file, the
  registry, or an unencrypted local database. This is a Windows-specific storage
  mechanism, but the *concept* (platform-native secure credential storage) generalizes
  directly: Android's Keystore-backed `EncryptedSharedPreferences` and iOS's Keychain
  are the platform-native equivalents for those future clients — no cross-platform
  custom encryption scheme should be built when each platform already provides one.
- **Refresh**: standard OIDC refresh-token flow, silently, on token expiry, without
  requiring the user to re-authenticate interactively unless the refresh token itself
  has expired or been revoked.
- **Logout**: clear the local token cache; optionally invoke Entra's federated
  sign-out endpoint in the system browser if a full IdP-level logout is desired (e.g.
  "sign out everywhere").
- **Token expiration handling**: on a 401 from the AUTRAXIS API (or a locally-detected
  expired token before even calling the API), attempt silent refresh first; only
  fall back to an interactive re-login prompt if refresh itself fails.
- **Network failure**: if the API is unreachable, the client must **not** fall back to
  any cached "last known good" entitlement/authorization state as a substitute for a
  real request — this directly matches the existing, approved Phase 6.2B §12 rule
  ("a client that cannot reach the backend... cannot start a new translation session —
  full stop") and Phase 7.0 introduces nothing that would weaken it.
- **Suspended account**: the client receives `403 account_suspended` on its next API
  call (already the exact behavior `AccountResolutionMiddleware` produces today) — the
  client's job is only to surface this cleanly, not to locally infer or cache an account
  state.
- **Account switching**: sign out (clear cache), then a fresh sign-in flow for a
  different identity — no "multiple concurrent signed-in accounts" concept is proposed,
  matching the fact that `Account`/`Device`/everything downstream already assumes one
  authenticated account per request.
- **No secrets in the executable**: the WPF client's Entra application registration
  is a **public client** (no client secret at all, per PKCE above) — there is no
  secret to embed, which is the correct outcome, not a mitigation applied after the
  fact.
- **Provider master keys never enter the client**: unchanged — this was already
  guaranteed structurally by Phase 6.8 (`AzureProviderCredentialIssuer` never returns
  the long-lived Azure master key, only a short-lived STS token) and nothing in Phase
  7.0 touches that boundary.

## 15. Audit model

Proposed identity/account lifecycle `AuditEvent.EventType` values (additive — no schema
change, since `EventType` is already a free-form string per its own doc comment):

- `AccountProvisioned` — `AccountId`, provisioning path metadata (e.g. which gate in
  §7 admitted it), timestamp. Never the raw Entra claims.
- `AccountActivated` — `Pending → Active` (if §8's `Pending` state is approved).
- `AccountSuspended` — `AccountId`, actor (which admin/automated policy), reason
  category (not free-text abuse detail that could itself be sensitive — a short
  enumerable reason code).
- `AccountDisabled` — analogous to `AccountSuspended`.
- `AccountClosed` — `AccountId`, actor, timestamp. Never removes prior audit rows (§8).
- `ProfileCreated` — `AccountId` only; never `DisplayName` content (that's account
  metadata, not audit-worthy content, and needlessly duplicates PII into a second
  table).
- `ProfileUpdated` — `AccountId`, which fields changed (field *names*, never old/new
  *values* — matches the existing "metadata only" doctrine on `AuditEvent.Metadata`).
- `IdentityLinked` / `IdentityUnlinked` — reserved for the not-yet-designed
  account-merge/re-linking scenario noted in §10; not actively emitted until that
  feature, if ever, is designed.
- `AuthenticationDenied` — already partially covered by existing
  `AccountResolutionMiddleware` log lines (`logger.LogWarning`/`LogInformation`); this
  event formalizes it as a persisted `AuditEvent` rather than only an application log
  line, useful for the observability/alerting in §21. Metadata: outcome category
  (`account_not_found`/`account_suspended`/etc.), never the Entra claims or token.
- `RoleChanged` — `AccountId`, actor, old role, new role (role values are not
  sensitive — they're already visible to the account holder via their own API
  responses).

**Must never log**: passwords (never touch AUTRAXIS code at all — Entra-native flow),
access/refresh tokens, provider credentials, authorization codes, PKCE verifiers,
signing keys, or unnecessary PII (e.g., raw email address in `Metadata` where an
`AccountId` reference already suffices). This restates, rather than changes, the
existing `AuditEvent` doc-comment discipline.

**Retention**: audit history must survive `Account` closure (§8) and should follow
whatever retention window the eventual legal/compliance decision in §19 sets — this
document does not set a specific retention period, since that is explicitly a
legal/business decision, not an engineering one.

## 16. Security / threat model

| Threat | Impact | Mitigation | Residual risk |
|---|---|---|---|
| Account enumeration via provisioning/login response differences | Attacker learns which emails/identities have AUTRAXIS accounts | `AccountResolutionMiddleware` already returns the same 403 shape (`account_not_found` vs. `account_suspended`) with no timing-sensitive branching visible to the client; §7's provisioning gate should likewise return a uniform response regardless of whether provisioning succeeded, was rate-limited, or the identity already existed | A sufficiently precise timing side-channel between "provision new row" and "look up existing row" is a residual risk; a specific research task, not resolved by architecture alone |
| Identity spoofing (forged Entra token) | Full account takeover | Standard `Microsoft.IdentityModel`/`JwtBearer` cryptographic validation (unchanged, already implemented and tested) | Residual risk only if Entra's own signing keys were ever compromised — outside AUTRAXIS's control surface |
| Forged role/roles claim inside a valid JWT | Privilege escalation | Already structurally impossible — `AuthenticatedPrincipal` carries no role field at all; `Account.Role` is the only source (proven by existing test `ClientSuppliedRoleClaimInsideJwt_CannotEscalatePrivileges`) | None identified beyond an Entra-tenant misconfiguration that somehow let a claim override `Account.Role` server-side, which nothing in this design permits |
| Email-based identity confusion (two accounts, "same" email) | Support/product confusion, not a security bypass | Identity is keyed on `(Provider, ExternalSubjectId)`, never email (unchanged, already implemented) | Residual: a customer legitimately confused about "which account is mine" — a support/UX issue, not a security one |
| Token replay | Reuse of a stolen access token within its validity window | Standard token lifetime + `ValidateLifetime` (unchanged); short Entra access-token lifetime bounds the exposure window | Residual risk exists for any bearer token during its live window — inherent to the OIDC bearer-token model, not specific to this design |
| Stolen refresh token | Long-lived unauthorized access | Entra's native refresh-token rotation + family tracking (standard OIDC feature, not custom-built) | Residual: detection latency between theft and the legitimate client's next refresh attempt revealing the theft |
| Compromised client (malware on the customer's machine) | Local token-cache theft | DPAPI encryption (§14) ties the cached token to the OS user profile, raising the bar above "just copy a file" | Does not protect against malware running *as* the same OS user — inherent limit of any local-token-cache design; out of scope for a backend architecture document |
| First-login provisioning race (two concurrent requests, same new identity) | Duplicate-account attempt | §14 (below) — unique constraint + explicit transaction boundary | None, if implemented per §14 — the database constraint is the actual backstop, not merely the application-level check |
| Duplicate provisioning via retried requests | Same | Same as above | Same as above |
| Account takeover via Entra credential compromise | Full account access | Outside AUTRAXIS's code boundary — Entra's own account-security features (MFA, risk-based sign-in) are the primary control; AUTRAXIS-side `Account.Suspended` is the secondary, independent control (§6) | Residual risk is inherent to any federated-identity model — accepted, standard trade-off of not building custom credential storage |
| Disabled/suspended user still accessing via a not-yet-expired token | Continued access after a suspension decision | `AccountResolutionMiddleware` re-checks `Account.Status` on **every** request, not just at token issuance — so a suspension takes effect on the customer's very next API call regardless of remaining token lifetime | None beyond the gap between suspension and the customer's next request, which is typically seconds given the product's real-time nature |
| Tenant confusion (customer token accepted by admin surface, or vice versa) | Privilege boundary failure | Separate tenants/audiences per §12 — a customer token structurally cannot satisfy an admin audience validation | Requires correct, ongoing operational configuration (§20/§23) — a config-drift risk, not a design flaw |
| Redirect URI attacks (open redirect, URI hijack) | Authorization-code interception | Exact-match redirect URI registration per client/environment (§6), no wildcard redirect URIs | Standard OIDC risk category; mitigated by strict registration, not eliminated in principle |
| Authorization-code interception | Code exchanged by an attacker instead of the legitimate client | PKCE (mandatory, §14) — a stolen authorization code is useless without the original code verifier | None beyond a fully compromised client device, which PKCE does not and cannot address |
| PKCE bypass | Same as above, if PKCE were ever disabled | Architecture mandates PKCE for the public desktop/mobile client type with no fallback flow | None, provided the recommendation is actually enforced in the Entra application registration (an operational/configuration checklist item, §23) |
| Local token theft | Stolen cached token used elsewhere | DPAPI/Keystore/Keychain per platform (§14) | Same limits as "compromised client" above |
| Admin privilege abuse | A compromised or malicious `SuperAdmin` account causes broad damage | Separate admin tenant/audience (§12), least-privilege role split already in PO-D9, every admin action audited (§15) | Residual: insider-threat risk is never fully eliminated by architecture alone; break-glass process (§12, flagged open) would further bound this |
| Account deletion abuse (using closure to erase audit/billing history) | Fraud/chargeback evasion | §8's explicit rule: `Closed` never deletes prior `UsageRecord`/`AuditEvent`/`BillingEvent` rows — closure is additive, not destructive | None identified, provided the implementation actually enforces "closure never deletes," which is a direct, testable requirement for the eventual implementation phase |
| Support impersonation (an admin silently acting as a customer) | Undetected unauthorized action on a customer's behalf | Not built (§12) — if ever built, must be its own explicitly audited action | Currently zero risk, because the capability does not exist; flagged so it isn't added later without this same audit requirement |

## 17. Data model impact

**No schema changes are required by this phase.** Every entity referenced in this
design (`Account`, `Profile`, `AuditEvent`) already has the fields this design needs:

- `Account.Status` is already an enum (`AccountStatus`) — widening it from two values to
  up to five (`Pending`/`Active`/`Suspended`/`Disabled`/`Closed`) is a **future,
  additive enum change**, not a new column, new table, or new relationship. It is
  explicitly **not proposed for implementation in this design-only phase** — it is
  named here so a future implementation phase has an exact, reviewed target rather than
  inventing the state set at implementation time.
- `Profile` needs zero new fields for the lifecycle designed in §9 — every field this
  design references (`DisplayName`, `PreferredLanguagePair`, `CreatedAt`, `UpdatedAt`)
  already exists.
- `AuditEvent.EventType` is already a free-form string — every new event name in §15 is
  a new *value*, not a new *column*.
- **No schema change is proposed for provisioning tracking** (e.g., no new
  "ProvisioningAttempt" table) — the existing `(ExternalIdentityProvider,
  ExternalSubjectId)` unique constraint on `Account` is already sufficient to prevent
  duplicate provisioning (§14 below), and `AuditEvent` is already sufficient to record
  that a provisioning happened. Adding a dedicated table would be exactly the kind of
  unnecessary schema expansion this document is instructed to avoid.

## 18. Commercial lifecycle integration

```
Identity (Entra) → Account → Subscription → Entitlement → Device → Provider Access → Translation Session → Usage
```

No layer may bypass another — this is already true today and Phase 7.0 introduces
nothing that weakens it. Worked examples:

- **New customer** (Option D provisioning approved): Entra sign-up → first
  authenticated request → `Account` (and `Profile`) created, `Active` → no
  `Subscription` yet (Phase 6.6's own logic already handles "no subscription" as a
  fail-closed default, e.g. `DeviceRegistrationService`'s documented "no-subscription
  fail-closed default (limit 1)" behavior, confirmed in Phase 6.9 test fixtures) → the
  *first* subscription action (trial start or purchase) is entirely Phase 6.6's
  existing, frozen responsibility, not Phase 7.0's.
- **Suspended customer**: `Account.Status = Suspended` → blocked at
  `AccountResolutionMiddleware`, before any `Subscription`/`Device`/`ProviderAccess`/
  `TranslationSession` code ever runs — the block happens at the *identity* layer, so
  none of the layers below it need their own redundant "is this account suspended"
  check (though several already have one anyway, as defense-in-depth, e.g.
  `AuthorizationService.HasAtLeastRole`'s own `IsUsable` check).
- **Cancelled subscription**: unaffected by anything in this phase — `Account` stays
  `Active` (a customer can cancel their subscription and still sign in to view billing
  history/reactivate), `Subscription.Status = Cancelled` (Phase 6.6, unchanged) is what
  actually blocks new usage, via the existing `EntitlementService` gate.
- **Expired subscription**: same reasoning — an `Account` does not need to become
  non-`Active` merely because its `Subscription` expired; the two are deliberately
  independent axes, matching the same reasoning in §8 for why `Account.Status` does not
  duplicate `Subscription.Status`.
- **Closed account**: `Account.Status = Closed` → blocked at
  `AccountResolutionMiddleware` permanently → any still-`Active` `Subscription` should
  be independently cancelled through Phase 6.6's own cancellation path (a coordinated
  *operational* step when closing an account, not a new code path that reaches into
  `Subscription` from the account-lifecycle layer — keeping the layers from bypassing
  each other, per this section's own instruction, means account closure orchestrates a
  call into the existing subscription-cancellation logic rather than mutating
  `Subscription` state directly from account-lifecycle code).
- **Deleted identity** (a customer deletes their Entra identity entirely, e.g. via
  Entra's own self-service, if enabled): the AUTRAXIS `Account` row is **not**
  automatically deleted — `(ExternalIdentityProvider, ExternalSubjectId)` simply becomes
  an identity that can never authenticate again. This is actually the *safe* outcome
  for the exact reason §8/§16 both emphasize: the `Account`'s billing/usage/audit
  history must survive independent of what happens to the upstream identity. If the
  product later wants "identity deletion" to also trigger `Account` closure, that would
  be a deliberate webhook/notification integration with Entra — not designed here, and
  not required for Phase 7.0's scope.

## 19. Privacy / data minimization

**What AUTRAXIS actually needs, per entity, based on this inspection**:

- `Account`: `Email` (already collected — needed for identity linking/display and
  transactional communication, e.g. billing receipts via Phase 6.6's frozen billing
  provider), `EmailVerified`, the identity-mapping pair, `Role`, `Status`, `CreatedAt`,
  `DeletionRequestedAt`. Nothing here is proposed to grow.
- `Profile`: `DisplayName`, `PreferredLanguagePair` — both optional, both already
  minimal (§9). No IP address, no device fingerprint, no location data is proposed to
  be added to `Profile` or `Account`.
- **IP addresses**: not currently stored on any entity in this codebase, and this
  design does not propose adding IP storage to `Account`/`Profile`/`AuditEvent`. If a
  future fraud/abuse-detection need (§7's rate-limiting gate) requires *ephemeral*
  IP-based rate-limiting, that state can live in a short-TTL cache (e.g., the same
  infrastructure a rate limiter would use generally) rather than a persisted, permanent
  column on any customer-facing entity — this keeps IP data out of long-term storage
  entirely, which is the strongest minimization available.
- **Device information**: already minimal per Phase 6.2B §6 (no hardware
  fingerprinting) — unchanged, not revisited here.
- **Authentication metadata**: `AuditEvent.Metadata` for auth-related events (§15)
  should carry only outcome categories, never raw claims, tokens, or headers —
  restates existing discipline.

**Deletion / retention** (engineering framing only — legal specifics are §26/product-
owner items, not decided here):
- `Account` soft-delete (`DeletionRequestedAt`) already exists and is the correct
  mechanism to extend for `Closed` (§8) — a hard purge remains a **separate,
  not-yet-designed process**, exactly as the entity's own doc comment already states.
- `Profile` may be hard-deleted once its `Account` is hard-purged (§9) — no earlier,
  since retention windows (whatever they turn out to be) apply to the `Account` as a
  whole, not to `Profile` independently.
- `AuditEvent`/`UsageRecord`/`BillingEvent` rows must **never** be deleted as part of
  account closure (§8/§16) — any eventual retention-expiry deletion of these rows is a
  distinct, time-based retention policy, not an account-closure side effect.
- **GDPR-oriented considerations** (flagged, not resolved): a"right to erasure" request
  would need to reconcile against retained billing/audit records that may be legally
  required to be kept for a period regardless of the erasure request — this exact
  tension is a **legal decision** (§26), not something this architecture document
  resolves; the engineering answer is only that the architecture must be *capable* of
  distinguishing "erase customer-identifying `Profile`/`Account` fields" from "must
  retain financial/audit records for period X," which the existing entity separation
  already supports (`Profile`'s minimal, hard-deletable shape vs. `AuditEvent`/
  `UsageRecord`'s append-only, retained shape).

## 20. Environment strategy

| | Development | Staging | Production |
|---|---|---|---|
| Entra tenant | Separate dev tenant | Separate staging tenant | Separate production tenant |
| Client (app) registrations | Dev-only redirect URIs/client IDs | Staging-only | Production-only |
| `Identity:Authority`/`Identity:Audience` | Dev values, via local config/environment variable, never committed (unchanged pattern) | Staging values, via the deployment environment's secret/config mechanism | Production values, via the deployment environment's secret/config mechanism |
| Database | Already isolated per Phase 6.5's `Database:ConnectionString` pattern (empty-committed, environment-supplied) — unchanged | Same pattern, staging connection string | Same pattern, production connection string |
| Signing-key assumptions | Dev tenant's own JWKS (framework-fetched, unchanged) | Staging tenant's own JWKS | Production tenant's own JWKS — **never** shared with staging/dev, since a staging-issued token must never validate against production audience/authority |
| Admin tenant (§12) | A separate dev workforce tenant, or a well-scoped subset of admin test accounts in the dev customer tenant if a fully separate dev workforce tenant is judged unnecessary overhead for non-production | Separate staging workforce tenant | Separate production workforce tenant, most tightly access-controlled of the three |

**Production must never share, with any other environment**: tenant configuration,
client IDs, redirect URIs, secrets/connection strings, databases, or signing-key
material — this is a direct restatement of the instruction and matches the
configuration-boundary discipline already established for `Identity:*`/`Database:*`/
`ProviderCredentials:*`/`Billing:*` since Phase 6.4/6.5/6.6/6.8.

## 21. Observability

Safe, additive metrics/logs (no schema/infrastructure choice made here — this is a
requirements list for whatever the eventual metrics/logging stack is):

- Authentication success/denial counts, broken down by denial *reason category*
  (`invalid_identity_claims`/`account_not_found`/`account_suspended`/new states from
  §8), never by identity.
- Account-provisioning success/failure counts (if §7's Option D is approved), including
  counts specifically attributable to rate-limit or `email_verified`-gate denials — this
  is the primary signal for detecting the abuse pattern Option D is designed to bound.
- Suspended/disabled-account access-attempt counts — a sustained attempt pattern
  against one account is itself a signal worth alerting on (e.g., a customer's
  compromised credential still being used post-suspension).
- Token validation failure counts by *category* (expired/invalid-signature/invalid-
  issuer/invalid-audience) — already partially logged today
  (`OnAuthenticationFailed` logs `context.Exception.GetType().Name`); this recommends
  making that a first-class metric, not only a log line.
- Provisioning-conflict counts (a concurrent-first-login race, §14, resulting in a
  caught unique-constraint violation rather than a true failure) — useful for capacity/
  concurrency monitoring, not itself concerning if the rate is low and consistently
  resolved correctly.

**Never log**: tokens (access, refresh, ID), the authorization code, PKCE verifiers,
passwords (never touch AUTRAXIS code), or full claim sets. Avoid logging raw email
addresses in metrics labels/tags (high-cardinality PII in a metrics system is its own
minimization concern beyond just "don't log secrets") — use `AccountId` instead,
wherever a metric needs to be attributable at all.

## 22. Test strategy (future implementation test matrix)

**Mocked-identity tests** (unit/integration, no live Entra — the pattern already
proven by `AuthenticationIntegrationTests.cs`'s `WebApplicationFactory` + static
HMAC-signing-key override, extended to new scenarios):
- Forged role claim inside an otherwise-valid JWT → proven to have no effect (extends
  the existing test, if §8's new states change any authorization-adjacent code path).
- Forged email claim → proven never used as an identity/authorization key.
- Forged/attempted client-supplied `AccountId` in a provisioning-adjacent request body
  (if §7's provisioning flow ever accepts any request body at all — recommendation:
  it should not; provisioning should require nothing beyond the already-authenticated
  token) → proven ignored/rejected.
- Duplicate first login (two sequential requests, same new identity) → second request
  resolves to the *same* `Account`, never a second one.
- Simultaneous first login (concurrent requests, same new identity, real PostgreSQL,
  Testcontainers — following the exact pattern already proven in
  `ConcurrentDeviceRegistration_NeverExceedsPooledLimit_RealPostgresLock` (Phase 6.7)
  and `ConcurrentSessionStart_NeverExceedsUsageLimit_RealPostgresLock` (Phase 6.9)) →
  exactly one `Account` row is ever committed, the unique constraint rejects every
  other concurrent attempt, and every rejected attempt safely resolves to the winning
  `Account` rather than surfacing as a customer-visible error.
- Suspended/disabled/closed account → each denies access at the same gate, with the
  correct outcome/status code, and does not leak which specific non-`Active` state
  applies (a suspended vs. closed account should arguably return the *same* generic
  denial to a client, to avoid distinguishing account states to an attacker — an
  explicit test-driven decision for the implementation phase).
- Unknown identity (no account, provisioning disabled or gate not met) → unchanged
  `account_not_found` behavior, still proven.
- Invalid audience / invalid issuer / expired token — already covered by
  `AuthenticationIntegrationTests.cs`; extend only if new audiences (§12's admin
  audience) are introduced.
- Key rollover — already implicitly covered by relying on framework-managed JWKS
  refresh (no custom code to test); if an admin-tenant second scheme is added (§12), add
  a test proving a token from tenant A's keys never validates against tenant B's
  audience/issuer configuration.
- Account isolation — `Profile` read/update, once implemented, must follow the exact
  same isolation test pattern already used for `Device`/`ProviderAccessGrant`/
  `TranslationSession` (cross-account request → non-enumerating denial).
- Admin/customer separation — a customer-tenant token must never satisfy an
  admin-audience check (§12); a dedicated test analogous to
  `ClientSuppliedRoleClaimInsideJwt_CannotEscalatePrivileges` should assert this
  structurally, not just by convention.

**Real Entra E2E tests** (explicitly distinguished from the above — genuinely
different in kind, not just "more thorough" unit tests): a small, separate test suite
that authenticates against a real (dedicated, non-production) Entra External ID tenant
end-to-end — sign-up, sign-in, token acquisition, and one full authenticated API call —
to validate the *actual* production configuration surface (redirect URIs, scopes,
tenant policies, claim shapes as Entra *actually* emits them, not as a test harness
assumes them) that no mocked test can cover. These are **not** run on every commit
(they require live network access and a maintained test tenant); they belong in a
separate, less-frequent verification gate (e.g., pre-release or nightly), distinct from
the existing fast, deterministic `VTTranslate.Backend.Tests` suite.

## 23. Production-readiness checklist

- [ ] Production Entra External ID tenant provisioned and configured (separate from
      Staging/Development).
- [ ] Separate application registrations per client platform (Windows now; Android/iOS
      reserved) and per environment, each with exact-match redirect URIs and PKCE
      required, no client secret on any public client.
- [ ] API application registration (single customer-facing audience) configured;
      `Identity:Authority`/`Identity:Audience` set via the production deployment's
      config/secret mechanism, never committed.
- [ ] Scopes reviewed and minimized to what the API actually needs to validate (audience
      match), with no unused or overly broad scope requested by any client registration.
- [ ] Signing-key rotation validated to work automatically via framework JWKS refresh in
      the actual production configuration (not just assumed from Development testing).
- [ ] Separate admin/workforce tenant (or clearly separated admin user flow) provisioned,
      with its own audience validation, before any admin capability is built.
- [ ] Environment configuration boundaries verified: no shared tenant, client ID,
      redirect URI, secret, or database between Development/Staging/Production.
- [ ] Monitoring/alerting wired for the metrics in §21 (auth denial rate, provisioning
      denial rate, suspended-account access attempts, token-validation failure rate).
- [ ] Audit events (§15) verified to be emitted and queryable for every account
      lifecycle transition once implemented.
- [ ] Account-recovery process defined for a customer locked out at the Entra level
      (password reset flow tested end-to-end in the real tenant).
- [ ] Customer support access process defined and scoped to `Admin`'s existing
      view-only capability (§12) — no ad hoc elevated access outside the role model.
- [ ] Customer onboarding flow (§7's approved option) tested end-to-end against the real
      tenant, including the specific gate parameters the product owner selects.
- [ ] Account closure flow tested end-to-end, including verification that billing/audit/
      usage history is provably retained after closure (§8/§16/§19).
- [ ] A dedicated security review of the actual production Entra tenant configuration
      (custom policies, claim mapping) — per the existing, still-open risk flagged in
      `docs/phase-6.2b-resolved-architecture-decisions.md` §18, not yet closed by any
      later phase.

## 24. Frozen-area dependencies (documented, not modified)

- **Phase 6.6 (billing)**: depends on `Account.Id` existing and `Account.IsUsable`
  gating access — unchanged; this phase's new `Account.Status` values (§8) must,
  when eventually implemented, be included in whatever `IsUsable`-equivalent check
  Phase 6.6's own code already performs (today, `Subscription`/billing logic does not
  itself re-check `Account.Status` — it relies on `AccountResolutionMiddleware` having
  already gated the request, which remains true and sufficient under the new states).
- **Phase 6.7 (device licensing)**: depends on `Account.Id` and the existing device-
  registration concurrency lock — unaffected; no device-pooling policy question (still
  open per Phase 6.2B §19) is touched by this phase.
- **Phase 6.8 (provider access)**: depends on `Account.Id`/`Device.Id` and the existing
  entitlement gate — unaffected.
- **Phase 6.9 (usage metering)**: depends on `Account.Id`/`Device.Id` and the existing
  entitlement/lease/expiry model — unaffected; §8 above explicitly relies on Phase
  6.9's *existing* lazy-expiry behavior to naturally close out sessions after an
  account-level lockout, rather than adding a new forced-termination path into that
  frozen service.
- **Azure Speech pipeline / audio routing / WPF translation engine / Gemini
  naturalization**: no dependency in either direction — this phase's scope is entirely
  above the translation engine, in the identity/account/commercial-orchestration layer.

## 25. Recommended implementation sequence

(For a future implementation phase — not started here.)

1. Product-owner decision on §7 (provisioning option) and §8 (which new `AccountStatus`
   values are actually approved) — nothing below can be correctly sized without this.
2. `AccountStatus` enum extension + `Account.IsUsable` update (small, additive, no
   migration beyond an enum-string mapping change if EF stores it as a string — confirm
   against the existing `HasConversion<string>()` pattern used elsewhere, e.g.
   `TranslationSessionConfiguration`).
3. `IAccountProvisioningService` (new, per §5's seam) + concurrency-safe first-login
   flow (§14 requirements) + real-PostgreSQL concurrent-first-login test (mirroring the
   Phase 6.7/6.9 pattern).
4. `Profile` application service + `/profile` GET/PUT endpoints (wiring the already-
   persisted-but-unused entity), with account-isolation tests mirroring existing
   patterns.
5. Audit events (§15) wired into every new transition from steps 2–4.
6. Admin/workforce tenant + second-audience validation (§12) — can proceed in parallel
   with steps 2–5 once decided, since it has no dependency on the provisioning decision.
7. Windows client authentication flow (§14) implementation — depends on steps 1–3 being
   stable, since the client needs a real, decided provisioning behavior to design its
   own first-login UX against.
8. Production Entra tenant setup + the full §23 checklist, gated on a security review.

## 26. Open product-owner decisions (do not silently resolve)

| # | Decision | Options | Recommendation | Why | Impact | Owner |
|---|---|---|---|---|---|---|
| 1 | Account provisioning strategy (§7) | A: none / B: unrestricted JIT / C: invitation / D: gated JIT | **D** | Only launch-viable option that bounds automated abuse without a separate invitation subsystem | Determines whether any self-serve signup is possible at all before further work proceeds | Product owner |
| 2 | If D: specific gate parameters (rate-limit thresholds, whether `email_verified` is a hard or soft gate, any email-domain policy) | Configurable values | Not recommended here — mirrors the existing "trial values NOT YET DECIDED" pattern from Phase 6.2B | Values, not structure, are the open item; inventing them would misrepresent a product/business call as an engineering one | Determines abuse-resistance vs. signup friction trade-off | Product owner |
| 3 | Whether `Pending`/`Disabled`/`Closed` `AccountStatus` values (§8) are all approved, or only a subset | Adopt all three / adopt fewer | Adopt all three, as designed | Each answers a distinct, real question (provisioning-gate state, long-term deactivation short of closure, terminal closure) — omitting one would leave that question unanswered again | Determines the exact enum an implementation phase targets | Product owner |
| 4 | Whether customer MFA is required, optional, or risk-based (§6) | Required / optional / risk-based (Conditional Access) | Not recommended here | Pure product/security-posture trade-off (friction vs. account-security), no engineering constraint favors one | Affects Entra tenant configuration only, no backend code impact either way | Product owner + Security |
| 5 | Admin tenant model (§12): fully separate Entra ID workforce tenant vs. a segregated flow within the same External ID tenant | Separate tenant / segregated flow | Separate tenant | Structurally prevents any customer-signup-flow token from ever satisfying an admin audience check — segregated-flow-within-one-tenant relies on configuration discipline rather than structural separation | Determines admin-boundary security posture before any admin API is designed | Product owner + Security |
| 6 | Break-glass admin access process (§12) | Not designed | N/A — flagged only | Requires an incident-response decision outside this document's engineering scope | Determines recovery capability during an admin-account lockout incident | Security / Product owner |
| 7 | Whether `SuperAdmin` impersonation of a customer account is ever built (§12) | Build / do not build | Do not build unless a stated support requirement justifies it | No current requirement exists in this codebase for it | Avoids building a sensitive capability speculatively | Product owner |
| 8 | Data retention periods for `AuditEvent`/`UsageRecord`/`Account` post-closure (§19) | Specific durations | Not recommended here | Legal/regulatory decision (GDPR-oriented considerations, §19), not an engineering one | Determines when hard-purge processes (still undesigned) may ever run | Legal / Product owner |
| 9 | Whether an account-merge ("same person, two identities") capability is ever built (§10) | Build / do not build | Do not build until a stated requirement exists | No requirement exists anywhere in this codebase's history | Avoids speculative schema/process work | Product owner |
| 10 | Federated identity providers beyond email/password (§6), timeline/priority | Specific providers, specific order | Not recommended here | Already flagged NOT YET DECIDED in Phase 6.2B §19, unchanged by this phase | Affects Entra tenant configuration and `EntraIdentityProvider` claim-mapping breadth only if a federated provider surfaces different claim names | Product owner |

## Risks and mitigations (summary)

The full threat model is §16; the highest-leverage risks specific to *this phase's*
recommendation (Option D provisioning) are: (1) automated fake-account creation despite
rate-limiting/`email_verified` gating — mitigated by treating it as an expected,
bounded residual risk requiring product-level fraud monitoring, not an architecture
failure; (2) a first-login concurrency bug creating a duplicate or partially-created
account — mitigated by the real-PostgreSQL concurrency test pattern already proven
twice in this codebase (Phases 6.7, 6.9) being required, not optional, for the
eventual implementation (§22); (3) scope creep into building admin/impersonation/
account-merge capabilities without a stated requirement — mitigated by this document
explicitly recommending against building any of them absent a specific, stated need
(§12, §26 items 6/7/9).

---

## Confirmations

- **Only documentation changed.** This phase produced exactly one new file:
  `docs/phase-7.0-production-identity-and-account-lifecycle.md`. No other file in the
  repository was created or modified.
- **No production source code changed.** No `.cs` file, no `Program.cs` change, no new
  service, no new endpoint.
- **No migration created or modified.** The EF model and every existing migration are
  untouched.
- **No API endpoint added.**
- **No UI (WPF or otherwise) modified.**
- **No frozen phase (6.6 billing, 6.7 device licensing, 6.8 provider access, 6.9 usage
  metering, Azure Speech pipeline, audio routing, translation engine, Gemini
  naturalization) redesigned or modified** — every dependency on those phases is
  documented in §24, not altered.
- **git status is clean except for this one new documentation file.**

**Phase 7.0 architecture/design review complete — implemented below.**

---

## Addendum: Implementation status (Phase 7.0 implementation pass)

This addendum records what was actually built, so this document stays consistent with
the code rather than purely aspirational. It does not replace any section above — the
architecture as designed was implemented essentially as-is, with the deviations noted
here.

**Implemented:**
- `AccountStatus` extended to `Pending`/`Active`/`Suspended`/`Disabled`/`Closed` (no
  migration required — the column was already string-mapped,
  `HasMaxLength(20)`, in `AccountConfiguration`).
- `IAccountProvisioningService`/`AccountProvisioningService` (Application/Identity) —
  gated JIT provisioning: enabled/disabled by configuration, `EmailVerified` gate,
  per-identity `(Provider, ExternalSubjectId)`-keyed rate limiting, transactional
  Account+Profile+`AccountProvisioned`-audit write, and the exact race-resolution
  behavior designed in §7/§14/§24 (a losing `DuplicateIdentityException` — new,
  translated from the database's unique-constraint violation exactly like
  `DuplicateBillingEventException`/`ActiveSessionAlreadyExistsException` — causes the
  transaction to roll back cleanly, then a fresh read resolves the winning Account).
  Wired into `AccountResolutionMiddleware` as the sole call site, on
  `AccountResolutionOutcome.AccountNotFound` only — `AccountResolutionService` itself
  remains pure resolution-only and untouched.
- `IProvisioningRateLimiter`/`InMemoryProvisioningRateLimiter` (Infrastructure) —
  explicitly documented as NOT production-grade for a multi-instance deployment; a
  distributed implementation is a production requirement, not built here.
- `IAccountLifecycleService`/`AccountLifecycleService` (Application/Identity) —
  `Activate`/`Suspend`/`Disable`/`Close`, enforcing exactly the transition matrix in §9,
  idempotent on a same-state request, auditing every real transition. Not wired to any
  API endpoint (per §8/§32 — no admin API in this phase); exists as a tested
  domain/application capability for a future admin surface.
- `IProfileService`/`ProfileService` (Application/Profiles) wiring the previously-unused
  `Profile` entity, plus `GET /profile` and `PUT /profile` (account-isolated,
  `AccountId` sourced only from `AccountResolutionMiddleware`, `DisplayName` length- and
  `PreferredLanguagePair` shape-validated).
- Admin/customer authentication boundary: a second, named JwtBearer scheme
  (`AdminIdentityOptions.SchemeName = "AdminScheme"`) bound to a new, separate
  `Identity:Admin:Authority`/`Identity:Admin:Audience` configuration namespace (the
  existing customer `Identity:Authority`/`Identity:Audience` keys are unchanged, per the
  explicit instruction to preserve them) — a corresponding `"AdminScheme"` authorization
  policy. No admin API endpoint exists; a single authorization-boundary-proof-only
  placeholder route (`/internal/admin-boundary`, `.RequireAuthorization("AdminScheme")`)
  mirrors the exact precedent already set by Phase 6.4's own
  `/internal/diagnostics` role-boundary-proof route, so the boundary is exercised
  end-to-end over real HTTP in tests without constituting a product feature.
- Configuration: `AccountProvisioningOptions` (`AccountProvisioning` section — `Enabled`,
  `RequireEmailVerified`, `RateLimitMaxAttempts`, `RateLimitWindowSeconds`, all with safe
  documented defaults) and `AdminIdentityOptions` (`Identity:Admin` section) —
  both non-secret, both empty/default in the committed `appsettings.json`, both covered
  by extended `ConfigurationSecretSafetyTests`.

**Test coverage added** (see §H of the final implementation report for exact counts):
unit tests for provisioning (verified/unverified, enabled/disabled, rate-limited, no
client AccountId/Role acceptance, forged-email non-redirection, repeated-first-login
resolution, transactional Profile/audit creation), unit tests for every lifecycle
transition (legal/illegal/idempotent/audit), unit tests for Profile CRUD/validation/
isolation, HTTP-boundary tests for `/profile` (including the real gated-provisioning
path exercised end-to-end, suspended/disabled/closed/pending denial, forged-role-claim
non-elevation, cross-account isolation, forged-AccountId-in-body no-effect), HTTP
boundary tests for the admin/customer scheme separation (`CustomerToken_CannotAuthenticateAsAdmin`,
`AdminToken_CannotAuthenticateAsCustomer`, forged role/audience/issuer non-crossing), and
a dedicated real-PostgreSQL test class addition: `Account_NewStatusValues_PersistAndRoundTrip`,
`AccountProvisioning_ExceptionInsideTransaction_RollsBackAccountAndProfileAndAudit`, and
`ConcurrentFirstLogin_SameIdentity_CreatesExactlyOneAccount_RealPostgres` (10 concurrent
attempts against a real PostgreSQL instance, each on its own connection/service graph,
proving exactly one Account/Profile/audit row is ever committed).

**Production Entra configuration required before go-live** (unchanged from §6/§20/§23):
a real customer Entra External ID tenant and a real, separate admin/workforce tenant,
each environment-specific, neither committed to source control — this implementation
pass only makes the code capable of consuming that configuration; it does not (and per
the master prompt, must not) fabricate it.

**Distributed rate-limit production requirement**: explicitly documented on
`InMemoryProvisioningRateLimiter` itself — a multi-instance production deployment must
replace that registration with a distributed-store-backed `IProvisioningRateLimiter`
implementation before relying on the rate limit across more than one API instance.

**Real Entra E2E**: not built (no live non-production tenant is available in this
environment) — the deterministic mocked-JWT test strategy in §22 is what was
implemented; the real-tenant E2E checklist in §22/§23 remains a pre-release,
manually-executed gate.

**Windows client authentication**: unchanged from §14's design — no WPF code was
modified in this implementation pass (out of scope; the backend boundary this design
requires is what was built).

**Deviations from the design document**: none of substance. The only refinement is the
admin configuration namespace: §14 offered `Identity:Customer:Authority`/
`Identity:Customer:Audience` alongside `Identity:Admin:*` as one example shape; the
implementation instead kept the existing `Identity:Authority`/`Identity:Audience` keys
completely unchanged (as multiple instructions independently required) and added only
`Identity:Admin:*` alongside them — the "or another clean equivalent" the master prompt
explicitly allowed.

**STOP — Phase 7.0 implementation complete. No Phase 7.1, billing integration, mobile
implementation, commercial UI, or admin API was started, per explicit instruction.**
