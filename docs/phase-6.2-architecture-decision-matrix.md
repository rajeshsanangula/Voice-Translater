# Phase 6.2 — Architecture Decision Matrix (Review of Phase 6.1)

**Status: REVIEW/ANALYSIS ONLY. No production code, cloud resources, authentication libraries, database, or payment configuration were created or modified in this phase.** This document extracts every unresolved or technology-choice decision from `docs/phase-6-account-subscription-device-architecture.md` ("Phase 6.1") and presents each as a standalone decision for product-owner review. It does not resolve any of them. Terminology and entity names are kept identical to Phase 6.1.

## How this matrix was built

Every decision below is one of two kinds, and each entry says which:

- **"Technology choice" decisions** — Phase 6.1 §14 already compared multiple options and recommended one. These are restated here as formal decisions requiring product-owner sign-off, not silently treated as settled just because a recommendation exists.
- **"Unresolved" decisions** — Phase 6.1 §18 (or scattered inline "§UNRESOLVED" markers in §3/§4/§6/§11/§13) explicitly left these open, often with no options compared at all. Where Phase 6.1 did not provide enough detail to responsibly recommend an option, this document says so explicitly rather than inventing one.

Fourteen decisions are extracted in total. The "pay particular attention to" list in the Step 6.2 instruction is fully covered — several of those topics turned out to be **underspecified in Phase 6.1** (token/session architecture, usage metering method, account/profile registration method, customer-vs-admin roles), and this document flags those gaps explicitly rather than filling them in.

---

## D1 — Identity / Authentication Provider

1. **Decision**: Which identity provider issues and manages AUTRAXIS accounts, passwords, and tokens?
2. **Why it matters**: Every other security guarantee in Phase 6.1 (§10 — password hashing, token rotation, replay protection) sits on top of this choice. Getting it wrong is expensive to reverse once customer accounts exist.
3. **Options identified by Phase 6.1** (§14): Hand-rolled; Azure AD B2C; Auth0/Okta CIC; Firebase Auth.
4. **Recommended option (per Phase 6.1)**: Azure AD B2C, with Auth0 flagged as the fallback if B2C's custom-policy complexity proves too heavy.
5. **Advantages**: Single-vendor alignment with the already-in-use Azure Speech/Translator relationship; Microsoft-managed security patching; B2C supports custom claims (needed for device/subscription state on the token).
6. **Disadvantages**: B2C's custom-policy (XML-based "Identity Experience Framework") authoring has a real learning curve and is widely reported as harder to operate than Auth0's UI-driven configuration; smaller ecosystem of third-party tutorials/support than Auth0.
7. **Security implications**: All four options are professionally maintained platforms with strong track records; hand-rolled is explicitly the highest-risk option per Phase 6.1 and is not the recommendation. No option in Phase 6.1's comparison is flagged as insecure.
8. **Cost implications**: B2C is consumption-based (pay per authentication, generally low at small scale); Auth0/Okta shift to per-monthly-active-user pricing that can grow quickly; Firebase Auth is free at generous volume but ties the identity layer to Google's ecosystem specifically. Phase 6.1 does not model exact costs at any specific customer volume — no number is available to compare precisely.
9. **Implementation complexity**: Medium for B2C (policy configuration), low-medium for Auth0/Firebase, high for hand-rolled.
10. **Effect on Windows**: None of the four options change the desktop app's shape beyond adding one auth SDK dependency (§15's "new authentication SDK package").
11. **Effect on future Android/iOS**: All four options have first-party or well-supported mobile SDKs per Phase 6.1 §14's table; B2C and Auth0 both support the OIDC flows a native mobile client needs.
12. **Effect on subscription/device licensing**: The chosen provider must be able to carry custom claims (AccountId is a given; DeviceId/session-family claims are needed per §10) — Phase 6.1 asserts B2C supports this via custom policies but does not include a worked example, so this should be validated with a small spike before Phase 6.3 commits.
13. **Migration/lock-in implications**: Switching identity providers after real customer accounts exist is a significant migration (password re-hashing is generally impossible to carry over across providers without a forced reset flow) — this is one of the highest lock-in-risk decisions in the whole document and should be made deliberately, not by default.

---

## D2 — Backend Framework/Runtime

1. **Decision**: What server-side framework/language implements the new backend (`VTTranslate.Backend`)?
2. **Why it matters**: Determines team ramp-up speed, hiring pool, and how much of the existing codebase's patterns/tooling carry over.
3. **Options identified by Phase 6.1** (§14): ASP.NET Core Web API (.NET 8); Node/Express; Python/FastAPI; Go.
4. **Recommended option**: ASP.NET Core Web API (.NET 8).
5. **Advantages**: Same language/runtime as 100% of the existing repository (`VTTranslate.Core`, `VTTranslate.App`); the team's existing C#/xUnit testing discipline (visible throughout `VTTranslate.Core.Tests`, 399 tests) transfers directly; native, first-party integration with Azure Key Vault and Azure AD B2C if those are chosen (D1, D12).
6. **Disadvantages**: Narrows the backend hiring pool to .NET developers specifically, versus a more ecosystem-agnostic choice like Node; ASP.NET Core is a mature but sometimes perceived as a "heavier" framework than lightweight alternatives like FastAPI for a small initial API surface.
7. **Security implications**: No material security difference between the four options — all are production-grade, actively maintained frameworks. Security posture is driven far more by D1 (identity provider) and D10 (secret management) than by framework choice.
8. **Cost implications**: Hosting cost is comparable across all four on Azure App Service/Container Apps; not separately quantified in Phase 6.1.
9. **Implementation complexity**: Lowest for the team specifically, because of shared language/tooling with the existing codebase — Phase 6.1 explicitly frames this as "lowest ramp-up cost" rather than "objectively simplest."
10. **Effect on Windows**: None directly — this is a server-side-only decision.
11. **Effect on future Android/iOS**: None directly — the backend exposes the same REST API (§9) to any client regardless of the server's internal language.
12. **Effect on subscription/device licensing**: None directly — this is an implementation-language choice, not an architectural one; the same API contract in §9 is implementable in any of the four.
13. **Migration/lock-in implications**: Backend language choice is more reversible than D1/D3 (a REST API is a REST API regardless of what's behind it) but a full rewrite is still substantial, non-trivial work — not a decision to revisit casually once built.

---

## D3 — Database

1. **Decision**: What database stores accounts, subscriptions, entitlements, devices, sessions, and usage records (§8's schema)?
2. **Why it matters**: This data is the system of record for who is entitled to use the product — correctness and availability requirements are high.
3. **Options identified by Phase 6.1** (§14): Azure SQL Database; PostgreSQL (Azure Database for PostgreSQL); Cosmos DB/NoSQL.
4. **Recommended option**: Azure SQL Database, with PostgreSQL flagged as the "cost-conscious alternative."
5. **Advantages**: Phase 6.1's §8 schema is explicitly relational (foreign keys, joins for entitlement checks) — a relational database is the natural fit, not a forced choice; Azure SQL integrates cleanly with EF Core, which the C#-based backend (D2) would use directly; single-vendor operational alignment with D1/D2/D10 if those also land on Azure.
6. **Disadvantages**: Azure SQL licensing/pricing can be less transparent than PostgreSQL's at small scale; some lock-in to SQL Server's specific dialect/tooling versus the more portable PostgreSQL.
7. **Security implications**: No material difference — both Azure SQL and Azure Database for PostgreSQL support encryption at rest/in transit, private networking, and integrate with Key Vault-based credential rotation. Phase 6.1 explicitly rules out Cosmos DB/NoSQL as a poor fit for this relational data shape, which itself has a security-adjacent benefit: relational integrity constraints (foreign keys) make it structurally harder to end up with orphaned/inconsistent entitlement records than a schemaless store would.
8. **Cost implications**: Phase 6.1 flags PostgreSQL as "slightly cheaper at small scale" without a specific number — no concrete cost comparison is available from the document.
9. **Implementation complexity**: Comparable between Azure SQL and PostgreSQL given EF Core supports both well; Cosmos DB is explicitly not recommended and would require reshaping the entire §8 schema away from its natural relational form, which Phase 6.1 argues adds complexity without benefit.
10. **Effect on Windows**: None directly — server-side-only decision.
11. **Effect on future Android/iOS**: None directly.
12. **Effect on subscription/device licensing**: The schema in §8 (`Subscription`, `Entitlement`, `Device`, `UsageRecord` with foreign keys to `Account`) is designed relationally regardless of which relational engine is chosen — this decision is Azure SQL vs. PostgreSQL specifically, not relational vs. non-relational (that sub-decision is effectively already made by Phase 6.1's schema design, and is not treated here as still open).
13. **Migration/lock-in implications**: Relational-to-relational migration (Azure SQL ↔ PostgreSQL) is a well-trodden, moderate-effort path if needed later; migrating away from a relational model entirely (e.g., to Cosmos DB) would require a schema redesign and is not recommended by Phase 6.1 for this data shape.

---

## D4 — Subscription/Payment Provider

1. **Decision**: Which billing platform processes payments and drives subscription state?
2. **Why it matters**: Directly handles money and PCI-sensitive data; also determines how much dunning/grace-period logic (§4) the team must build versus get for free.
3. **Options identified by Phase 6.1** (§14): Stripe Billing; Paddle/Chargebee (merchant-of-record); roll-your-own.
4. **Recommended option**: Stripe Billing by default, with the explicit caveat (also captured as its own unresolved decision, D14) that the merchant-of-record question is a separate business/legal decision.
5. **Advantages**: Industry-standard; webhook model maps directly onto §9's `/subscription/webhook` endpoint; substantial built-in dunning/grace-period tooling (relevant to §4's Grace Period state and D6 below).
6. **Disadvantages**: Stripe is not a merchant of record — AUTRAXIS itself remains responsible for sales tax/VAT compliance across jurisdictions unless a merchant-of-record alternative (Paddle/Chargebee) is chosen instead; Stripe does not natively unify with mobile app-store purchases (a risk already flagged in Phase 6.1 §16/§7, restated here as it directly affects this decision's completeness).
7. **Security implications**: All three commercial options (Stripe/Paddle/Chargebee) remove PCI scope from AUTRAXIS's own systems, which is the main security benefit over "roll-your-own," which Phase 6.1 explicitly does not recommend due to PCI compliance burden and reinventing a solved problem.
8. **Cost implications**: Stripe/Paddle/Chargebee all take a percentage-based transaction fee; merchant-of-record options (Paddle/Chargebee) typically charge a higher percentage in exchange for absorbing tax compliance. Phase 6.1 does not specify exact fee percentages or model them against any placeholder pricing (correctly, since §17/§18 also forbid inventing pricing).
9. **Implementation complexity**: Stripe integration is well-documented and moderate effort; merchant-of-record platforms add their own onboarding/KYC requirements on top.
10. **Effect on Windows**: The desktop app's only touchpoint is `/subscription/checkout` returning a `checkoutUrl` (§9) — the actual payment UI is a browser redirect in all three options, so this decision has minimal direct effect on desktop app code.
11. **Effect on future Android/iOS**: Significant — see D14 (merchant-of-record/tax decision) and the standing risk (Phase 6.1 §16) that mobile in-app-purchase reconciliation is a separate, unsolved integration regardless of which web billing provider is picked here.
12. **Effect on subscription/device licensing**: The chosen provider's webhook is the trigger for `Subscription.Status` transitions (§4) — webhook reliability/signature verification (§10) is load-bearing for correctness of entitlement enforcement, not just billing.
13. **Migration/lock-in implications**: Migrating billing providers after real subscribers exist is disruptive (re-establishing payment methods, proration handling) — a high-friction decision to revisit, similar in weight to D1.

---

## D5 — Entitlement Model

1. **Decision**: Is the proposed `Plan` → `Entitlement` → `UsageRecord` three-tier model (§4, §8) the right shape, or should a different entitlement model be used?
2. **Why it matters**: This model determines how flexible the system is for future plan types (e.g., add-ons, per-seat business plans, feature flags unrelated to usage limits).
3. **Options identified by Phase 6.1**: **None — Phase 6.1 presents exactly one entitlement model and does not compare it against alternatives.** This is stated explicitly rather than inventing a comparison Phase 6.1 doesn't contain.
4. **Recommended option**: Not able to recommend an alternative from Phase 6.1's content alone, since none is presented. The existing proposal (key/value `Entitlement` rows keyed by `PlanId`, e.g. `MaxActiveDevices`, `MaxMinutesPerMonth`, `AllowedLanguagePairs`) is a conventional and reasonable default, but confirming it as final requires product input on whether more complex entitlement types (e.g., add-on purchases independent of the base plan) are anticipated.
5. **Advantages** (of the proposed model, as designed): Simple, extensible key/value shape means adding a new entitlement type later does not require a schema migration; centralizes all limit-checking logic at one gate (the `entitlements/provider-token` endpoint, §5/§9), which Phase 6.1 explicitly calls out as a deliberate security/simplicity choice.
6. **Disadvantages**: Key/value entitlements lose some type safety (a `Value` column presumably stored as a string/variant needs careful validation); no support for time-limited add-ons or per-seat models without further design.
7. **Security implications**: Centralizing enforcement at one endpoint (§5) is a security positive, independent of which entitlement model underlies it.
8. **Cost implications**: Not addressed in Phase 6.1 — no cost difference is identified between entitlement model shapes.
9. **Implementation complexity**: The proposed model is low-complexity to implement; Phase 6.1 does not describe what a more complex alternative would cost to build, so no comparison is possible.
10. **Effect on Windows**: The client only ever calls `/entitlements` to read current limits (§9) — the desktop app is insulated from entitlement-model internals either way.
11. **Effect on future Android/iOS**: Same insulation applies — mobile clients would consume the same `/entitlements` read surface regardless of internal model.
12. **Effect on subscription/device licensing**: Directly foundational — `MaxActiveDevices` (D6) and usage limits (D9) are both expressed as entitlements in this model.
13. **Migration/lock-in implications**: Low lock-in — a key/value entitlement table can be extended or partially migrated to a more structured model later without necessarily breaking the `/entitlements` API contract, since that endpoint already returns a flattened view.

---

## D6 — Device Licensing Model (Pooled vs. Per-Platform Limits)

1. **Decision**: Does `MaxActiveDevices` apply as one pooled limit across all platforms (Windows/Android/iOS), or per-platform sub-limits?
2. **Why it matters**: Directly shapes customer experience once mobile ships — e.g., "2 devices total" vs. "2 Windows + 2 mobile."
3. **Options identified by Phase 6.1** (§6, §18 item 4): Pooled across platforms; per-platform sub-limits. Phase 6.1 states this explicitly as unresolved and does not recommend one.
4. **Recommended option**: **None — Phase 6.1 explicitly defers this and provides no basis to recommend one over the other; this document does not silently pick one either.**
5. **Advantages/Disadvantages**: Not compared in Phase 6.1 beyond noting both are possible under the same `Device` entity shape (§8) — no additional detail is available to expand on trade-offs without inventing product reasoning Phase 6.1 doesn't contain.
6. **Security implications**: None identified — this is a business-rule decision layered on top of the same `Device.Status`/`AccountId` enforcement mechanism (§6) regardless of which limit style is chosen.
7. **Cost implications**: Not addressed.
8. **Implementation complexity**: Per-platform sub-limits require one additional field/check (`Platform`-scoped counting) versus a single pooled count — a minor implementation delta, not a major one, per the schema already in §8.
9. **Effect on Windows**: Windows is the only platform live today, so this decision has no observable effect until mobile ships — but the `/devices/register` endpoint's rejection logic (§9) needs to know which rule to enforce before mobile launches, so the decision should be made before Phase 6.10, not after.
10. **Effect on future Android/iOS**: Directly determines mobile device-limit UX at launch.
11. **Effect on subscription/device licensing**: This decision IS a device-licensing decision.
12. **Migration/lock-in implications**: Low — switching from pooled to per-platform (or vice versa) post-launch is a business-rule change, not a schema migration, since `Device.Platform` is already a proposed column (§8).

---

## D7 — Trial Structure (Time-Boxed vs. Usage-Boxed)

1. **Decision**: Is the free trial defined by elapsed calendar time, translated-minutes consumed, or both?
2. **Why it matters**: Directly shapes conversion economics and how "trial abuse" (e.g., creating repeat accounts) is even measurable.
3. **Options identified by Phase 6.1** (§4, §18 item 1): Time-boxed (placeholder `{{TRIAL_DAYS}}`); usage-boxed (placeholder `{{TRIAL_MINUTES}}`); or both. Explicitly flagged as "do not pick one without a product decision."
4. **Recommended option**: **None — Phase 6.1 deliberately does not recommend one**, and this document does not resolve it either.
5. **Advantages/Disadvantages**: Not compared in Phase 6.1 — flagged only as a placeholder decision.
6. **Security implications**: A usage-boxed or hybrid trial is somewhat more resistant to "leave the trial running idle forever" abuse than a pure time-box, but Phase 6.1 does not make this argument itself — noted here only as a general SaaS consideration, not as something Phase 6.1 asserts.
7. **Cost implications**: A usage-boxed trial bounds AUTRAXIS's own Azure/provider cost exposure per trial user more predictably than a pure time-box (a trial user could otherwise translate continuously for the full trial period) — again, a general consideration not explicitly stated in Phase 6.1, flagged here as relevant context for the product owner rather than a Phase 6.1 finding.
8. **Implementation complexity**: A hybrid (both time and usage caps) is marginally more complex than either alone, since both the `Subscription.CurrentPeriodEnd` and a `UsageRecord` rollup would need checking at the entitlement gate (§5) — both mechanisms already exist in the proposed data model (§8) regardless of which is chosen, so no new entity is required either way.
9. **Effect on Windows/Android/iOS/subscription/device licensing**: Uniform across platforms once the AUTRAXIS backend enforces it centrally (§5) — no platform-specific effect.
10. **Migration/lock-in implications**: Low — this is a configuration/business-rule value, not a structural commitment; the underlying `Plan`/`Entitlement`/`UsageRecord` model (§8) supports either without schema change.

---

## D8 — Grace Period (Existence and Length)

1. **Decision**: Should a `GracePeriod` subscription state (§4) exist at all, and if so, for how long (placeholder `{{GRACE_DAYS}}`)?
2. **Why it matters**: Affects churn from transient payment failures (a real card decline shouldn't instantly cut off a paying customer) versus revenue leakage from an overly generous grace window.
3. **Options identified by Phase 6.1** (§4, §18 item 2): Offer a grace period (standard SaaS dunning pattern) of some placeholder length; or skip it entirely (go straight from a failed payment to `Expired`).
4. **Recommended option**: Phase 6.1 describes grace periods as "a standard SaaS dunning pattern" and includes the state in its core subscription lifecycle diagram (§4), which is a soft lean toward including one — but Phase 6.1 does not commit to this or specify a length, so this document does not treat it as decided.
5. **Advantages**: Reduces involuntary churn from transient card failures (expired card, temporary bank decline) — a widely-recognized SaaS billing benefit, consistent with why Stripe Billing (D4) was recommended partly for its built-in dunning tooling.
6. **Disadvantages**: A customer who has genuinely stopped paying still receives service during the grace window — a real (if typically small and bounded) cost.
7. **Security implications**: None directly — this is a billing-state decision, not an authentication/authorization mechanism change; enforcement still routes through the same entitlement gate (§5) regardless of the grace window's length.
8. **Cost implications**: Directly proportional to grace-period length (longer grace = more free service to lapsed payers) — no specific number is modeled in Phase 6.1.
9. **Implementation complexity**: Low — `GracePeriod` is already a first-class state in the proposed `Subscription.Status` enum (§4/§8); only the length constant and whether to skip the state entirely need deciding.
10. **Effect on Windows**: The client would show whatever status text corresponds to this state (§13's subscription-status indicator) — no functional difference beyond what text/urgency is displayed.
11. **Effect on future Android/iOS**: Same, uniformly.
12. **Effect on subscription/device licensing**: Directly a subscription-lifecycle decision.
13. **Migration/lock-in implications**: Very low — a configuration value change, reversible at any time without a data migration.

---

## D9 — Usage Metering Method

1. **Decision**: How is translation usage actually measured for entitlement-limit enforcement — client-self-reported, server-derived from provider-token issuance/duration, or reconciled from the provider's own usage logs?
2. **Why it matters**: `UsageRecord`/`/usage/report` (§8, §9) is described as client-reported ("the client reports consumption at session end / periodic checkpoints"), but Phase 6.1 itself flags that "a client-only report is not trustworthy for enforcement" (§9) — meaning the document identifies a problem with its own proposed mechanism without fully resolving it.
3. **Options identified by Phase 6.1**: Client self-report via `/usage/report` (proposed, for the display/summary use case in `/usage/summary`); server-side enforcement via the `entitlements/provider-token` mint gate (proposed, for the enforcement use case). **Phase 6.1 does not describe a third, more precise method** (e.g., deriving actual consumed seconds from Azure's own usage/billing API, or from provider-token active-duration tracking on the backend) as an explicit alternative — this document flags that gap rather than inventing that mechanism on Phase 6.1's behalf.
4. **Recommended option**: **Cannot be fully recommended from Phase 6.1's content** — the document itself only partially resolves this (enforcement is decoupled from the untrusted client report, which is sound, but the actual *metering precision* used for `/usage/summary` and for approaching-limit warnings is left as client-self-report, which Phase 6.1 has already flagged as not fully trustworthy). This is surfaced as a genuine gap for product/engineering follow-up, not silently treated as settled.
5. **Advantages** (of the as-described split): Enforcement integrity does not depend on the client being honest (§9's own reasoning) — this part is sound and should not be revisited.
6. **Disadvantages**: The customer-facing usage summary (§9's `/usage/summary`) could show inaccurate numbers if it relies solely on client self-reports that Phase 6.1 already distrusts for enforcement purposes — a customer-trust risk (showing "80% of your minutes used" that doesn't match reality) distinct from the security risk already addressed.
7. **Security implications**: The enforcement path (provider-token minting, §5) is not exposed to this weakness — only the informational/display path is. Worth stating precisely so this isn't misread as a security hole; it is a data-accuracy gap in a non-enforcement surface.
8. **Cost implications**: A more precise server-derived metering approach (e.g., tracking actual provider-token active duration server-side) would need additional backend bookkeeping not currently scoped in Phase 6.1 — no cost estimate is available since the mechanism itself isn't specified.
9. **Implementation complexity**: Unknown/unspecified — Phase 6.1 does not design the more precise alternative, so its complexity can't be assessed here.
10. **Effect on Windows**: The client would still need to call `/usage/report` at minimum for the current design to work at all; a more precise backend-derived approach would reduce (not eliminate) the client's reporting responsibility.
11. **Effect on future Android/iOS**: Same consideration applies uniformly once mobile exists.
12. **Effect on subscription/device licensing**: Usage limits are one of the `Entitlement` types (D5) — metering precision affects how reliably those limits are enforced and communicated, but not the core enforcement gate itself (which is already sound per §7 above).
13. **Migration/lock-in implications**: Low — refining the metering mechanism later is an internal implementation change behind the existing `/usage/*` API shape (§9), not a breaking change to any client contract.

---

## D10 — Provider-Secret Storage Architecture

1. **Decision**: Which secret-management service holds the real Azure Speech/Translator/Gemini keys that `ProviderConfiguration.SecretRef` (§8) points to?
2. **Why it matters**: This is the single component that makes the entire "provider secrets never touch the client" guarantee (§5, the phase's core security requirement) actually true in practice.
3. **Options identified by Phase 6.1**: Azure Key Vault; AWS Secrets Manager; "or equivalent" (§10 states this explicitly but, unlike D1–D4, **Phase 6.1 does not provide a dedicated comparison table for this specific choice** in §14 — it is named only in passing).
4. **Recommended option**: Azure Key Vault, by extrapolation from Phase 6.1's consistent single-vendor-Azure reasoning applied to D1/D2/D3 — but this document notes explicitly that **Phase 6.1 itself never directly argues this for the secret store specifically**, so this recommendation is this document's inference, not a restatement of an explicit Phase 6.1 conclusion.
5. **Advantages**: If D1 (Azure AD B2C), D2 (ASP.NET Core), and D3 (Azure SQL) all land on Azure, Key Vault continues the single-vendor operational simplicity theme Phase 6.1 argues for repeatedly elsewhere.
6. **Disadvantages**: If any of D1/D2/D3 land on a non-Azure option, the case for Key Vault specifically weakens and AWS Secrets Manager (or another provider matching whatever cloud the rest of the backend runs on) may be more consistent — this decision is coupled to D1/D2/D3 and should be finalized after those, not independently.
7. **Security implications**: Both Key Vault and Secrets Manager are purpose-built, audited secret stores with access-policy and rotation support — Phase 6.1's core requirement (§10: "never returned by any API, never logged") is achievable with either; the choice does not itself change the security guarantee, only the operational vendor.
8. **Cost implications**: Not modeled in Phase 6.1 — both services are low-cost at the secret volumes this system would need (a handful of provider keys, not per-customer secrets).
9. **Implementation complexity**: Comparable between the two named options; effectively zero incremental complexity if the backend is already on Azure (D1–D3), since Key Vault integration with ASP.NET Core is first-party tooling.
10. **Effect on Windows**: None — the desktop app never touches this store directly, by design (§5).
11. **Effect on future Android/iOS**: None, for the same reason.
12. **Effect on subscription/device licensing**: Indirect only — this store backs the provider-token minting step (§5) that D6/D9 enforcement ultimately depends on being reliable and available.
13. **Migration/lock-in implications**: Low-to-moderate — secret stores are generally migrated by re-provisioning secrets into the new store and updating `SecretRef` values, a contained operation that doesn't touch customer data (§8's `Account`/`Subscription`/`Device` tables are unaffected).

---

## D11 — Token/Session Architecture (JWT vs. Opaque Tokens)

1. **Decision**: Are access tokens self-contained signed JWTs, or opaque tokens validated against a server-side session store on every request?
2. **Why it matters**: Affects how revocation works (a JWT already issued is valid until it expires, even if the account is disabled in the meantime, unless additional revocation-list machinery is added; an opaque token can be invalidated instantly).
3. **Options identified by Phase 6.1**: JWTs; opaque tokens validated server-side. Phase 6.1 §10 states plainly: "signed JWTs (or opaque tokens validated server-side — **a technology choice, not re-litigated here**)." **This is Phase 6.1 explicitly declining to decide**, not a gap this document is inferring.
4. **Recommended option**: **None — Phase 6.1 explicitly defers this**, and this document does not resolve it either. It is called out here specifically because the Step 6.2 instruction asked for particular attention to "token/session architecture," and Phase 6.1's treatment of it is real but incomplete (it names the two options and consciously stops there).
5. **Advantages/Disadvantages**: Not compared in Phase 6.1 beyond the general tradeoff implied above (JWT: fewer round-trips, harder instant revocation; opaque: a lookup per request, trivial instant revocation) — this document states the well-known general tradeoff for context but does not attribute it to Phase 6.1, since Phase 6.1 does not itself walk through it.
6. **Security implications**: Material — the access token's short lifetime (§10, placeholder 15 minutes) partially compensates for JWT's harder-revocation property (a revoked account is locked out within one token lifetime even without a revocation list), which is likely *why* Phase 6.1 felt comfortable deferring this choice; that reasoning is this document's inference, not stated outright in Phase 6.1.
7. **Cost implications**: Opaque tokens imply a server-side lookup (e.g., a cache/database hit) on every authenticated request; JWTs avoid that at the cost of the harder-revocation tradeoff above. No cost modeling exists in Phase 6.1.
8. **Implementation complexity**: Whichever identity provider is chosen (D1) likely determines this by default — Azure AD B2C and Auth0 both issue JWTs natively; building a fully opaque-token scheme independent of the IdP would be additional custom work not currently scoped anywhere in Phase 6.1.
9. **Effect on Windows/Android/iOS**: Uniform — both approaches are consumed identically by any client as an opaque-looking bearer string in an `Authorization` header; the client does not need to know which scheme is in effect.
10. **Effect on subscription/device licensing**: The `entitlements/provider-token` endpoint (§5/§9) re-checks subscription/device state at mint time regardless of which access-token scheme is used, so this decision does not weaken the core enforcement gate either way.
11. **Migration/lock-in implications**: Effectively decided by D1 in practice (see point 8) — flagged as its own decision here only because Phase 6.1 explicitly named it as unresolved, not because it is likely to be independently deliberated.

---

## D12 — Account/Profile Registration Method (Password vs. Passwordless)

1. **Decision**: Does registration/login use a traditional password, a passwordless "magic link" flow, or both?
2. **Why it matters**: Affects the entire password-security surface (§10) — a passwordless design removes password-hashing/reset/complexity concerns (D13) entirely for that flow.
3. **Options identified by Phase 6.1**: §3 mentions "Email + password (or a placeholder-friendly 'email + magic link', see §14 for the tradeoff)" — **but §14 does not actually contain a magic-link-vs-password comparison**; this is a cross-reference in Phase 6.1 that does not resolve to real content. This document flags that gap explicitly rather than inventing the missing comparison.
4. **Recommended option**: **Cannot be recommended — Phase 6.1's own internal cross-reference for this decision points to a section that does not address it.** This should be treated as needing fresh analysis, not as "Phase 6.1 leans toward password" or any other reading.
5–13. **Advantages/Disadvantages/Security/Cost/Complexity/Platform effects/Migration**: **Not assessable from Phase 6.1's content** for the same reason — no comparison exists in the source document to draw from. Restating general industry tradeoffs here (passwordless removes password-breach risk but depends on email deliverability/latency for every login) would be this document inventing analysis Phase 6.1 doesn't contain, which the Step 6.2 instruction explicitly prohibits ("do not invent... if not enough information, explicitly state that"). Flagged for the product owner as a decision needing its own dedicated analysis before Phase 6.3.

---

## D13 — Password Policy Specifics

1. **Decision**: Minimum length/complexity rules for passwords (only relevant if D12 resolves to include a password option).
2. **Why it matters**: A UX/security balance — overly strict policies increase signup friction; overly weak ones increase breach risk (mitigated but not eliminated by strong hashing, §10).
3. **Options identified by Phase 6.1** (§18 item 6, §10): None enumerated — Phase 6.1 explicitly separates this from the security architecture itself ("a UX decision layered on top of the security architecture, not part of it") and does not propose specific rules.
4. **Recommended option**: **None — explicitly deferred by Phase 6.1 as a product/UX decision, not a security-architecture one.**
5. **Advantages/Disadvantages**: Not addressed in Phase 6.1.
6. **Security implications**: Bounded by the hashing algorithm choice (Argon2id or bcrypt, §10 — itself also left open as "not re-litigated here"), which matters more for breach resistance than the complexity policy itself, per Phase 6.1's own framing.
7. **Cost implications**: None identified.
8. **Implementation complexity**: Trivial regardless of the specific rules chosen — a client/server-side validation rule, not an architectural change.
9. **Effect on Windows/Android/iOS**: Uniform — the same rule would be enforced server-side (and mirrored client-side for UX) on any platform.
10. **Effect on subscription/device licensing**: None.
11. **Migration/lock-in implications**: None — a policy value, trivially changeable at any time, contingent on D12 being resolved first.

---

## D14 — Merchant-of-Record vs. Direct Billing (Tax/VAT Handling)

1. **Decision**: Does AUTRAXIS bill directly via Stripe (assuming direct tax/VAT compliance responsibility itself) or use a merchant-of-record platform (Paddle/Chargebee) that assumes that responsibility?
2. **Why it matters**: A legal/compliance obligation, not just a technical one — international sales tax/VAT compliance is materially different in effort and risk between the two models.
3. **Options identified by Phase 6.1** (§14, §18 item 10): Stripe (direct); Paddle/Chargebee (merchant-of-record). Phase 6.1 explicitly labels this "a legal/business decision outside this design's scope" and does not recommend one.
4. **Recommended option**: **None — Phase 6.1 explicitly declines to recommend, correctly identifying this as outside an architecture document's scope.** This document does not overstep that boundary either.
5. **Advantages/Disadvantages**: Direct billing (Stripe) keeps more revenue (lower effective fees) but requires AUTRAXIS to handle tax registration/remittance across every jurisdiction it sells into; merchant-of-record platforms handle that compliance burden in exchange for a higher fee — a business tradeoff, not a technical one, and Phase 6.1 does not weigh in further.
6. **Security implications**: None material — both models keep PCI scope off AUTRAXIS's own systems (already covered under D4).
7. **Cost implications**: Merchant-of-record fees are typically higher than Stripe's base rate specifically because they include tax handling — no exact figures are available from Phase 6.1 or should be assumed.
8. **Implementation complexity**: Comparable at the technical-integration level (both are webhook-driven, per D4); the complexity difference is almost entirely legal/operational (tax registration effort), not engineering effort.
9. **Effect on Windows/Android/iOS**: None directly — same `checkoutUrl` redirect pattern (§9) regardless of which model is chosen.
10. **Effect on subscription/device licensing**: None directly.
11. **Migration/lock-in implications**: Switching between direct and merchant-of-record billing after establishing tax registrations in multiple jurisdictions (if direct billing was chosen first) could itself become a significant undoing-of-compliance-work exercise — this should be decided with legal/finance input before Phase 6.5, not treated as easily reversible.

---

## Additional gaps identified during this review (not separately numbered as decisions, but flagged per the Step 6.2 instruction's "pay particular attention to" list)

- **Customer vs. admin roles**: Phase 6.1 mentions an "internal/admin diagnostic capability" (§13, replacing the customer-facing Azure Speech Provider card) and an admin-managed `ProviderConfiguration` entity (§8), but **does not define an actual role/permission model** (e.g., is there an `IsAdmin` flag on `Account`, a separate internal-staff identity system, or a completely separate admin application?). This is a real gap, not a decision Phase 6.1 leaves open with named options — there is nothing to build a decision-matrix row from. **Flagged for product/engineering to scope explicitly before Phase 6.8 or wherever the admin surface is first built**, since Phase 6.1 does not currently describe one.
- **Account/profile architecture beyond registration method**: the core profile fields (§3, §8: display name, locale, email) are reasonably well specified; no additional gap beyond D12's registration-method question was found here.

---

## Decisions Required From Product Owner

The following decisions genuinely require product-owner approval before the corresponding Phase 6.x sub-phase can proceed responsibly. Purely technical/reversible choices with no material business, legal, or customer-experience consequence are not repeated here even though they appear above.

1. **D1 — Identity provider** (Azure AD B2C vs. Auth0 vs. other): high switching cost once accounts exist; needs sign-off before Phase 6.3.
2. **D4 — Subscription/payment provider** (Stripe vs. Paddle/Chargebee vs. other): handles real money; needs sign-off before Phase 6.5.
3. **D6 — Device licensing model** (pooled vs. per-platform device limits): directly shapes customer-visible product behavior; needs sign-off before Phase 6.7 (and ideally before Phase 6.10, mobile identity foundation).
4. **D7 — Trial structure** (time-boxed / usage-boxed / hybrid, and the actual placeholder values): directly shapes conversion economics; needs sign-off before Phase 6.5/6.6.
5. **D8 — Grace period** (whether one exists, and its length): affects both customer experience and revenue leakage; needs sign-off before Phase 6.5.
6. **D9 — Usage metering method**: Phase 6.1 itself identifies an internal inconsistency (client-reported usage flagged as "not trustworthy" for the very summary feature that relies on it) — needs a product/engineering decision on acceptable accuracy for customer-facing usage display, before Phase 6.9.
7. **D12 — Registration method** (password vs. passwordless vs. both): Phase 6.1's own cross-reference for this is broken (points to a section with no relevant content) — needs fresh analysis and a decision before Phase 6.3, since it shapes the entire login/registration UI (§13) and password-security scope (D13).
8. **D14 — Merchant-of-record vs. direct billing**: a legal/tax-compliance decision Phase 6.1 explicitly and correctly declines to make; needs legal/finance input and sign-off before Phase 6.5.
9. **Customer vs. admin role model** (gap, not a Phase 6.1 decision with options): needs to be scoped and decided — currently nothing in Phase 6.1 describes how AUTRAXIS staff would actually operate `ProviderConfiguration` or any other admin function.

*Not included above because they are lower-stakes, more easily reversible, or purely technical with no distinct business/legal consequence*: D2 (backend framework), D3 (database engine specifically — the relational-vs-not question is already settled by the schema), D5 (entitlement model shape — extensible without breaking changes), D10 (secret store vendor — coupled to D1–D3, decide after), D11 (JWT vs. opaque tokens — effectively inherited from D1), D13 (password policy specifics — trivial to change later, contingent on D12).

---

## Confirmations

- **No production code was changed.** This phase produced only this review document; no file under `src/` was modified.
- **No cloud resources were created.** No Azure AD B2C tenant, database, Key Vault, or any other cloud service was provisioned.
- **No authentication or payment implementation was added.** No SDK, library, or configuration for identity, billing, or secrets management was introduced into the repository.

**STOP — Phase 6.2 decision matrix complete. Waiting for review before any of the "Decisions Required From Product Owner" are resolved or Phase 6.3 begins.**
