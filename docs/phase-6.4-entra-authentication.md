# Phase 6.4 — Microsoft Entra External ID Authentication

## 1. Objective

Implement real authentication for the AUTRAXIS backend using Microsoft Entra
External ID, strictly behind the `IIdentityProvider` abstraction approved in
Phase 6.2B, and wire it to a backend-authoritative account-resolution and
role-authorization flow:

```
Microsoft Entra External ID → IIdentityProvider → AuthenticatedPrincipal
  → AUTRAXIS Account Resolution → Account Status → AUTRAXIS Role
  → AuthorizationService → protected application operations
```

Authentication answers "who authenticated?" (Entra's job). Authorization
answers "what is this AUTRAXIS account allowed to do?" (this backend's job).
An Entra claim, email address, device identifier, or client request must
never directly grant AUTRAXIS privileges. **IMPLEMENTED.**

## 2. Existing architecture before Phase 6.4

Phase 6.3 established the layered project structure
(Domain → Application → Infrastructure → Api), in-memory persistence,
`Account`/`Role` entities, a placeholder `IIdentityProvider`
(`NotImplementedIdentityProvider`) whose `AuthenticatedPrincipal` carried a
`Roles` list sourced from claims, and five anonymous `501` placeholder
routes. That placeholder identity provider and the claims-sourced role list
are superseded by this phase — see §8 for why.

## 3. Entra integration architecture

Standard, Microsoft-supported ASP.NET Core JWT bearer authentication
(`Microsoft.AspNetCore.Authentication.JwtBearer` v8.0.11) is configured in
`Program.cs` against `Identity:Authority` / `Identity:Audience`
configuration. This performs all cryptographic token verification —
signature, issuer, audience, expiration, not-before, and signing-key
rotation via standard OIDC metadata/JWKS — entirely inside framework code,
before any AUTRAXIS code runs. **IMPLEMENTED, TESTED.**

## 4. `IIdentityProvider` implementation

`EntraIdentityProvider` ([EntraIdentityProvider.cs](../src/VTTranslate.Backend.Infrastructure/Identity/EntraIdentityProvider.cs))
maps already-verified claims (`sub`/`oid`, `email`/`preferred_username`,
`email_verified`, `name`, `iat`, `exp`) into `AuthenticatedPrincipal`. It
never parses or cryptographically validates a token itself, never reads a
role/permission claim, and fails closed (returns `null`) if the stable
subject identifier or `iat`/`exp` are missing or malformed.
`ClaimsPrincipalMapper.ToClaimsDictionary()`
([ClaimsPrincipalMapper.cs](../src/VTTranslate.Backend.Infrastructure/Identity/ClaimsPrincipalMapper.cs))
is the only place `ClaimsPrincipal` is converted to the primitive dictionary
this interface expects. **IMPLEMENTED, TESTED** (13 unit tests in
`EntraIdentityProviderTests.cs`).

## 5. `AuthenticatedPrincipal`

Defined in Domain
([IIdentityProvider.cs](../src/VTTranslate.Backend.Domain/Abstractions/IIdentityProvider.cs))
as `(Provider, ExternalSubjectId, Email, EmailVerified, DisplayName,
IssuedAt, ExpiresAt)`. Deliberately carries no role, no `Account` reference,
and no device information. Contains zero ASP.NET, Entra SDK, or JWT types.
A reflection-based test (`NeverProducesARoleOrAnyAuthorizationInformation`)
asserts no property name contains "Role", so this cannot silently regress.
**IMPLEMENTED, TESTED.**

## 6. Account-resolution flow

`AccountResolutionService`
([IAccountResolutionService.cs](../src/VTTranslate.Backend.Application/Identity/IAccountResolutionService.cs))
looks up the AUTRAXIS `Account` by `(Provider, ExternalSubjectId)` — never
by email — and returns one of three explicit outcomes: `Resolved`,
`AccountNotFound`, or `AccountSuspended` (which also covers soft-deleted
accounts, via `Account.IsUsable`). No account is ever auto-provisioned; an
unknown identity is a controlled `AccountNotFound` result, not a side
effect. Two different external subjects sharing the same email resolve to
two different accounts, proving email is never the identity key.
**IMPLEMENTED, TESTED** (8 unit tests in `AccountResolutionServiceTests.cs`).

## 7. Account status handling

`AccountStatus` (`Active` / `Suspended`) lives on `Account`, is
backend-authoritative, and is never settable by a client or inferred from
an Entra claim. `Account.IsUsable` combines status with the pre-existing
soft-delete flag; both `AuthorizationService` and `AccountResolutionService`
treat any non-usable account as denied, regardless of its stored `Role`.
**IMPLEMENTED, TESTED.**

## 8. Role authority and mapping

The AUTRAXIS role (`Customer` / `Admin` / `SuperAdmin`) comes from exactly
one place: `Account.Role`, read from the resolved account inside
`AccountResolutionMiddleware` after account resolution succeeds — never
from any Entra token claim, request header, query parameter, or body field.
This is why `AuthenticatedPrincipal` (§5) carries no role at all: it
structurally removes "unknown role claim" and "missing role claim" as
attack surfaces rather than special-casing them. A JWT containing forged
`role`/`roles` claims is proven (integration test
`ClientSuppliedRoleClaimInsideJwt_CannotEscalatePrivileges`) to have no
effect on the account's actual authorized role. **IMPLEMENTED, TESTED.**

## 9. Authentication middleware

Pipeline order in `Program.cs`:
`app.UseAuthentication()` (real JwtBearer — establishes `HttpContext.User`
from a cryptographically verified token, or leaves the request
unauthenticated) → `app.UseAutraxisAccountResolution()`
(`AccountResolutionMiddleware`
([AccountResolutionMiddleware.cs](../src/VTTranslate.Backend.Api/Security/AccountResolutionMiddleware.cs)) —
maps claims, resolves the Account, stashes it in `HttpContext.Items`, or
fails closed with 401/403) → `app.UseAuthorization()` (ASP.NET's own
`RequireAuthorization()` policy, rejecting any still-unauthenticated request
with a uniform 401). **IMPLEMENTED, TESTED.**

## 10. Authorization boundary

Unauthenticated → 401 (`RequireAuthorization()`, one code path for every
route). Authenticated but no/suspended account → 403 from
`AccountResolutionMiddleware`. Authenticated, resolved, but insufficient
role for a specific endpoint → 403 from `RequireRoleEndpointFilter`
([RequireRoleEndpointFilter.cs](../src/VTTranslate.Backend.Api/Security/RequireRoleEndpointFilter.cs)),
which reads the `Account` middleware placed in `HttpContext.Items` and
calls `IAuthorizationService.HasAtLeastRole` — it does not use ASP.NET's
built-in claims-based `[Authorize(Roles=...)]`, because that would require
putting an AUTRAXIS role into an Entra claim, which this architecture
forbids. No response path returns 200 for an unauthorized request.
**IMPLEMENTED, TESTED.**

## 11. Device identity separation

No device licensing/authorization logic was implemented or changed in this
phase. `AuthenticatedPrincipal` and `Account` carry no device identifier,
hardware fingerprint, machine name, or Windows username, and nothing in the
resolution or authorization path accepts one as proof of identity. User
identity, device identity, subscription, and entitlement remain
conceptually and structurally distinct, matching Phase 6.3.
**NOT IMPLEMENTED (by design/scope) — conceptual separation preserved.**

## 12. Session separation

No changes were made to any session type. Identity-provider authentication
(this phase), the AUTRAXIS application session, translation session,
device authorization, subscription, and entitlement remain distinct
concepts; none were merged or redesigned. **NOT MODIFIED — no change was
justified.**

## 13. Configuration

`EntraIdentityOptions`
([EntraIdentityOptions.cs](../src/VTTranslate.Backend.Api/Security/EntraIdentityOptions.cs))
exposes exactly `Authority` and `Audience` under the `Identity:` section of
`appsettings.json` — both are non-secret (public issuer URL / application
ID), left empty in the committed file with an explanatory `_comment`.
Missing/empty configuration does not enable a fake authenticated state: the
real JwtBearer handler fails every authentication attempt closed until
valid configuration is supplied. No client secret, signing key, or token is
configured anywhere in this phase — none is needed, because the API only
validates tokens, it never acquires them. **IMPLEMENTED, TESTED.**

## 14. Security considerations

- Fail-closed by construction: missing/malformed claims, unknown accounts,
  suspended accounts, and unrecognized resolution outcomes all deny access
  rather than defaulting to allow.
- No custom cryptography: token validation is 100% standard
  `Microsoft.IdentityModel`/`JwtBearer`, with default `ValidateIssuer` /
  `ValidateAudience` / `ValidateLifetime` / `ValidateIssuerSigningKey`.
- `options.MapInboundClaims = false` is set explicitly so `EntraIdentityProvider`
  sees the standard OIDC claim names (`sub`, `iat`, etc.) rather than
  ASP.NET's legacy WIF claim-type remapping — a correctness fix, not a
  security relaxation (see [Program.cs](../src/VTTranslate.Backend.Api/Program.cs)).
- Logging is metadata-only (`OnAuthenticationFailed` logs only the
  exception type name; middleware logs only outcome names) — never tokens,
  claims, secrets, or authorization headers.
- Error responses return a short generic `status` string only — no account
  existence details, internal IDs, repository details, or stack traces.
**IMPLEMENTED, TESTED.**

## 15. Test strategy

- Deterministic tests only: no live Entra calls, no real credentials.
- Unit-level: `EntraIdentityProviderTests` (claims → principal mapping, all
  fail-closed paths), `AccountResolutionServiceTests` (resolution outcomes),
  `AuthorizationServiceTests` (role hierarchy + suspended/soft-deleted
  denial).
- Integration-level: `AuthenticationIntegrationTests` uses a
  `WebApplicationFactory<Program>` subclass that overrides `JwtBearerOptions`
  with a static symmetric (HMAC-SHA256) signing key and fixed test
  issuer/audience (`Authority`/`MetadataAddress` = null) — this exercises
  the real Microsoft.IdentityModel validation pipeline with zero network
  calls, covering the full numbered list from the instruction: missing,
  malformed, expired, invalid-issuer, invalid-audience, invalid-signature,
  and valid tokens; unknown/suspended/resolved accounts; role escalation
  attempts via forged JWT `role`/`roles` claims; 401-vs-403 boundaries.
- `ApiSecurityBoundaryTests` proves every placeholder route requires
  authentication end to end (anonymous → 401, never a leaked 501 body).
- `ConfigurationSecretSafetyTests` asserts the committed `Identity` config
  section contains only `_comment`/`Authority`/`Audience` with short/empty
  values. **IMPLEMENTED, TESTED.**

## 16. Test results

- Backend test suite (`VTTranslate.Backend.Tests`): **99/99 passing** (0
  failed, 0 skipped).
- Existing Windows/Core suite (`VTTranslate.Core.Tests`): **399/399
  passing**, unchanged from the Phase 6.3 baseline.
- Full solution build: **0 errors, 0 warnings**, 9 projects.

## 17. Deferred work

Explicitly not implemented in this phase (all listed in the governing
instruction's stop list): production database persistence, Paddle/billing
integration, subscription checkout, customer-facing UI (login/registration/
profile/subscription pages), mobile (Android/iOS) authentication, full
device licensing/authorization, usage-based billing/metering beyond the
existing Phase 6.3 abstractions, provider credential/token gateway.

## 18. Known limitations

- `Authority`/`Audience` are empty placeholders in committed configuration;
  a real Entra External ID tenant must be provisioned and configured
  per-environment before this authenticates real users.
- Account provisioning (how a new AUTRAXIS `Account` first gets created for
  a newly-registered Entra identity) is out of scope for this phase —
  `AccountResolutionService` returns a controlled `AccountNotFound` result
  for any identity with no existing account, by design; the provisioning
  flow itself is future work.
- No rate limiting or account-enumeration-timing mitigation was added
  beyond returning a generic 403 for both "not found" and "suspended".

## 19. Phase 6.5 prerequisites

None blocking — Phase 6.4 is self-contained and does not require any
Phase 6.5 work to be considered complete. Phase 6.5 (whatever scope is
approved) should build on: the `IIdentityProvider`/`AuthenticatedPrincipal`
contract (§4–5, stable), the `AccountResolutionService` contract (§6,
stable), and the `IAuthorizationService` contract (§8, stable) — none of
these are expected to need breaking changes for foreseeable future work
(database persistence, billing, customer UI, mobile).
