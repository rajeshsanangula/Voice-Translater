# Phase 6.2B — Resolved Architecture Decisions

**Status: DESIGN ONLY. No production code, cloud resources, database, authentication library, or payment integration was created or modified in this phase.** This document incorporates the product owner's decisions on the open items raised in `docs/phase-6.2-architecture-decision-matrix.md` ("Phase 6.2") and updates the architecture from `docs/phase-6-account-subscription-device-architecture.md` ("Phase 6.1") accordingly. Terminology and entity names are kept consistent with both prior documents. Every statement below is explicitly labeled:

- **APPROVED** — the product owner has directly decided this; treat as settled for planning purposes, though still not implemented until its own phase.
- **RECOMMENDED** — a direction the product owner endorsed for evaluation, but not yet a locked-in, irreversible commitment (e.g., "evaluate Paddle" is not the same as "Paddle is integrated").
- **NOT YET DECIDED** — still open; a placeholder or configurable value stands in for it.
- **REQUIRES BUSINESS/LEGAL VERIFICATION** — outside this document's authority to decide; flagged for the appropriate non-engineering function.

To avoid numbering collisions with Phase 6.2's own D1–D14, this document refers to the product owner's nine new decisions as **PO-D1 through PO-D9** (matching the order given in the Step 6.2B instruction) and cross-references them to the specific Phase 6.2 item(s) they resolve.

---

## 1. Approved product decisions (summary table)

| # | Topic | Resolution | Status | Resolves |
|---|---|---|---|---|
| PO-D1 | Identity provider | Microsoft Entra External ID, standards-based OIDC/OAuth 2.0; product depends on an internal AUTRAXIS account abstraction, not IdP-specific code | **APPROVED** (direction) | Phase 6.2 D1 |
| PO-D2 | Billing/payment | Evaluate Paddle (Merchant of Record) as the initial commercial billing model, behind a billing abstraction | **RECOMMENDED** (evaluation only — not integrated) | Phase 6.2 D4, D14 |
| PO-D3 | Device identity model | Account-based, server-authorized devices; explicitly NOT hardware-fingerprint-primary | **APPROVED** | Confirms Phase 6.1 §6 design as-is |
| PO-D4 | Trial structure | Configurable time-limited trial AND configurable usage limit (not a fixed final value) | **APPROVED** (structure); **NOT YET DECIDED** (values) | Phase 6.2 D7 |
| PO-D5 | Grace period | A bounded grace period exists, for temporary payment/status/backend problems only — never an indefinite offline bypass | **APPROVED** (structure); **NOT YET DECIDED** (length) | Phase 6.2 D8 |
| PO-D6 | Usage metering | Server-authoritative; client may display, never authoritative | **APPROVED** | Phase 6.2 D9 |
| PO-D7 | Registration method | Email/password through the chosen identity architecture, designed for future federated-identity extension without account-model redesign | **APPROVED** | Phase 6.2 D12 |
| PO-D8 | Merchant/tax architecture | Billing-provider/merchant-of-record logic isolated behind an abstraction; no Paddle-specific concepts in the core domain; no legal/tax advice given here | **APPROVED** (architectural pattern); tax/legal specifics **REQUIRE BUSINESS/LEGAL VERIFICATION** | Phase 6.2 D14 |
| PO-D9 | Roles | `CUSTOMER`, `ADMIN`, `SUPER_ADMIN`, backend-authoritative | **APPROVED** | Closes the "customer vs. admin roles" gap flagged in Phase 6.2 |

Everything below expands each row into the architecture it implies, while being explicit about what is now locked versus still open.

---

## 2. Identity architecture

**APPROVED direction**: Microsoft Entra External ID (Microsoft's current customer-identity product; the natural successor to the "Azure AD B2C" option evaluated in Phase 6.2 D1 — this is a refinement of that recommendation, not a reversal: it stays Microsoft/Azure-aligned, consistent with Phase 6.2 D1's single-vendor reasoning, while committing to a standards-based, OIDC/OAuth 2.0-first posture explicitly requested by the product owner for Windows/Android/iOS portability).

**Design principle (APPROVED, load-bearing)**: the rest of the product — desktop app, future mobile apps, and every other backend component (subscription, entitlement, device, usage) — depends on an **internal `AutraxisIdentity` abstraction**, never on Entra-specific types, SDK calls, or claim shapes directly. Concretely:

```
IIdentityProvider (new internal interface — backend-only)
    Task<AuthResult> RegisterAsync(...)
    Task<AuthResult> LoginAsync(...)
    Task<AuthResult> RefreshAsync(...)
    Task RevokeAsync(...)
    Task<AutraxisIdentity> ValidateTokenAsync(...)

AutraxisIdentity (internal DTO — the ONLY shape the rest of the backend sees)
    AccountId, Email, EmailVerified, Roles[], DeviceId (from token claim, §6/§8), IssuedAt, ExpiresAt
```

`EntraExternalIdProvider` is the one (and, for now, only) implementation of `IIdentityProvider`. This mirrors a pattern already proven in this exact codebase: `ISpeechTranslationProvider` isolates `AzureSpeechTranslationProvider` from every caller in exactly the same way (Phase 6.1 §5, §12). Adding a second identity provider later (Google/Microsoft-personal/Apple/enterprise SSO — PO-D7) means writing a second `IIdentityProvider` implementation, never touching `Account`, `Subscription`, `Device`, or any UI beyond the login screen's provider list.

**Token model**: standards-based OIDC — Entra issues signed JWT access tokens + refresh tokens natively. This resolves Phase 6.2 D11 (JWT vs. opaque) **by inheritance**, exactly as that document predicted ("effectively decided by D1 in practice") — status: **APPROVED as a consequence of PO-D1**, not independently re-litigated.

---

## 3. Account architecture

Unchanged in shape from Phase 6.1 §3/§8 (`Account` + `Profile`), now made concrete by PO-D1/PO-D7:

- `Account.Id` is the AUTRAXIS-internal identifier — **never** the identity provider's own subject identifier directly exposed to other services, so a future identity-provider migration or a second federated provider does not require renumbering every `Subscription`/`Device`/`UsageRecord` row. `Account` stores a mapping (`ExternalIdentityProvider`, `ExternalSubjectId`) internally, but all cross-entity foreign keys use `Account.Id`.
- Registration (PO-D7, **APPROVED**): email/password, via Entra External ID's native email/password flow. Email verification, password reset, and account deletion follow Phase 6.1 §3 unchanged.
- Future federated sign-in (Google/Microsoft personal/Apple/enterprise SSO): **APPROVED architecturally as a day-one design constraint** (the `IIdentityProvider` abstraction above exists specifically for this), but **NOT YET DECIDED** which providers, in what order, or on what timeline — no specific federated provider is committed to.
- Password policy specifics (Phase 6.2 D13): still **NOT YET DECIDED** — unaffected by this round of decisions.

---

## 4. Subscription architecture

Unchanged from Phase 6.1 §4's state machine (`Trial → Active → GracePeriod/Cancelled → Expired`). What PO-D2/PO-D4/PO-D5/PO-D8 add:

- **Billing provider**: Paddle (Merchant of Record) is the **RECOMMENDED** evaluation target — not integrated, not contracted, not confirmed. The `Subscription` entity (Phase 6.1 §8) already stores only an opaque `BillingProviderSubscriptionId`, which was designed precisely so this decision could be made without a schema change — no update needed there.
- **Trial (PO-D4, APPROVED structure)**: both a time bound AND a usage bound apply together (not either/or, resolving Phase 6.2 D7 toward "hybrid," which that document explicitly declined to pick). Both are represented as `Entitlement` values on the Trial `Plan` row (Phase 6.1 §8's key/value entitlement shape already supports this with zero schema change) — e.g. `TrialDurationDays` and `TrialMinutesLimit` as configurable, admin-editable entitlement keys, never hard-coded constants in application code. **Exact values are NOT YET DECIDED** — placeholders only, per the explicit instruction.
- **Grace period (PO-D5, APPROVED structure)**: retained as a first-class `Subscription.Status` value (Phase 6.1 §4). Its length is a configurable value (parallel treatment to trial values above) — **NOT YET DECIDED**. Its *purpose* is now explicitly constrained: it exists only to absorb **transient** payment failures, backend unavailability, or status-check hiccups — see §12 below for the hard boundary against it becoming an offline bypass.

---

## 5. Entitlement architecture

No change to the `Plan → Entitlement → UsageRecord` model (Phase 6.1 §4/§8; Phase 6.2 D5 found no alternative to compare it against, and none is introduced here). What's now explicit:

- Every configurable numeric limit referenced anywhere in this document — trial duration, trial usage cap, grace-period length, `MaxActiveDevices` — is an `Entitlement` row scoped to a `Plan`, never a compiled-in constant. This is now a **firm architectural rule (APPROVED)**, not just a convenient side effect of the existing schema.
- Device-limit pooling (Phase 6.2 D6 — pooled across platforms vs. per-platform sub-limits) is **NOT YET DECIDED** — PO-D3 addressed *device identity*, not *device counting policy*; these are different questions and PO-D3 does not resolve D6.

---

## 6. Device licensing architecture

**PO-D3, APPROVED** — this formalizes what Phase 6.1 §6/§8 already proposed almost verbatim; the product owner has now explicitly ratified it and explicitly ruled out an alternative (hardware fingerprinting) that Phase 6.1 never proposed in the first place but which is common enough in commercial licensing that it's worth recording as a deliberate exclusion.

`Device` entity fields (unchanged from Phase 6.1 §8, now APPROVED rather than merely proposed):

```
Device
  Id                  — AUTRAXIS-issued device identifier (server-authorized, not a hardware fingerprint)
  AccountId           — FK to Account
  Platform            — Windows | Android (future) | iOS (future)
  DisplayName         — user-editable, e.g. "Sanan's Laptop"
  RegisteredAt
  LastSeenAt
  Status              — Pending | Authorized | Revoked   (authorization state)
  RevokedAt           — nullable                          (revocation state, distinct from Status for audit clarity)
```

**Why not hardware fingerprinting (explicit design rationale, since PO-D3 explicitly rules it out)**: hardware fingerprints are brittle across OS reinstalls/hardware upgrades (false revocations), are a weaker security boundary than a server-issued, server-revocable device credential (Phase 6.1 §5's short-lived-token model already makes revocation instant and reliable without needing to *identify* the hardware at all — it just stops issuing the device new tokens), and raise unnecessary privacy questions for a product that otherwise deliberately avoids collecting more than it needs (Phase 6.1 §6: "never hardware-fingerprint-based tracking beyond what's needed for licensing"). The account-based model already in Phase 6.1 is sufficient and is now the explicitly locked-in approach.

Device-count limits (`MaxActiveDevices`) and pooling policy remain governed by §5 above — **NOT YET DECIDED** on the pooling question specifically.

---

## 7. Usage metering architecture

**PO-D6, APPROVED**: server-authoritative, full stop. This directly resolves the gap Phase 6.2 D9 identified (Phase 6.1's own text flagged client-reported usage as "not trustworthy for enforcement" without fully saying what replaces it for the customer-facing summary). The resolution:

- **Authoritative usage record generation** moves from "client reports it, backend trusts it" to **backend-derived**, using the mechanism already implicit in Phase 6.1 §5: every real-time translation session requires the client to obtain a short-lived provider token from `/entitlements/provider-token` (Phase 6.1 §9). The backend already knows, from its own records, *when* a token was issued and *when it expired or was renewed*. Session duration — and therefore billable/limited usage — is computed **server-side from token issuance/renewal/expiry events**, not from a number the client sends.
- A session-end signal from the client (`/usage/report`, Phase 6.1 §9) is **retained but demoted**: it becomes a *hint* used only to close out a `UsageRecord` promptly (better UX — the summary updates immediately rather than waiting for token-expiry cleanup) and as an **anomaly-detection input** (a large mismatch between client-reported and server-derived duration is itself a signal worth an `AuditEvent`, Phase 6.1 §8), never as the source of truth for billing/enforcement.
- `/usage/summary` (Phase 6.1 §9) now explicitly serves **display-only** data, sourced from the same server-authoritative `UsageRecord` rows used for enforcement — eliminating the inconsistency Phase 6.2 flagged (previously, display and enforcement risked using different, differently-trustworthy data).
- This adds a small, well-scoped new backend responsibility (tracking token issuance/renewal/expiry as first-class events, not just a stateless mint-and-forget) — flagged here as a real scope addition versus Phase 6.1's original sketch, not a free change, and should be sized accordingly when Phase 6.9 is planned.

---

## 8. Role/authorization architecture

**PO-D9, APPROVED — new to this document; Phase 6.1 did not previously define this.**

Three roles, backend-authoritative:

| Role | Scope |
|---|---|
| `CUSTOMER` | Default role for every registered `Account`. Can manage their own profile, subscription, devices, and view their own usage. This is the entirety of what Phase 6.1's §9 API surface already assumed a caller could do. |
| `ADMIN` | AUTRAXIS staff. Can view (not silently modify) customer accounts for support purposes, revoke a customer's device on their behalf, view audit logs, and view (but not, by default, edit) `ProviderConfiguration` (Phase 6.1 §8/§13's internal secret-reference table). |
| `SUPER_ADMIN` | AUTRAXIS staff with elevated privilege: can edit `ProviderConfiguration`, override entitlements manually (e.g., a goodwill extension), and manage other admin accounts. |

**Enforcement (APPROVED, non-negotiable per the stated security requirement)**: role is carried as a **signed claim on the access token**, issued by the backend at login based on the `Account`'s role assignment stored server-side (a new field/table, not a client-supplied value at any point) — never inferred from anything the client sends, never settable by the client, never present as a boolean the client could flip locally. Every admin-surface backend endpoint checks the role claim server-side on every request, exactly like the existing entitlement-check pattern in Phase 6.1 §5/§10. **The client never grants itself administrative privileges — this is enforced by the fact that the client cannot forge a signed token claim, not by any client-side UI restriction (UI restrictions are a convenience, not the security boundary).**

**Scope note**: no dedicated "admin application" is designed here — whether admin/super-admin functions get their own minimal web app, a CLI tool, or a hidden mode of the existing backend's own tooling is **NOT YET DECIDED** and does not need to be decided before Phase 6.3–6.7 (the customer-facing path) proceed.

---

## 9. Billing abstraction

**PO-D2/PO-D8, APPROVED pattern, RECOMMENDED vendor**:

```
IBillingProvider (new internal interface — backend-only)
    Task<CheckoutSession> CreateCheckoutAsync(accountId, planId)
    Task CancelAsync(subscriptionId)
    Task<WebhookResult> HandleWebhookAsync(rawPayload, signature)   // provider-specific verification INSIDE the implementation
```

`PaddleBillingProvider` would be the first (evaluated, not yet built) implementation. The core domain (`Subscription`, `Plan`, `Entitlement` — Phase 6.1 §8) references only the interface's vendor-neutral shapes (`CheckoutSession`, `WebhookResult`) and the opaque `BillingProviderSubscriptionId` string already in the schema — **no Paddle-specific concept (their terminology for subscriptions, their specific webhook event names, their specific tax-handling fields) leaks into `VTTranslate.Backend`'s domain model.** This is the same isolation discipline already proven twice in this codebase (`ISpeechTranslationProvider` for Azure Speech, and now `IIdentityProvider` for Entra above) — applied a third time, consistently.

**Explicitly not decided here (per instruction, "do not provide legal/tax advice")**: whether Paddle's Merchant-of-Record model is the right long-term choice, what its exact fee structure means for margins, and how it interacts with any jurisdiction-specific tax registration AUTRAXIS may independently need — these are **REQUIRES BUSINESS/LEGAL VERIFICATION** items, listed exhaustively in §20.

---

## 10. Authentication/session model

Inherits directly from PO-D1 (§2 above) and Phase 6.1 §10, now made concrete:

- **Access token**: short-lived signed JWT (Entra-issued), carrying `AccountId`, `Roles[]` (§8), and — critically — a `DeviceId` claim binding the token to the specific authorized device that requested it (this is what lets §11's real-time session authorization work without a separate device-lookup on every request).
- **Refresh token**: longer-lived, rotated on every use, family-tracked for replay detection — unchanged from Phase 6.1 §10; Entra External ID supports this pattern natively as a standard OIDC refresh-token flow, so no custom rotation logic needs to be hand-built.
- **Session revocation** (`/account/sessions`, Phase 6.1 §9): unchanged — sign-out-this-device and sign-out-everywhere both remain available, now implemented as calls into `IIdentityProvider.RevokeAsync`.

---

## 11. Real-time session authorization

**Security requirement #4 ("Real-time translation sessions must be authorized by the backend") — APPROVED, and it was already the design in Phase 6.1 §5/§12; this document reconfirms it as non-negotiable given the explicit restated requirement.**

Restating the exact mechanism from Phase 6.1 §5 with the role/token model now attached: the desktop (or future mobile) client presents its access token — carrying `AccountId`, `DeviceId`, `Roles[]` — to `/entitlements/provider-token`. The backend checks (server-side, always): subscription state (§4), device authorization state (§6), and usage-against-limit (§7), and only then mints a short-lived Azure Speech token. **Nothing about this changes** from Phase 6.1; PO-D6's server-authoritative usage metering (§7 above) makes the usage-limit check in this exact gate more accurate than Phase 6.1's original sketch, but the gate itself, and the fact that it is the *only* path to a working provider credential, is unchanged and remains the single enforcement point.

---

## 12. Offline/grace-period behavior

**Security requirement #8 ("Offline behavior must not allow subscription bypass") + PO-D5 — APPROVED, tightened from Phase 6.1 §11.**

Phase 6.1 §11 already flagged a bounded offline-grace-cache as **NOT YET DECIDED** ("must be a deliberate, product-approved, time-boxed exception... must never be implemented as 'if backend unreachable, assume entitled' without an explicit expiry and re-validation requirement"). PO-D5 approves that *a* grace mechanism exists but — matching the newly restated security requirement precisely — does **not** approve indefinite offline use under any circumstance. The resulting rule, now explicit:

- A currently-active, already-issued short-lived provider token (Phase 6.1 §5) continues to work until its own short expiry (minutes) — this is not "grace," it's just the natural consequence of a token that was already validly issued. **This is the only offline continuity the architecture provides**, and it is bounded by design (the token's own lifetime), not by a separate offline-mode flag.
- `Subscription.Status = GracePeriod` (§4) is a **billing-state** concept (a payment problem or backend-side status-check hiccup), not a **client offline-mode** concept — the two must not be conflated. A client that cannot reach the backend at all cannot obtain a new provider token, cannot know it's in a grace period, and cannot start a new translation session — full stop, matching security requirement #8 exactly.
- **NOT YET DECIDED**: the exact grace-period length (§4), and whether any additional short client-side "last known good" caching (Phase 6.1 §11's "bounded grace cache" idea) is worth building at all, given how explicitly the security requirement above discourages anything resembling an offline bypass. This document's default posture, absent further product input, is to **not** build that additional client-side cache — the token-expiry-bound continuity above is likely sufficient and strictly simpler to reason about securely.

---

## 13. Provider-secret architecture

**Unchanged from Phase 6.1 §5/§10, now reconfirmed against the explicitly restated security requirements #1/#2**: Azure Speech, Azure Translator, and Gemini keys (and, newly relevant, **Paddle's API/webhook secret keys** once billing is evaluated — security requirement #2 explicitly extends this list to "payment-provider secret keys") all live only in the backend's secret store (Phase 6.2 D10 — Azure Key Vault recommended, still formally a technology choice pending confirmation once §2's Azure-alignment is locked by PO-D1's Entra decision). None of these are ever placed in the customer application, in any endpoint response, or in any log — this rule now explicitly covers the billing-provider secret alongside the AI-provider secrets, closing a gap the original Phase 6.1 document (written before billing was scoped in detail) did not explicitly enumerate.

---

## 14. Windows integration

No change to the assessment in Phase 6.1 §13/§15: the "Azure Speech Provider" card is still removed from customer view and replaced by the login screen, account/subscription/device status UI, and settings described there. New, specific to this round of decisions:

- The login screen (Phase 6.1 §13) now specifically means "Entra External ID's email/password flow, presented through the AUTRAXIS-branded UI" (PO-D1/PO-D7) — not a generic placeholder. The existing `Themes/BrandTheme.xaml`/`AppStatusKind` visual patterns (Phase 5) extend to this screen unchanged in spirit.
- The account/profile menu gains a role-aware element **only if** the signed-in account has `ADMIN`/`SUPER_ADMIN` (§8) — e.g., a hidden "Diagnostics"/admin entry point that only renders based on the token's role claim, never based on a client-side toggle. **NOT YET DECIDED** whether any admin functionality is ever exposed inside the customer desktop app at all, versus entirely in a separate admin surface (§8's scope note) — the architecture supports either without change.

---

## 15. Future Android integration

Unchanged in principle from Phase 6.1 §7, now reinforced: because PO-D1 commits to **standards-based OIDC/OAuth 2.0** specifically (not a Microsoft-proprietary SDK-only flow), an Android client can authenticate against the same Entra External ID tenant using any standard OIDC mobile library (e.g., AppAuth-Android) without requiring a Microsoft-specific dependency chain — this was the explicit reasoning behind the product owner's standards-based requirement, and it is satisfied by PO-D1 as stated. Device registration (`Platform=Android`, §6) and the entitlement/session model (§11) are unchanged across platforms by design.

---

## 16. Future iOS integration

Same reasoning as §15 — a standards-based OIDC flow (e.g., via AppAuth-iOS or Apple's native `ASWebAuthenticationSession` against a standard OIDC endpoint) works identically. No iOS-specific identity concern is introduced by PO-D1. Audio capture/playback remain the platform-specific engineering effort already noted in Phase 6.1 §7/§16 (App Store purchase reconciliation, CallKit/AVAudioSession constraints) — unaffected by this round of decisions.

---

## 17. Migration impact on the current codebase

No change to the Phase 6.1 §15 impact map's structure; this section only refines *what* fills each previously-identified slot, given PO-D1–PO-D9:

- **New backend interfaces implied** (none built yet): `IIdentityProvider` (§2), `IBillingProvider` (§9) — both would live in `VTTranslate.Backend` (the new project Phase 6.1 §15 already anticipated), never in `VTTranslate.Core`.
- **`VTTranslate.Core` remains fully untouched** — `ISpeechTranslationProvider`, `AzureSpeechTranslationProvider`, `DirectionPipeline`, `Audio/*`, `Streaming/*` (the experimental Gemini/naturalization research line) are unaffected by any decision in this document, exactly as Phase 6.1 §12 specified and as the Step 6.2B instruction explicitly reconfirms by name.
- **`VTTranslate.App` impact is unchanged in scope from Phase 6.1 §15/§13** (login screen, account menu, subscription/device status, removal of the Azure Speech Provider card) — this document adds no new UI surface beyond the role-aware admin-entry-point note in §14, which is itself explicitly not yet decided.
- **`AppSettings.cs`**: unchanged assessment from Phase 6.1 §15 — would eventually gain cached account/device-session state, with the existing environment-variable credential path preserved as an internal/dev-mode fallback, additively.

---

## 18. Security risks

Carried forward from Phase 6.1 §16, plus what this round of decisions newly surfaces:

- **Entra External ID tenant configuration risk**: standards-based OIDC is the right *requirement*, but Entra's own configuration surface (custom policies, token claim mapping for `DeviceId`/`Roles[]`) still needs careful setup to avoid accidentally trusting a client-supplied claim instead of a server-issued one — worth a dedicated security review at Phase 6.3, not assumed safe by default just because the underlying standard is sound.
- **Role-claim tampering risk**: since `Roles[]` rides on the access token, the signing-key integrity of whichever token issuer is used (Entra) becomes directly responsible for preventing privilege escalation — this is a standard OIDC property, not a new risk category, but it is now a *named* dependency of the role model (§8) and should be explicitly covered in any future penetration-testing scope.
- **Server-authoritative usage metering (§7) adds new backend state** (token issuance/renewal/expiry tracking) that did not exist in Phase 6.1's original sketch — more server-side state generally means more attack surface and more operational complexity (what happens if this tracking service itself has an outage mid-session?) — flagged as a genuine new consideration for Phase 6.9's design, not dismissed.
- **Billing-secret exposure** (§13): now explicitly named alongside AI-provider secrets — the same secret-store discipline applies, but this is a reminder that "provider secrets" in security requirement #2 is broader than just the AI providers this project has focused on so far.
- **Admin/super-admin account compromise**: introducing privileged roles (§8) introduces a new high-value target (a compromised `SUPER_ADMIN` account could edit `ProviderConfiguration` or override entitlements) — this is an inherent, expected consequence of adding roles, not a flaw in the design, but it means admin-account security (e.g., mandatory stronger authentication for those roles specifically) should be revisited once §8's scope note (dedicated admin surface or not) is resolved.

---

## 19. Remaining decisions

Everything from Phase 6.2's "Decisions Required From Product Owner" list that this round did **not** resolve, plus new items this round introduced:

- **Not resolved by this round** (unchanged status from Phase 6.2): device-pooling policy (Phase 6.2 D6), exact trial values (§4), exact grace-period length (§4), password policy specifics (Phase 6.2 D13).
- **Newly introduced by this round, NOT YET DECIDED**:
  - Whether any admin/super-admin functionality is ever exposed inside the customer desktop app, or lives entirely in a separate surface (§8, §14).
  - Timeline/priority for federated identity providers beyond email/password (§3) — none committed.
  - Whether the additional client-side "last known good" offline cache (Phase 6.1 §11) is built at all, given §12's default lean against it.
  - Secret-store vendor confirmation (Azure Key Vault, per Phase 6.2 D10) — not independently re-decided here, still formally open pending final confirmation that the backend stack is Azure-hosted end to end.

---

## 20. Items requiring legal/business verification

Explicitly not addressed by this document (engineering architecture only) — listed exhaustively so nothing is silently assumed:

1. **Paddle Merchant-of-Record suitability** for AUTRAXIS's actual target markets, entity structure, and expected transaction volumes — a business/finance decision, not resolved by recommending evaluation.
2. **Tax registration and compliance obligations** in every jurisdiction AUTRAXIS intends to sell into, and whether Paddle's Merchant-of-Record status fully discharges them or only partially — REQUIRES LEGAL VERIFICATION, explicitly not assessed here per the instruction not to provide tax/legal advice.
3. **Terms of Service, Privacy Policy, and Refund Policy** drafting — none exist yet and none are drafted by this document; required before any commercial checkout flow (Phase 6.5) goes live.
4. **Data residency/privacy regulatory scope** (e.g., GDPR if selling into the EU, CCPA if selling into California) — affects where the database (Phase 6.2 D3) and identity tenant (PO-D1) are hosted/regioned; not assessed here.
5. **Business entity and merchant-of-record contractual onboarding** with Paddle (or whichever provider is ultimately chosen) — a business/legal process, not an engineering one.
6. **Future mobile app-store merchant agreements** (Apple Developer Program, Google Play Developer account) and their own tax/revenue-share terms, relevant once Android/iOS billing (§15/§16, Phase 6.1 §7) is actually built — not needed before Phase 6.3–6.9, but flagged so it isn't forgotten.
7. **Employment/contractor status of anyone granted `ADMIN`/`SUPER_ADMIN` access** (§8) and any associated access-control/background-check policy — an HR/operational decision, not an engineering one, though the technical enforcement (§8) is ready to support whatever policy is decided.

---

## Confirmations

- **Only documentation changed.** This phase produced exactly one new file (`docs/phase-6.2b-resolved-architecture-decisions.md`); no other file in the repository was created or modified.
- **No production code changed.** `MainWindow.xaml`, `MainViewModel.cs`, `DirectionPipeline.cs`, `AzureSpeechTranslationProvider.cs`, `GeminiNaturalizationProvider.cs`, and `MeaningPreservationValidator.cs` were not touched, consistent with the explicit "do not modify" list in this step's instructions.
- **No cloud resources were created.** No Entra External ID tenant, database, Key Vault, or Paddle account was provisioned. No packages were installed and no new projects were created.

**STOP — Phase 6.2B resolved-decisions document complete. Waiting for review before Phase 6.3 (identity/authentication implementation) begins.**
