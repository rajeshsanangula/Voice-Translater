# Phase 25 / 25B — Production subscription provisioning and the approved trial

Baseline commit `ab2e6b65e7cd44e5090ccd6864b511e27f477e21`. Scope: how an authenticated customer obtains a first,
server-controlled entitlement in Production, and what that entitlement means. **This is entitlement provisioning only.
No payment is taken, no payment provider is integrated, and nothing here claims otherwise.**

## 1. Approved trial specification (Phase 25B — do not reinterpret)

| Item | Approved value | Where it lives |
|---|---|---|
| Duration | 14 days | plan entitlement `TrialDurationDays = 14` |
| Usage allowance | 10 hours = 36,000 seconds, **one allowance for the whole trial** | plan entitlement `TrialUsageLimitSeconds = 36000` |
| Devices | 1 | plan entitlement `MaxActiveDevices = 1` |
| Usage measure | existing server-derived session wall-clock seconds | `UsageRecord` (source `ServerDerived`) |
| Mid-session | a running session is never cut off | existing behaviour |
| After expiry | translation refused | `EntitlementService` + persisted `Expired` |
| After allowance used | no new session may start | `EntitlementService` |

The values are operator data on the trial plan (never compiled in, never sent by the customer).

## 2. Architecture (audit summary)

- **Identity → account:** Entra JWT → `AccountResolutionMiddleware` (the account id is never taken from a request).
- **Model:** `Plan` + `Entitlement` (key/value) + `Subscription`; PostgreSQL enforces at most one live subscription per account.
- **Enforcement:** `EntitlementService` (device authorized → live subscription → status/period → usage). No subscription ⇒ denied.
- **Billing:** only `IBillingProvider` with `NotImplementedBillingProvider` (throws); webhook/lifecycle mutate existing subscriptions only.
- **Gap that Phase 25 closed:** nothing created a plan or a first subscription outside the Development-only `/dev/seed-subscription`.

## 3. Trial lifecycle

1. Operator enables the trial (config) and creates the plan row (runbook below).
2. Customer signs in; the client registers its device (while no subscription exists the limit is the fail-closed default of 1).
3. `POST /subscription/trial` (no body). Server, in a transaction after locking the account row: account must be `Active`; if a live subscription exists it is reconciled first — an ended trial becomes `Expired` and is refused (`409 trial_already_used`), otherwise returned unchanged (`200`, `created:false`, period never extended); any earlier subscription ⇒ `409` (one free trial per account, ever); the plan must define positive duration / usage limit / device limit, else `503`.
   Creates `Trial`, `CurrentPeriodStart = UTC now`, `CurrentPeriodEnd = start + 14 days` (persisted; never recomputed) and an audit event.
4. Sessions start while `now ≤ CurrentPeriodEnd` and trial usage `< 36,000` s.
5. Trial ends (time) → `EntitlementService` denies immediately (`trial_expired`); the persisted `Trial → Expired` transition happens deterministically whenever the subscription is read/reconciled (`GET /subscription`, `POST /subscription/trial`) — **no background job**. Once `Expired` it is history (not live), so it cannot be restarted.
6. Allowance used → new sessions denied (`trial_usage_exhausted`).
7. Upgrade: not implemented (no payment). The client shows wording only.

## 4. Usage calculation (no schema change)

`UsageRecord` has no `SubscriptionId`. For a `Trial` the server sums **server-derived** records whose `RecordedAt >= subscription.CurrentPeriodStart`, across every UTC month bucket from the start month through the current month (`IUsageService.GetAuthoritativeUsageSecondsSinceAsync`). It therefore never resets at a month boundary; records before the trial start and client-reported hints are ignored. **`Active` subscriptions are unchanged** (calendar-month bucket, `UsageLimitSecondsPerPeriod`). The trial plan must **not** carry `UsageLimitSecondsPerPeriod`; My Account reads the trial allowance from `GET /usage → trial`, so no compatibility key is needed.

**Overshoot (documented, by design):** the allowance is checked only when a session starts, and usage is recorded when a session ends or lapses (60 s lease). A session started just under 36,000 s may therefore run past the allowance (bounded by that one session's length); several sessions started concurrently just under the limit are all allowed (they cannot see each other's not-yet-recorded usage). No running session is terminated.

## 5. Denial contract (machine-readable)

`POST /translation-sessions` → `403 { status, code }`:
`usage_limit_exceeded` + `trial_usage_exhausted`; `entitlement_denied` + `trial_expired`; (`trial_not_configured` if a trial plan lost its limit — fail closed). The client maps the codes to
*"Your trial has ended. Upgrade to Premium to continue using Voice-Translater."* / *"Your trial allowance has been used. Upgrade to Premium…"*. No upgrade URL or payment flow exists; configure that when billing exists. Non-trial denials are unchanged.

## 6. My Account

`GET /usage` keeps its monthly fields and adds `trial` (null when there is none):
`status, periodStart, periodEnd, usedSeconds, limitSeconds, remainingSeconds, ended, exhausted`. For a trial customer My Account shows *Trial · 14-day period*, *X / 10 hours used*, *remaining*, *trial ends/ended <date>* and the upgrade message when ended/exhausted; the monthly / paid-limit line is suppressed. (One pre-existing client bug fixed on the way: the backend's `usage_limit_exceeded` status was not mapped to the entitlement-denied category.)

## 7. Operator runbook

```sql
-- once, against the production database; the id is yours to choose
INSERT INTO plans ("Id","Name","PriceHandle","IsPubliclyPurchasable")
VALUES ('<PLAN-GUID>', 'Trial', NULL, false);
INSERT INTO entitlements ("Id","PlanId","Key","Value") VALUES
 (gen_random_uuid(), '<PLAN-GUID>', 'TrialDurationDays', '14'),
 (gen_random_uuid(), '<PLAN-GUID>', 'TrialUsageLimitSeconds', '36000'),
 (gen_random_uuid(), '<PLAN-GUID>', 'MaxActiveDevices', '1');
```
Then set `Subscriptions__Trial__Enabled=true` and `Subscriptions__Trial__PlanId=<PLAN-GUID>` (non-secret) and restart. The endpoint is not mapped unless both are set; a bare deployment cannot grant anything. No migration is required.

## 8. Security properties

No client input is read by the trial endpoint (hostile bodies/queries ignored — tested): a customer cannot choose plan, duration, quota, device limit, account, status, period, or billing values; cannot extend or restart a trial, reset usage, create a second trial, or provision another account. Plan changes/quota are operator data. Server-side authorization throughout; `/dev/seed-subscription` remains Development-only (a PostgreSQL-gated test asserts it is absent in Production). No secrets added.

## 9. Known limits

Plan row creation is an operator SQL step; multi-account trial abuse is limited only by one-trial-per-account plus the existing email-verified gate and rate limit; billing/upgrade, admin API and notifications are future work.
