# Phase 6.3 Review Gate — Backend Foundation Security & Architecture Audit

**Scope: audit only.** No Entra integration, no login/registration UI, no production database, no Paddle, and no change to the existing translation/audio pipeline were made in this phase. Two categories of change WERE made, both explicitly permitted by the review-gate instructions: (a) focused tests closing gaps this audit found, and (b) two documentation/config accuracy fixes (a false doc-comment, and template leftovers already cleaned in Phase 6.3 — reconfirmed here, not re-done).

---

## 1. Executive verdict

**Phase 6.4 (real Microsoft Entra External ID behind `IIdentityProvider`) is APPROVED to begin, with one required addition to be made AT THE START of Phase 6.4 (not before) and several documented implementation-guidance items that Phase 6.4 must honor.** No BLOCKED findings were identified anywhere in the current Phase 6.3 code. Layering, role/authorization separation, entitlement fail-closed behavior, and usage-source separation are all sound and test-proven. The one gap worth calling "required before Phase 6.4 completes" (not before it starts) is account-status support (§13) — Phase 6.4 cannot correctly reject a suspended/disabled account without it, but building it now would be adding product functionality outside this audit's charter, so it is correctly sequenced as the first thing Phase 6.4 itself should add, before wiring real Entra calls to endpoints.

---

## 2. Layering audit — **PASS**

Verified directly from each `.csproj`'s `<ProjectReference>` list (not inferred):

```
VTTranslate.Backend.Domain          → (no project references at all)
VTTranslate.Backend.Application     → Domain
VTTranslate.Backend.Infrastructure  → Domain, Application
VTTranslate.Backend.Api             → Domain, Application, Infrastructure
```

- **Domain has no infrastructure/API dependency** — confirmed (zero `<ProjectReference>` entries in `VTTranslate.Backend.Domain.csproj`).
- **Domain has no Microsoft Entra dependency** — confirmed; Domain also has zero `<PackageReference>` entries of any kind (pure C#, no NuGet packages at all).
- **Domain has no billing-provider dependency** — confirmed, same reason.
- **Application does not directly depend on Entra SDKs** — confirmed; `VTTranslate.Backend.Application.csproj` has zero `<PackageReference>` entries.
- **Application does not directly depend on Paddle** — confirmed, same reason.
- **API does not contain business logic that belongs in Application** — confirmed by reading `Program.cs` in full (90 lines): it contains only DI registration and route mapping (a single shared `NotImplementedPlaceholder` handler, no conditional business logic, no entitlement/device/usage decision-making inline).
- **Infrastructure contains external-system implementations** — confirmed: `NotImplementedIdentityProvider`, `NotImplementedBillingProvider` (both deliberately throwing), `InMemory*Repository` (dev/test-only persistence), `SystemClock`. Nothing that belongs in Domain or Application was found misplaced here.
- **Provider abstractions remain replaceable** — confirmed: swapping `NotImplementedIdentityProvider` for a real `EntraExternalIdIdentityProvider` requires touching exactly one DI registration line in `Program.cs` and adding one new class in Infrastructure; no other file needs to change, because Domain/Application only ever reference `IIdentityProvider`.

No violation found in any direction.

---

## 3. Identity abstraction audit

Reviewed `IIdentityProvider`, `AuthenticatedPrincipal`, `NotImplementedIdentityProvider`, and every call site (there are none yet outside DI registration and tests — correctly, since no endpoint uses it in this phase).

| Requirement | Assessment |
|---|---|
| Authenticated user identity | **PASS** — `AuthenticatedPrincipal` record exists with exactly this purpose. |
| Stable external identity subject | **PASS** — `ExternalSubjectId` field, `string`, provider-agnostic. |
| AUTRAXIS internal Account ID | **PASS WITH MINOR CHANGE (guidance, not a code defect)** — `AuthenticatedPrincipal` intentionally does NOT carry an internal `Account.Id` (only `ExternalSubjectId`); resolving external identity → internal `Account` is, by design, a separate application-layer lookup via `IAccountRepository.FindByExternalIdentityAsync`. This is architecturally correct (keeps the identity boundary from needing to know about `Account` at all) but it means **Phase 6.4 must build a small "resolve-or-provision-account" step** (not yet built) as the first real consumer of `IIdentityProvider` — flagged in §13 as a required Phase 6.4 deliverable, not a Phase 6.3 defect. |
| Login/session validation | **DEFERRED, correctly** — `ValidateAccessTokenAsync` is the validation surface; login/registration are explicitly out of this interface's scope by design (Phase 6.3's own instruction excluded them), and remain so. |
| Token expiration | **PASS** — `AuthenticatedPrincipal.ExpiresAt` exists; a real implementation is expected to reject an expired token before ever constructing a principal (i.e., expiry is checked BY `ValidateAccessTokenAsync`, and `ExpiresAt` on the returned principal is informational/for logging, not something callers should re-check). This should be stated explicitly in the real implementation's own doc comment when Phase 6.4 writes it — noted as implementation guidance. |
| Issuer validation | **PASS (by design, not by visible field)** — issuer/audience validation is correctly treated as an internal responsibility of whichever `IIdentityProvider` implementation exists, not something the interface needs to expose as data. Exposing raw issuer/audience as fields would leak Entra-specific concepts into the domain, which is exactly what this abstraction exists to prevent. No change needed. |
| Audience validation | Same as above — **PASS**. |
| Role claims | **PASS** — `Roles` field, `IReadOnlyList<Role>`, populated only from a verified claim (by contract/doc comment). |
| Account lookup | **PASS WITH MINOR CHANGE** — same finding as "internal Account ID" above; the lookup mechanism (`IAccountRepository.FindByExternalIdentityAsync`) already exists and is implemented/tested, but nothing yet calls it in sequence with `ValidateAccessTokenAsync`. This is the missing "glue," not a missing primitive. |
| Disabled/suspended accounts | **BLOCKED for Phase 6.4 COMPLETION (not for Phase 6.4 START)** — see §13; `Account` has no status/suspended concept today, only `DeletionRequestedAt`-derived soft-delete. Phase 6.4 cannot safely finish wiring real authentication without a way to reject a suspended account. Not fixed in this review (would be new product functionality, outside this audit's charter) — listed as the first required addition of Phase 6.4 itself. |
| Unauthorized requests | **DEFERRED, correctly** — no authentication middleware exists yet (by design); nothing to audit until Phase 6.4 adds it. |
| Future Windows/Android/iOS clients | **PASS** — nothing in `IIdentityProvider`/`AuthenticatedPrincipal` assumes a platform; `DeviceId` is a plain `Guid?`, not tied to any platform-specific concept (confirmed in §11). |

**No redesign recommended.** The shape is sound; the gap is a not-yet-built account-resolution step (expected — Phase 6.3 was never asked to build it) and the account-status field (§13).

---

## 4. Authentication vs. authorization audit — **PASS**

- Identity (who) comes exclusively from `IIdentityProvider` → `AuthenticatedPrincipal.Roles`; nowhere in Domain or Application is a `Role` ever constructed from anything other than this record.
- Authorization (what) is entirely `IAuthorizationService`'s responsibility, operating only on an already-authenticated `AuthenticatedPrincipal`.
- **CUSTOMER/ADMIN/SUPER_ADMIN hierarchy is not client-authoritative** — confirmed by inspection: no code path anywhere in `VTTranslate.Backend.*` parses a `Role` from an HTTP request body, query string, or header. The only two places a `Role` value is ever constructed are (a) test code, and (b) the (not-yet-built) future claim-mapping inside a real `IIdentityProvider` implementation.
- **Client-provided role values cannot grant privileges** — true today by simple absence of any such code path; this must remain true when Phase 6.4 adds the real implementation (see §13's guidance about validating claim-to-`Role` mapping).
- **Account/device/entitlement decisions remain server-authoritative** — confirmed: `EntitlementService`, `DeviceRegistrationService`, and `UsageService` are all plain C# services with no client-facing HTTP surface wired to them yet (the `/entitlements`, `/devices`, `/usage` routes are still 501 placeholders) — there is currently no way for a client to influence any of these decisions at all, let alone bypass them.

---

## 5. Device/session audit

### Device model — **PASS**

- Device IDs are `Guid.NewGuid()`, generated in `DeviceRegistrationService.RegisterDeviceAsync` — confirmed by reading the method; no hardware API, MAC address, disk serial, or similar is read anywhere in this codebase (confirmed by a targeted grep across all backend source — zero matches for any hardware-identifier pattern).
- Device authorization (`Device.Status`) is a field on `Device`, entirely independent of `Account`/`Session`/authentication state — a device can be `Revoked` while the account itself remains fully authenticated; `EntitlementService` checks device authorization and subscription status as two independent conditions, never conflating them.
- Revocation is implemented and tested (`RevokeDeviceAsync`, idempotent, ownership-checked).
- Multi-platform: `DevicePlatform` enum (`Windows`/`Android`/`iOS`) exists and every service operates on it generically — confirmed no platform-conditional logic exists anywhere in `DeviceRegistrationService`.
- **Device identity is not treated as proof of user identity** — confirmed: nothing in `IIdentityProvider` or the authentication model accepts a `Device.Id` as a substitute for a verified account credential; `AuthenticatedPrincipal.DeviceId` is populated FROM a token claim (i.e., after authentication already succeeded), never used TO authenticate.

### Session model — **PASS, no conceptual collision found, one clarifying note**

Reviewed `Session` and every entity/field that could plausibly be confused with it:

- `Session` = one (device, refresh-token-family) **authentication** session — sign-out-this-device / sign-out-everywhere. Confirmed distinct from:
  - **A real-time translation session** — deliberately NOT modeled as its own entity in this phase (documented explicitly in `Session`'s own doc comment, written in Phase 6.3, reconfirmed correct here). `EntitlementService.CanStartTranslationSessionAsync` returns a stateless yes/no decision and creates no `Session` row and no other persistent record of "a translation session is in progress" — there is nothing to collide with yet.
  - **Device authorization** — `Device.Status`, a completely separate concept/entity, confirmed above.
  - **Entitlement** — `Entitlement`/`Subscription`, separate entities, no shared fields with `Session`.
- **One clarifying note, not a defect**: `AuthenticatedPrincipal.DeviceId`, `Session.DeviceId`, and `Device.Id` all conceptually refer to the same device, but nothing in the current code (correctly, since no protected endpoint exists yet) exercises the full chain of "token claims this DeviceId → is this Device actually Authorized for this Account → proceed." This is exactly the kind of wiring Phase 6.4/6.6 will need to add carefully (a token's `DeviceId` claim must be cross-checked against `IDeviceRegistrationService.IsDeviceAuthorizedAsync` before trusting it for anything) — flagged as required guidance in §13, not a present defect since there is no code path today that could get this wrong.

---

## 6. Role security audit — **PASS, with one documented trust-boundary note**

- `Role.Customer (0) < Role.Admin (1) < Role.SuperAdmin (2)` — confirmed via `Enums/Role.cs`.
- **Role escalation is impossible through client input** — confirmed, same finding as §4 (no code path constructs `Role` from client input anywhere in this phase).
- **Role checks are centralized** — confirmed: `IAuthorizationService.HasAtLeastRole`/`RequireAtLeastRole` are the only methods in the entire codebase that compare a `Role` against a required threshold; nothing else duplicates this logic.
- **Missing roles fail closed** — confirmed and tested (`NoRolesAtAll_NeverSatisfiesAnyRequirement`): a principal with zero roles satisfies no requirement, including the lowest (`Customer`).
- **Unknown roles**: a NEW test added by this review (`OutOfRangeRoleValue_IsTrustedNumerically_ByDesign`) makes explicit and regression-proof that `AuthorizationService` performs a pure numeric comparison and does **not** itself re-validate enum membership — safe today only because nothing constructs an out-of-range `Role` value anywhere in this phase. **This is documented as REQUIRED IMPLEMENTATION GUIDANCE for Phase 6.4** (§13): the future Entra claim-to-`Role` mapping must explicitly validate/whitelist recognized role-claim strings and reject (fail closed, default to `Customer` or reject the token outright) anything unrecognized — it must never do an unchecked numeric parse into `Role`.
- **Authorization decisions do not depend on UI state** — confirmed by construction: `IAuthorizationService` has no dependency on anything UI-related (it's in a platform-neutral `net8.0` library with no UI framework reference at all).

---

## 7. Entitlement audit — **PASS**

Re-verified against the actual `EntitlementService` code (not just the Phase 6.3 doc's description of it):

- **Entitlement checks fail closed**: confirmed in three separate places in the code — (a) `DeviceRegistrationService.GetMaxActiveDevicesAsync` returns `1` (not `int.MaxValue`) if no subscription or a malformed entitlement value is found; (b) `EntitlementService`'s `GracePeriod` branch denies if `GracePeriodDays` is missing or unparsable; (c) `ReadInt`/`ReadDouble` helpers return `null` (not a default "allow" value) on any missing/malformed entitlement, and every caller treats `null` as "no limit configured" only where that's the deliberately-safe interpretation (an unset usage limit means "no cap configured," which is a legitimate plan design, not a bypass — worth noting this is the ONE place "missing entitlement" means "unrestricted" rather than "deny," and it's intentional: a plan that simply doesn't define a usage cap is different from a plan whose cap value is corrupted).
- **Expired subscription cannot start a session** — tested (`ExpiredStatus_AlwaysDenies_EvenIfPeriodEndIsInTheFuture` — notably also proves `Status` is authoritative over any date field, which is the correct precedence).
- **Cancelled subscription cannot start a session** — tested.
- **Revoked/unauthorized device cannot start a session** — tested (`RevokedDevice_Denies`, `UnauthorizedDevice_Denies`), and device authorization is checked as step 1, before any subscription/usage logic even runs.
- **Usage limits are enforced server-side** — tested extensively (`UsageAtOrAboveLimit_Denies`, `UsageBelowLimit_Allows`, and critically `ClientReportedUsageHint_NeverCountsTowardTheLimit`, which proves a 10,000-second client-reported hint has zero effect on a 100-second limit).
- **Grace-period behavior follows Phase 6.2B** — tested both directions (`GracePeriod_WithinBound_Allows`, `GracePeriod_PastBound_Denies_NeverBecomesIndefiniteBypass`) plus the fail-closed case (`GracePeriod_MissingGraceEntitlement_FailsClosed_NotOpen`).
- **Offline behavior does not become an entitlement bypass** — not directly testable in this phase because no offline-caching mechanism exists anywhere in the code yet (correctly deferred per Phase 6.2B §12's own lean against building one) — there is nothing to audit here beyond confirming its absence, which is itself the correct state.
- **Client-reported usage cannot grant entitlement** — confirmed and tested, see above.

No implementation defect found. No business rule was changed by this review.

---

## 8. Usage audit — **PASS**

- **Server-authoritative path** (`RecordServerDerivedUsageAsync` → `GetAuthoritativeUsageSecondsAsync`, filtered to `UsageRecordSource.ServerDerived`) is the only path `EntitlementService` ever reads from.
- **Client-information-only path** (`RecordClientReportedHintAsync`) writes to the same table with a different `Source` tag and is **never** read by `GetAuthoritativeUsageSecondsAsync` — confirmed by reading the LINQ filter directly (`.Where(r => r.Source == UsageRecordSource.ServerDerived)`).
- **A malicious client reporting "0 seconds used" cannot obtain unlimited service** — confirmed structurally: `RecordClientReportedHintAsync` writes a `ClientReportedHint` record that is invisible to the enforcement path regardless of its value (0, negative-clamped-to-error, or a fabricated large number all have equally zero effect on the entitlement decision, proven by the existing `ClientReportedUsageHint_NeverCountsTowardTheLimit` test using the fabricated-large-number case — the "reports 0" case is symmetric and equally covered by the same code path, since the filter excludes the entire `ClientReportedHint` source regardless of its numeric value).
- **No client-facing endpoint currently calls either method** — `/usage` is still a 501 placeholder, so this entire boundary is not yet reachable from outside the process at all in this phase; the audit above is of the primitive's correctness, ready for Phase 6.6+ wiring.

---

## 9. API security boundary audit — **PASS, one accuracy defect found and fixed**

- `/health/live`, `/health/ready` — both appropriate for their stated purposes; both verified live (via a new `WebApplicationFactory`-based integration test, not just manual curl) to return only a fixed literal `{"status": "..."}` with HTTP 200, nothing else.
- **Defect found**: `Program.cs` carried a comment claiming "`Program` exposed for `VTTranslate.Backend.Tests`' minimal-API integration smoke test" — **no such test existed**. This is now fixed by adding the test the comment claimed (`ApiSecurityBoundaryTests`, 11 new test cases using `WebApplicationFactory<Program>`), rather than by deleting the now-accurate comment — closing the gap instead of hiding it. This was a documentation-accuracy defect, not a security defect (no code behaved differently than documented; a comment simply described a test that hadn't been written yet).
- **Future protected endpoints have a clear place for auth middleware**: confirmed — ASP.NET Core's standard `app.UseAuthentication()`/`app.UseAuthorization()` middleware pipeline slot (between `app.Build()` and the route mappings) is empty and ready; adding it is a well-understood, additive change, not a restructuring.
- **No sensitive configuration is exposed** — confirmed live: `/health/live`/`/health/ready` responses contain only the literal strings `"live"`/`"ready"`.
- **No provider credentials are exposed** — confirmed; no endpoint reads or returns anything from `ProviderCredentials`/`Identity`/`Billing`/`Database` config sections (those sections aren't even bound to any C# type yet — they exist only as documentation placeholders, §10 of the Phase 6.3 doc).
- **No internal exception details are returned unnecessarily** — confirmed: no endpoint has a try/catch that serializes an exception message to the response; the 501 placeholder bodies are static, hand-written JSON, not exception output. (ASP.NET Core's default developer-exception-page behavior in the Development environment is a separate, standard, non-production concern not altered here.)
- **501 placeholder endpoints cannot accidentally become authorization bypasses** — confirmed both by inspection (the placeholder handler calls no application service at all, so there is nothing to bypass) and now by test (`PlaceholderRoutes_Return501_NeverAFakeSuccess`, parameterized over 7 representative paths including nested ones like `/entitlements/provider-token`).

---

## 10. Configuration audit — **PASS** (reconfirms Phase 6.3's own findings, independently re-verified)

Re-ran the exact secret-scan patterns from Phase 6.3 plus the review's own broader pass across every new file (§ "Regression protection" below) — clean. `Identity`/`Billing`/`Database`/`ProviderCredentials` sections in both `appsettings.json` and `appsettings.Development.json` contain only `_comment` properties (enforced by `ConfigurationSecretSafetyTests`, which this review re-ran and confirms still passes). No API key, password, connection string with embedded credential, client secret, or provider token found anywhere in source or tests.

---

## 11. Cross-platform audit — **PASS**

Targeted search across every backend source file for WPF/Windows-specific/hardware-identifier/desktop-only patterns (`WPF`, `WASAPI`, `NAudio`, `Environment.MachineName`, `Environment.UserName`, MAC-address/CPUID patterns, `net8.0-windows`) → **zero matches** in any `.cs` file under `src/VTTranslate.Backend.*` or `tests/VTTranslate.Backend.Tests` (the only matches found were two doc-comments explicitly documenting that hardware fingerprinting is NOT used, plus incidental substring matches inside compiled binary/lock-file artifacts, which are not source code). `VTTranslate.Backend.Domain`/`.Application`/`.Infrastructure`/`.Api` all target plain `net8.0`, confirmed via each `.csproj`. `DevicePlatform` and `AuthenticatedPrincipal.DeviceId` are both platform-neutral as designed (§5).

---

## 12. Test audit

Before this review: 45 tests. **After this review: 60 tests** (15 added, all focused on gaps this specific audit identified — no speculative tests added).

| Attack surface the instruction asked to verify | Covered before this review? | Covered now |
|---|---|---|
| Unauthorized access | Partially (no HTTP-level test existed) | **Yes** — `ApiSecurityBoundaryTests` (new) |
| Role escalation | Yes (`AuthorizationServiceTests`) | Yes, plus the new out-of-range-value trust-boundary test |
| Entitlement bypass | Yes, extensively (`EntitlementServiceTests`) | Unchanged — already adequate |
| Revoked device | Yes | Unchanged — already adequate |
| Expired entitlement | Yes | Unchanged — already adequate |
| Usage-limit bypass | Yes | Unchanged — already adequate |
| Client-reported usage manipulation | Yes | Unchanged — already adequate |
| Invalid identity | Partially (only one generic token value tested) | **Yes** — new `[Theory]` covering empty/null/garbage token values |
| Missing identity | Partially | **Yes** — same theory covers this |
| Unknown role | **No — genuinely missing** | **Yes** — new test added, see §6 |

**New test files/additions**: `ApiSecurityBoundaryTests.cs` (new file, 11 tests, requires a new `Microsoft.AspNetCore.Mvc.Testing` package reference and a project reference from the test project to the API project — both added), one new test in `AuthorizationServiceTests.cs`, one new `[Theory]` (replacing a single `[Fact]`) in `ProviderPlaceholderTests.cs`.

No unnecessary speculative tests were added (e.g., no tests for hypothetical future entitlement types, no tests for a database vendor that doesn't exist yet, no tests for Entra-specific claim formats that don't exist yet).

---

## 13. Required changes before Phase 6.4

These are sequencing requirements for Phase 6.4's own work, not changes made in this review (making them now would be adding product functionality outside this audit's charter):

1. **Add account-status support** (`Account.Status` or an `IsSuspended` concept, distinct from the existing soft-delete `DeletionRequestedAt`) — needed before Phase 6.4 can correctly reject a suspended/disabled account during its account-resolution step. This should be the FIRST small addition of Phase 6.4, before any real Entra call is wired in.
2. **Build the account-resolution step**: given a validated `AuthenticatedPrincipal`, look up (or provision, for first login) the corresponding `Account` via `IAccountRepository.FindByExternalIdentityAsync`, check its status (item 1), and only then proceed. This glue code does not exist yet — both halves it connects (`IIdentityProvider` and `IAccountRepository`) do.
3. **Claim-to-`Role` mapping must fail closed on unrecognized values**: the real `EntraExternalIdIdentityProvider`'s role-claim parsing must explicitly validate against the known `Role` enum members and reject/default-deny anything else — never an unchecked numeric or string parse (§6).
4. **Device-claim cross-check**: before trusting a token's `DeviceId` claim for any device-scoped operation, Phase 6.4/6.6 must call `IDeviceRegistrationService.IsDeviceAuthorizedAsync` to confirm that device is actually authorized for that account — this wiring does not exist yet (§5).
5. **Wire `app.UseAuthentication()`/`app.UseAuthorization()`** into the empty middleware slot identified in §9, once a real `IIdentityProvider` exists to back it.

None of these require redesigning anything already built in Phase 6.3 — all are additive.

---

## 14. Deferred changes (explicitly not needed for Phase 6.4, revisit later)

- Real database vendor selection (Phase 6.2's D3 remains unresolved; not needed until real persistence is required).
- Paddle/billing integration (explicitly out of scope, remains evaluation-only).
- Any offline-caching mechanism (Phase 6.2B §12 already leans against building one; nothing changes that assessment here).
- `Device.Status = Pending`'s eventual manual-approval trigger (still undesigned, still not needed).
- Admin/super-admin UI or dedicated admin surface.
- Multi-role-per-account support (flagged as a possible future need in Phase 6.3's own doc; not revisited here since nothing in Phase 6.4's scope requires it).

---

## 15. Final recommendation

**APPROVE Phase 6.4 to begin**, conditioned on Phase 6.4 addressing the five items in §13 as part of its own scope (in the order listed — items 1–2 before any endpoint goes live, items 3–5 as part of wiring the real identity provider and its middleware). No BLOCKED findings exist in the Phase 6.3 code as delivered. The layering, fail-closed entitlement logic, and server-authoritative usage separation are all sound, real, and test-proven — Phase 6.4 can build on them directly rather than needing to revisit them.

---

## Regression protection — exact results

**Full solution build**: `dotnet build VTTranslate.sln` → **0 Warning(s), 0 Error(s)**, all 9 projects.

**Backend tests**: `dotnet test tests/VTTranslate.Backend.Tests/VTTranslate.Backend.Tests.csproj` → **60/60 passed** (45 pre-existing + 15 added by this review), 0 failed, 0 skipped.

**Existing Windows/Core tests**: `dotnet test tests/VTTranslate.Core.Tests/VTTranslate.Core.Tests.csproj` → **399/399 passed**, unchanged from the established baseline.

**Secret scan**: re-run across all new/modified files (including the newly-added `ApiSecurityBoundaryTests.cs` and the `Microsoft.AspNetCore.Mvc.Testing` package reference) — clean, no matches beyond the already-known false positive (a regex pattern's own string literal in `ConfigurationSecretSafetyTests.cs`).

**Production-isolation check**: `git diff --stat` against `DirectionPipeline.cs`, `AzureSpeechTranslationProvider.cs`, `GeminiNaturalizationProvider.cs`, `MeaningPreservationValidator.cs` — the first two carry only their pre-existing baseline diff (present since before this entire Step 5.x/Phase 6 series began); `GeminiNaturalizationProvider.cs` and `MeaningPreservationValidator.cs` show **zero diff**. None of the four were touched by this review.

---

## Files changed in this review

- **New**: `tests/VTTranslate.Backend.Tests/ApiSecurityBoundaryTests.cs`, `docs/phase-6.3-review-gate.md`.
- **Modified**: `tests/VTTranslate.Backend.Tests/AuthorizationServiceTests.cs` (+1 test), `tests/VTTranslate.Backend.Tests/ProviderPlaceholderTests.cs` (1 test generalized into a 3-case theory), `tests/VTTranslate.Backend.Tests/VTTranslate.Backend.Tests.csproj` (added `Microsoft.AspNetCore.Mvc.Testing` package reference and a project reference to `VTTranslate.Backend.Api`).
- **Untouched**: every file under `src/VTTranslate.Backend.Domain/`, `src/VTTranslate.Backend.Application/`, `src/VTTranslate.Backend.Infrastructure/`, `src/VTTranslate.Backend.Api/` (the false doc-comment on `Program` was left as-is and made TRUE by adding the test it described, per the instruction to close gaps rather than paper over them) — no business-logic code was changed anywhere.
- **Untouched, confirmed**: everything under `src/VTTranslate.Core/`, `src/VTTranslate.App/`, `tools/VTTranslate.LiveTest/`, `tests/VTTranslate.Core.Tests/`.

**STOP — Phase 6.3 review gate complete. Phase 6.4 is APPROVED to begin, conditioned on §13. No Entra integration, login/registration UI, production database, or Paddle work was performed. Waiting for review.**
