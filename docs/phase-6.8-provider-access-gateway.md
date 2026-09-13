# Phase 6.8 — Provider Access Gateway

**Status: IMPLEMENTED.** Introduces a secure, backend-mediated provider-access boundary
so the customer application never receives a long-lived Azure Speech/Translator key.
Reuses Phase 6.3/6.6 (`EntitlementService`) and Phase 6.7 (`DeviceRegistrationService`)
authorities unchanged — no subscription/device logic was duplicated or reinvented.

## 1. Objective

The customer application authenticates to AUTRAXIS and requests provider access; the
backend authorizes that request using the existing authenticated identity, account,
subscription, entitlement, and device state, and — only then — issues a short-lived,
provider-scoped credential. Provider master credentials never leave the backend.

## 2. Architecture

```
Domain:       Provider, ProviderCapability (enums)
              ProviderAccessGrant (entity — metadata only, never the credential itself)
              IProviderAccessRepository, IProviderCredentialIssuer, IssuedProviderCredential (abstractions)

Application:  IProviderAccessGateway / ProviderAccessGateway
              — orchestrates: device authorization (Phase 6.7, reused) ->
                entitlement/subscription/usage authority (Phase 6.3/6.6, reused) ->
                provider/capability support -> issuance -> atomic persist+audit.

Infrastructure: AzureProviderCredentialIssuer (one instance per Provider)
              EfProviderAccessRepository / InMemoryProviderAccessRepository

Api:          POST /provider-access
```

Domain and Application depend only on `IProviderCredentialIssuer` — no Azure SDK type,
no HTTP detail, no master credential ever appears above the Infrastructure layer.

## 3. Provider mechanism — why Azure Speech/Translator, not Gemini

Azure Speech and Azure Translator both support a standard delegated-token mechanism:
`POST https://{region}.api.cognitive.microsoft.com/sts/v1.0/issueToken`, authenticated
with the long-lived subscription key, returning a bearer JWT valid for exactly 10
minutes. This is the "secure short-lived scoped credential" this phase's instruction
requires — a real, Microsoft-documented mechanism, not something invented here.

**Gemini is deliberately excluded** from this gateway. Gemini API keys have no
equivalent STS/delegated-token mechanism — there is no safe way to hand a customer
application a short-lived, scoped Gemini credential. Per the instruction ("if the
provider cannot safely support direct customer-side short-lived credentials... use a
backend-mediated design instead") and the explicit Phase 6.8 stop-list ("no Gemini
naturalization"), Gemini is out of scope for this gateway entirely — it remains an
isolated R&D track (Step 5.14 series), never promoted to production, never brokered.

## 4. Authorization chain (implemented exactly as specified)

```
JWT authentication (Phase 6.4, unchanged)
  -> AccountResolutionMiddleware (Phase 6.4, unchanged) -> usable Account
  -> IDeviceRegistrationService.IsDeviceAuthorizedAsync (Phase 6.7, reused unchanged)
  -> IEntitlementService.CanStartTranslationSessionAsync (Phase 6.3/6.6, reused unchanged)
  -> provider/capability support (new, this phase)
  -> IProviderCredentialIssuer.IssueAsync (new, this phase)
  -> atomic grant + audit persistence (IUnitOfWork, Phase 6.6 pattern reused)
  -> response
```

Every step fails closed: an unauthorized/nonexistent/revoked device, a denied
entitlement, an unsupported provider/capability, or a provider-side issuance failure
all deny access — none can be bypassed by any client-supplied value, since `accountId`
is never accepted from the client at any point (derived exclusively from
`AccountResolutionMiddleware`, exactly like every other Phase 6.6/6.7 endpoint).

## 5. Account isolation and device authorization

Reused verbatim: `IsDeviceAuthorizedAsync(accountId, deviceId)` already returns `false`
identically for "device doesn't exist", "device belongs to another account", and
"device is revoked" — this gateway inherits that non-enumerable failure mode without
any new code. No second device-licensing system was introduced.

## 6. Usage model — deliberately conservative

Per the explicit instruction ("do not automatically count the entire provider
credential lifetime as consumed usage unless explicitly justified"), **this phase does
NOT record usage at issuance time**. `EntitlementService`'s existing usage-limit check
(reading already-recorded `ServerDerived` `UsageRecord` rows) is used as a pre-issuance
gate, unchanged — but actual usage-duration recording tied to a specific grant's
issuance/expiry/renewal cycle is explicitly **deferred** (this was already flagged as
not-yet-implemented in Phase 6.3 §8, pending exactly this kind of real-time-session
integration point — Phase 6.8 establishes the issuance boundary but does not yet wire
usage-recording to it). No check-then-act usage race was introduced because no usage
write happens here at all yet.

## 7. Concurrency

No new count-based limit was introduced by this phase (unlike Phase 6.7's device pool),
so there is no new multi-row race to close. The one genuine atomicity requirement —
that a granted access's `ProviderAccessGrant` row and its `AuditEvent` row commit
together or not at all — is handled via Phase 6.6's existing `IUnitOfWork` transaction
abstraction, reused unchanged (not a new locking primitive). Proven by a real-PostgreSQL
test that forces a mid-transaction failure and confirms the grant row is rolled back.

## 8. Credential lifetime and non-revocability — explicit, honest limitation

**Two distinct authorization layers exist here, and they must never be conflated:**

- **AUTRAXIS authorization** — the backend-authoritative decision made fresh on every
  `/provider-access` call (device authorization, entitlement/subscription/usage state).
  This is instantly and fully revocable: revoking a device, suspending an account, or a
  subscription lapsing takes effect on the very next `/provider-access` call, since
  there is no separate lightweight "renew" path — every call re-runs the entire
  authorization chain from scratch (§4).
- **Azure bearer-token authorization** — the STS-issued JWT itself, which Azure's own
  Speech/Translator REST and SDK surfaces validate independently, entirely outside this
  backend's control once issued. **This token is NOT revocable** — Azure exposes no
  provider-side revoke call for it — and it is **not intrinsically scoped to a specific
  AUTRAXIS account or device**; Azure only knows it as "a token this subscription key
  issued," with no AUTRAXIS identity embedded in it.

The practical consequence: revoking AUTRAXIS authorization (layer 1) stops the *next*
credential from ever being issued, but cannot reach back and invalidate an
*already-issued* Azure token (layer 2) before its own natural 10-minute expiry. This
bounded exposure window — never longer than 10 minutes, and only for a device/account
that WAS legitimately authorized at the moment of issuance — is the same
"short-lived-token offline continuity" principle Phase 6.2B §12 already established for
provider tokens generally. This is a real, disclosed limitation, not a false claim that
Azure's token is customer-revocable or AUTRAXIS-scoped.

## 9. Audit

Every issuance (`ProviderAccessIssued`) and every denial
(`ProviderAccessDenied{Outcome}`) produces an `AuditEvent`, safely identifying
`AccountId`, `DeviceId`, `Provider`, `Capability`, and (on success) `CorrelationId`
and `ExpiresAt` — never the issued token, never a provider secret, never the
`Authorization` header or JWT.

## 10. Configuration

`ProviderCredentials:{AzureSpeech,AzureTranslator}:{SubscriptionKey,Region}` —
`SubscriptionKey` is a secret, always empty in committed config; `Region` is not.
`ProviderAccess:CredentialLifetimeSeconds` is non-secret. An unconfigured provider
(empty key or region) is simply never registered — `/provider-access` reports
`unsupported_provider` for it, never a fabricated credential or a silent fallback to a
long-lived key.

## 11. API

`POST /provider-access` — `{ deviceId, provider, capability }`. Account derived
exclusively from `AccountResolutionMiddleware`. Responses: `200` (credential + metadata)
on success; `400` for malformed input or unsupported provider/capability; `403` for
`device_not_authorized`/`entitlement_denied`/`usage_denied`; `503` for
`provider_unavailable`; `401`/existing `403 account_not_found`/`account_suspended` from
the existing authentication/account-resolution pipeline, unchanged.

## 12. Deferred / out of scope

Real usage-duration recording tied to grant issuance/expiry (§6); Gemini brokering (not
possible safely, §3); a customer-facing renewal-specific endpoint (unnecessary — every
request already re-runs full authorization, §4); mobile client integration; desktop
client migration to actually call this endpoint (out of scope for this phase — the
desktop app's existing Azure key usage is untouched, per the "existing translation/audio
implementation" protection).
