# Phase 6.3 — AUTRAXIS Backend Foundation

**Status: BACKEND FOUNDATION IMPLEMENTED (domain + application logic, real and tested). Identity, billing, and persistence integrations are explicit placeholders — not implemented. No production Windows code was modified.**

This document reports what Phase 6.3 built, labeling every capability **IMPLEMENTED**, **DESIGN ONLY**, **NOT YET IMPLEMENTED**, or **REQUIRES FUTURE INTEGRATION** per the instruction.

---

## 1. Repository analysis (performed before writing any code)

| Aspect | Finding |
|---|---|
| Solution structure | One flat `VTTranslate.sln` with `src/` (Core, App) and `tests/` (Core.Tests) and `tools/` (LiveTest) solution folders — small, single-solution, no separate backend repo anywhere. |
| Target frameworks | `VTTranslate.Core`/`VTTranslate.App` both target `net8.0-windows` (Windows-only APIs: NAudio/WASAPI, Azure Speech SDK, WPF). `VTTranslate.Core.Tests` also `net8.0-windows` (references Core). |
| Dependency management | Plain `<PackageReference>` in each `.csproj`, no central package management file, no lock files committed. |
| Configuration conventions | `VTTranslate.Core.Config.AppSettings` — non-secret settings persist to a local JSON file; secrets (`AZURE_SPEECH_KEY`/`REGION`, `AZURE_TRANSLATOR_KEY`, `GEMINI_API_KEY`) are read **exclusively from environment variables**, never written to disk, never logged. This convention is directly extended into the new backend's configuration approach (§9 below). |
| Test conventions | xUnit, deterministic fakes for every external dependency (no real network calls in any unit test across the 399 existing `VTTranslate.Core.Tests`) — the same discipline was applied to the new `VTTranslate.Backend.Tests`. |
| Existing interfaces/abstractions | `ISpeechTranslationProvider` is the proven pattern this whole repository already uses to isolate a vendor (Azure Speech) behind a domain-owned interface. `IIdentityProvider`/`IBillingProvider` (§5, §7 below) are built the same way, deliberately. |
| Logging/error handling | `IDiagnosticLogger`/`FileDiagnosticLogger` — metadata-only logging (lengths, booleans, timestamps; never speech content or secrets). The new `AuditEvent` entity (§4) and the explicit "no secrets in health/log output" rule (§10, §11) extend this same discipline server-side. |
| Build/test scripts | Plain `dotnet build`/`dotnet test` invocations (no CI pipeline file found in the repository) — the new projects build and test the same way, added to the same solution. |
| Windows application boundaries | `VTTranslate.App` (WPF UI) → `VTTranslate.Core` (`DirectionPipeline`, `AzureSpeechTranslationProvider`, `Audio/*`) — a clean, already-isolated boundary that this phase does not cross in either direction. |

### A vs. B: new project(s) in the current solution, or a separate backend solution?

**Recommendation: A — new projects inside the current solution.** Reasoning:

- The repository is small (one team, one solution) with an already-established, working `dotnet build`/`dotnet test` workflow that every prior phase (5.x UI, Step 5.x experiments) has relied on without friction — introducing a second solution/repo would fragment that workflow for no offsetting benefit at this size.
- `VTTranslate.Core`/`VTTranslate.App` are `net8.0-windows`; the new backend targets plain `net8.0` (platform-neutral, since it must eventually run on Linux/cloud infrastructure, not just Windows) — this is handled cleanly by giving the new projects their own `<TargetFramework>net8.0</TargetFramework>`, with **zero project reference** from the backend to `VTTranslate.Core`/`VTTranslate.App` (confirmed — see §12) or vice versa. One solution can freely mix target frameworks per project; this is not a reason to split solutions.
- A separate solution/repo would be justified once the backend has its own independent release cadence, its own CI/CD pipeline, or its own team — none of which exist yet. Revisiting this decision later (extracting the backend into its own repo) is a low-cost, mechanical `git filter-repo`-style operation if it's ever needed; it is not a decision that gets meaningfully harder to make later.

**Status: DECIDED for this phase (not a Phase 6.1/6.2/6.2B-approved item — a Phase 6.3-scoped engineering decision, made per the instruction "recommend the least disruptive maintainable approach").**

---

## 2. Project structure created — **IMPLEMENTED**

Five new projects, all added to the existing `VTTranslate.sln`:

```
src/VTTranslate.Backend.Domain/          — entities, enums, abstractions (ports). No dependencies on any other project.
src/VTTranslate.Backend.Application/     — application services (use-case orchestration). Depends only on Domain.
src/VTTranslate.Backend.Infrastructure/  — in-memory persistence + explicit NOT-IMPLEMENTED identity/billing stubs. Depends on Domain + Application.
src/VTTranslate.Backend.Api/             — ASP.NET Core minimal-API host (composition root + health/placeholder endpoints). Depends on all three above.
tests/VTTranslate.Backend.Tests/         — xUnit tests, 45 tests, all against fakes/in-memory implementations.
```

This is the four-layer separation requested (API/interface, application/service, domain, infrastructure), enforced by **project references, not just folders/namespaces** — `VTTranslate.Backend.Domain` cannot accidentally reference `VTTranslate.Backend.Infrastructure` because no such project reference exists; the compiler enforces the boundary.

**Confirmed zero coupling to the existing Windows app**: neither `VTTranslate.Backend.Domain`, `.Application`, `.Infrastructure`, nor `.Api` references `VTTranslate.Core` or `VTTranslate.App`, and neither of those existing projects references any new backend project. The backend foundation and the Windows MVP are two independent build graphs sharing one solution file, exactly as intended.

---

## 3. Domain layer — **IMPLEMENTED**

`VTTranslate.Backend.Domain` has no package dependencies at all (pure C#) and no dependency on Entra, Paddle, Azure, or any database vendor — satisfying the instruction directly, not just in spirit.

### Entities implemented (all ten requested, each justified against Phase 6.1/6.2B — none invented beyond what those documents call for):

| Entity | Fields | Notes on undecided business meaning |
|---|---|---|
| `Account` | `Id`, `Email`, `EmailVerified`, `ExternalIdentityProvider`, `ExternalSubjectId`, `Role`, `CreatedAt`, `DeletionRequestedAt` | `Role` is a single value here (not a list) — Phase 6.2B's role model (§8) does not describe multi-role accounts; if that's ever needed, this is a documented future change, not assumed now. |
| `Profile` | `AccountId`, `DisplayName`, `PreferredLanguagePair`, `CreatedAt`, `UpdatedAt` | `PreferredLanguagePair`'s exact business meaning ("which of the two existing directions this seeds") is explicitly documented as undecided pending Phase 6.8's desktop integration. |
| `Plan` | `Id`, `Name`, `PriceHandle`, `IsPubliclyPurchasable` | `PriceHandle` is an opaque string, populated only once a billing provider exists — null in this phase, by design. |
| `Entitlement` | `Id`, `PlanId`, `Key`, `Value` | Deliberately key/value (string `Value`) rather than typed columns — see Phase 6.2's D5 finding that no alternative entitlement model was ever compared; a closed schema would have invented a decision Phase 6.1/6.2 didn't make. |
| `Subscription` | `Id`, `AccountId`, `PlanId`, `Status`, `CurrentPeriodStart/End`, `BillingProviderSubscriptionId`, `CreatedAt`, `UpdatedAt` | Matches Phase 6.1 §8 exactly. |
| `Device` | `Id`, `AccountId`, `Platform`, `DisplayName`, `Status`, `RegisteredAt`, `LastSeenAt`, `RevokedAt` | Matches Phase 6.2B §6's explicit field list exactly — no additions. |
| `Session` | `Id`, `AccountId`, `DeviceId`, `RefreshTokenFamilyId`, `IssuedAt`, `ExpiresAt`, `RevokedAt`, `IpAddress` | This is the **authentication** session (sign-out-this-device), documented explicitly as distinct from a real-time translation session (which is not modeled as its own entity in this phase — see §7). |
| `UsageRecord` | `Id`, `AccountId`, `DeviceId`, `Direction`, `SecondsUsed`, `Provider`, `Source`, `PeriodBucket`, `RecordedAt` | `Source` (`ServerDerived`/`ClientReportedHint`) is a field **justified directly** by Phase 6.2B §7's server-authoritative requirement — without it, the domain couldn't distinguish a trustworthy record from an untrustworthy one. `SecondsUsed` is documented as a raw measurement, explicitly NOT a committed billing unit (instruction: "do not invent final billing units"). |
| `AuditEvent` | `Id`, `AccountId`, `EventType`, `Metadata`, `OccurredAt` | `EventType`/`Metadata` are free-form strings, not closed enums/schemas — the exhaustive list of audit-worthy events will grow across future phases without requiring an entity change. |

**Not created** (and why, so nothing is silently assumed): a `PaymentMethod`/`Invoice` table (delegated to the billing provider per Phase 6.1 §8), a transcript/history table (this project's standing convention is to never persist recognized/translated speech), and a separate "real-time translation session" entity (§7 explains why that's deferred).

### Enums implemented
`Role` (`Customer`/`Admin`/`SuperAdmin`, ordered — Phase 6.2B §8), `SubscriptionStatus` (Phase 6.1 §4), `DeviceStatus`/`DevicePlatform` (Phase 6.2B §6), `UsageRecordSource` (Phase 6.2B §7).

---

## 4. Identity abstraction — **IMPLEMENTED (boundary only); Entra integration is NOT YET IMPLEMENTED**

`IIdentityProvider` (in Domain) defines exactly the surface the Phase 6.3 instruction asked for: `AuthenticatedPrincipal` (external subject id, email, email-verified flag, role claims, device-id claim, token issued/expiry) plus `ValidateAccessTokenAsync`/`RevokeSessionAsync`. **Registration/login/password-reset methods are deliberately NOT part of this interface** — those are out of scope per the instruction's explicit stop-list, and adding them now would have been designing ahead of an approved need.

`NotImplementedIdentityProvider` (Infrastructure) is the only implementation. Every method throws `NotImplementedException` with a message naming exactly which future phase replaces it. **This is not fake authentication** — proven by test (`ProviderPlaceholderTests`): calling it never returns a fabricated principal, never accepts a hard-coded user.

**REQUIRES FUTURE INTEGRATION**: a real `EntraExternalIdIdentityProvider` implementing this same interface (Phase 6.4).

---

## 5. Billing abstraction — **IMPLEMENTED (boundary only); Paddle integration is NOT YET IMPLEMENTED**

`IBillingProvider` (in Domain) defines `FindCustomerAsync`, `GetSubscriptionStateAsync`, returning vendor-neutral `BillingCustomer`/`BillingSubscriptionState` records — no Paddle-specific type or concept appears anywhere in `Domain` or `Application`.

`NotImplementedBillingProvider` (Infrastructure) is the only implementation, throwing `NotImplementedException` on every call — proven by test. **No Paddle package, SDK, or API call exists anywhere in this repository.**

**REQUIRES FUTURE INTEGRATION**: a real `PaddleBillingProvider` (a later, separately-approved phase — Paddle remains RECOMMENDED-for-evaluation only per Phase 6.2B, not approved for integration).

---

## 6. Entitlement boundary — **IMPLEMENTED, real logic, unit-tested**

`IEntitlementService.CanStartTranslationSessionAsync(accountId, deviceId)` is a fully working implementation (`EntitlementService`) answering exactly the question the instruction posed: device authorization → subscription status → (for `GracePeriod`) a bounded check against `EntitlementKeys.GracePeriodDays` that **fails closed** if the entitlement is missing or the bound has passed (directly implementing Phase 6.2B §12's "grace period must never become an indefinite bypass") → usage-against-limit, reading `TrialUsageLimitSeconds` or `UsageLimitSecondsPerPeriod` depending on subscription status.

**No pricing, trial duration, or usage limit is hard-coded anywhere in this service** — every threshold is read from `Entitlement` rows via `IPlanRepository`, and a missing/malformed entitlement value fails closed (denies), never open. 15 dedicated tests (`EntitlementServiceTests`) cover: allow/deny for every subscription status, the grace-period bound (both sides), missing-entitlement fail-closed behavior, usage-at/under/over-limit, and — the specific regression this phase was told to guard against — a client-reported usage "hint" ten thousand seconds over the limit has **zero effect** on the decision.

---

## 7. Device licensing boundary — **IMPLEMENTED, real logic, unit-tested**

`IDeviceRegistrationService` implements register/list/authorize/revoke/`IsDeviceAuthorizedAsync`, exactly the five operations requested. Device identity is `Guid.NewGuid()` — an application-generated identifier — **never** derived from any hardware characteristic; there is no code path in this service that reads MAC addresses, disk serials, or any other hardware fingerprint. `MaxActiveDevices` is read from the account's plan entitlements (falls back to a conservative default of `1` if no subscription/entitlement exists — fail closed, not unlimited). 8 tests (`DeviceRegistrationServiceTests`) cover registration under/at the limit, revoked devices freeing up the limit, idempotent revocation, and cross-account ownership rejection.

**No platform-specific (Windows/Android/iOS) code exists** — this service operates purely on the `DevicePlatform` enum value passed to it; nothing here touches WASAPI, AudioRecord, or AVAudioSession.

---

## 8. Usage boundary — **IMPLEMENTED, real logic, unit-tested**

`IUsageService` separates `RecordServerDerivedUsageAsync` (the only source ever summed by `GetAuthoritativeUsageSecondsAsync`, which `EntitlementService` calls) from `RecordClientReportedHintAsync` (display/anomaly-detection only, per Phase 6.2B §7). 7 tests (`UsageServiceTests`) prove the separation holds, including that different accounts/periods are never conflated and that a negative duration is rejected outright.

**Explicitly documented, per the instruction, as NOT YET IMPLEMENTED**: the actual call site that will invoke `RecordServerDerivedUsageAsync` from real provider-token issuance/renewal/expiry tracking (Phase 6.2B §7's "server derives usage from its own token lifecycle records") does not exist yet — it requires the real-time-translation backend integration (`/entitlements/provider-token`) that is explicitly out of scope for this phase. The recording/query *primitive* is built and tested; the *automatic trigger* for it is a Phase 6.6+ concern, documented in the interface's own doc comment so this isn't forgotten.

---

## 9. Authorization (roles) — **IMPLEMENTED, real logic, unit-tested**

`IAuthorizationService.HasAtLeastRole`/`RequireAtLeastRole` implement the `CUSTOMER < ADMIN < SUPER_ADMIN` hierarchy check described in Phase 6.2B §8, operating only on an already-validated `AuthenticatedPrincipal.Roles` (i.e., a verified token claim — never a client-supplied value). 7 tests confirm the hierarchy in both directions and confirm a principal with no roles at all satisfies no requirement (no silent default-to-Customer). **No admin UI was built** — correctly out of scope per the instruction.

---

## 10. Configuration strategy — **IMPLEMENTED**

`appsettings.json` contains four **empty, comment-only placeholder sections** (`Identity`, `Billing`, `Database`, `ProviderCredentials`) — every property in them is a `_comment` string, never a real value. This is enforced by a dedicated test (`ConfigurationSecretSafetyTests.ConfigFile_IdentityBillingDatabaseProviderSections_AreEmptyPlaceholdersOnly`), not just a claim: the test fails if any non-comment property ever appears in those sections. A second test scans both committed config files against five secret-shaped regex patterns (Azure key shape, Gemini/Google key shape, OpenAI key shape, a connection string carrying `Password=`, an embedded bearer token) — all pass clean today, and will fail the build the moment a real secret is accidentally committed there in a future phase.

This directly extends `VTTranslate.Core.Config.AppSettings`'s existing convention (secrets from environment variables only, never from a committed file) to the backend.

---

## 11. Health/observability — **IMPLEMENTED**

`/health/live` and `/health/ready` both return a fixed literal `{"status": "..."}` — no configuration values, no connection state, no account information. Verified live (not just by inspection): the running API was started and both endpoints were curled, returning exactly `{"status":"live"}` / `{"status":"ready"}` at HTTP 200. Liveness and readiness are separated as two distinct routes per the instruction, even though in this phase (no real external dependency to check yet) they currently do the same thing — `/health/ready`'s implementation is the natural place a future phase adds an actual database/dependency check without changing the route contract.

---

## 12. Persistence strategy — **DESIGN ONLY for the real vendor; IMPLEMENTED for the in-memory/test path**

**No database vendor was approved in Phase 6.1/6.2/6.2B** — Phase 6.2's D3 recommended Azure SQL (with PostgreSQL as a flagged alternative) but this was never elevated to an approved product-owner decision in Phase 6.2B (which resolved nine *other* named decisions and did not include D3). Per the explicit instruction ("if a final database vendor was not actually approved... do not silently lock the architecture to a vendor"), **this phase does not choose one**.

Instead: `IAccountRepository`, `IDeviceRepository`, `ISubscriptionRepository`, `IPlanRepository`, `IUsageRecordRepository`, `IAuditEventRepository` (all in Domain) are the persistence ports — their method signatures contain no SQL, no Entity Framework type, no vendor-specific concept anywhere. `VTTranslate.Backend.Infrastructure.Persistence` provides **in-memory, `ConcurrentDictionary`/`ConcurrentBag`-backed implementations only**, explicitly documented (in the file's own header comment) as "NOT A PRODUCTION PERSISTENCE LAYER... never register these for anything resembling a production deployment." These are used by all 45 backend unit tests and would be usable for local development, but **no production cloud database was created, and none is implied by this code**.

**Remaining choice, unresolved**: Azure SQL vs. PostgreSQL vs. another relational engine — genuinely open, to be decided in a future phase once real persistence is needed (Phase 6.5+ territory, once Subscription/Plan data needs to survive a process restart).

---

## 13. API foundation — **IMPLEMENTED (health) / explicit contract placeholders (everything else)**

`/account`, `/devices`, `/subscription`, `/entitlements`, `/usage` (and any sub-path) all return **HTTP 501** with a JSON body explicitly stating `"status": "not_implemented"` and naming the phase and reason — verified live via curl, shown in §"Verification" below. **No endpoint returns a fabricated 200, a hard-coded subscription state, or any other fake success** — this was a specific instruction and is enforced by the endpoint implementation itself (a single shared `NotImplementedPlaceholder` route handler, so there is no risk of one endpoint accidentally being "more implemented" than the others without a deliberate code change).

The REAL application-layer logic these routes will eventually call (`IDeviceRegistrationService`, `IEntitlementService`, `IUsageService`, `IAuthorizationService`) already exists and is fully unit-tested — what's missing to safely expose it over HTTP is authenticated request context (extracting `AccountId`/`DeviceId`/`Roles` from a real, verified token), which requires Entra integration (Phase 6.4). Wiring real endpoint bodies onto already-tested services is expected to be a small, low-risk change once that exists.

---

## 14. Testing — **IMPLEMENTED**

45 tests in `VTTranslate.Backend.Tests`, all against fakes/in-memory implementations — **zero real network calls to Entra, Paddle, Azure, Gemini, or OpenAI occur anywhere in this test suite** (confirmed by inspection: no `HttpClient` pointed at a real host exists in any test or in the services under test — the only infrastructure code that would ever make a real external call, `NotImplementedIdentityProvider`/`NotImplementedBillingProvider`, throws before doing so).

| Test class | Count | Covers |
|---|---|---|
| `AuthorizationServiceTests` | 7 | Role hierarchy boundaries |
| `DeviceRegistrationServiceTests` | 8 | Device authorization rules, limit enforcement, ownership |
| `EntitlementServiceTests` | 15 | Entitlement evaluation boundaries (every subscription status, grace bound, usage limits, client-hint immunity) |
| `UsageServiceTests` | 7 | Usage-record validation, source separation, period/account isolation |
| `ProviderPlaceholderTests` | 6 | Identity/billing abstraction behavior — proves placeholders never fake success |
| `ConfigurationSecretSafetyTests` | 4 | Secret/configuration safety (parameterized over both committed config files) |

---

## 15. Security — secret scan result

`grep -rEi` for Azure-key/Gemini-key/OpenAI-key/password-bearing-connection-string/bearer-token shapes across every new backend file (`src/VTTranslate.Backend.*`, `tests/VTTranslate.Backend.Tests`) → **one match, a false positive**: the regex pattern literal itself, written as a string inside `ConfigurationSecretSafetyTests.cs` (the pattern `"Server=.*Password="` matching itself when grepped for). No actual secret, password, API key, connection string, or bearer token appears anywhere in the new code. **Confirmed clean.**

---

## 16. Existing Windows application — untouched, confirmed

`git diff --stat` against `DirectionPipeline.cs`, `AzureSpeechTranslationProvider.cs`, `GeminiNaturalizationProvider.cs`, `MeaningPreservationValidator.cs`, `MainViewModel.cs`, `MainWindow.xaml` shows: the last two files carry only their pre-existing Phase 5 (branding) diff, `DirectionPipeline.cs`/`AzureSpeechTranslationProvider.cs` carry only their pre-existing baseline diff (present since before this entire Step 5.x/Phase 6 series began), and `GeminiNaturalizationProvider.cs`/`MeaningPreservationValidator.cs` show **zero diff** — none of these six files were touched in this phase. The full existing 399-test suite (`VTTranslate.Core.Tests`) was re-run and passes unchanged.

---

## 17. Future integration points (explicit — not implemented, so nothing is forgotten)

- **REQUIRES FUTURE INTEGRATION**: `EntraExternalIdIdentityProvider` (Phase 6.4).
- **REQUIRES FUTURE INTEGRATION**: `PaddleBillingProvider` — or another provider — behind `IBillingProvider` (a separately-approved future phase; Paddle remains evaluation-only).
- **REQUIRES FUTURE INTEGRATION**: a real database-backed set of repository implementations, once a vendor is chosen (§12).
- **REQUIRES FUTURE INTEGRATION**: the `/entitlements/provider-token` endpoint (Phase 6.1 §5/§9) and its token-lifecycle tracking, which is what will actually call `IUsageService.RecordServerDerivedUsageAsync` in production (§8).
- **REQUIRES FUTURE INTEGRATION**: real HTTP endpoint bodies for `/account`, `/devices`, `/subscription`, `/entitlements`, `/usage`, once authenticated request context exists (Phase 6.4+), calling the already-implemented and tested application services.
- **NOT YET IMPLEMENTED**: any admin/super-admin UI or dedicated admin surface (Phase 6.2B §8 explicitly left this scope-note open).

---

## 18. Unresolved decisions surfaced or reconfirmed by this phase

- **Database vendor** (§12) — genuinely open, not silently decided.
- **`Device.Status = Pending`'s eventual trigger** — this phase's `DeviceRegistrationService` auto-authorizes under the device limit and never produces a `Pending` device; `Pending` is reserved in the enum for a possible future manual-approval flow that Phase 6.1/6.2B never designed. Documented in `IDeviceRegistrationService`'s own doc comment.
- **Multi-role accounts**: `Account.Role` is a single value; Phase 6.2B's role model doesn't address whether an account could ever hold more than one role simultaneously. Flagged, not assumed.
- Every item already listed as open in `docs/phase-6.2-architecture-decision-matrix.md` and `docs/phase-6.2b-resolved-architecture-decisions.md` (trial values, grace-period length, device-pooling policy, password policy, registration-method specifics beyond email/password, merchant-of-record/tax questions) remains exactly as open as those documents left it — nothing in Phase 6.3 resolves or silently forecloses any of them.

---

## Verification — exact results

**1. Full existing test suite** (`VTTranslate.Core.Tests`): `dotnet test tests/VTTranslate.Core.Tests/VTTranslate.Core.Tests.csproj` → **399/399 passed**, 0 failed, 0 skipped.

**2. Backend tests** (`VTTranslate.Backend.Tests`): `dotnet test tests/VTTranslate.Backend.Tests/VTTranslate.Backend.Tests.csproj` → **45/45 passed**, 0 failed, 0 skipped.

**3. Full solution build**: `dotnet build VTTranslate.sln` → **0 Warning(s), 0 Error(s)**, all 9 projects built (`VTTranslate.Core`, `VTTranslate.App`, `VTTranslate.LiveTest`, `VTTranslate.Core.Tests`, `VTTranslate.Backend.Domain`, `VTTranslate.Backend.Application`, `VTTranslate.Backend.Infrastructure`, `VTTranslate.Backend.Api`, `VTTranslate.Backend.Tests`).

**4. Secret scan**: one false-positive match (the test's own regex-pattern string literal); no actual secret found. See §15.

**5. Production-isolation check**: `DirectionPipeline.cs`, `AzureSpeechTranslationProvider.cs`, `GeminiNaturalizationProvider.cs`, `MeaningPreservationValidator.cs`, `MainViewModel.cs`, `MainWindow.xaml` — confirmed unmodified by this phase (pre-existing diffs only, two files with zero diff at all). See §16.

**6. Live smoke test**: the API was actually started (`dotnet run`) and its endpoints called with `curl`:
- `GET /health/live` → `200 {"status":"live"}`
- `GET /health/ready` → `200 {"status":"ready"}`
- `GET /account/me` → `501 {"status":"not_implemented","phase":"6.3",...}`
- `GET /devices` → `501 {"status":"not_implemented","phase":"6.3",...}`

---

## Files changed/added (complete list)

**New projects/files:**
- `src/VTTranslate.Backend.Domain/**` (Entities/, Enums/, Abstractions/, Exceptions.cs) — 15 files
- `src/VTTranslate.Backend.Application/**` (Authorization/, Devices/, Entitlements/, Usage/) — 6 files
- `src/VTTranslate.Backend.Infrastructure/**` (Identity/, Billing/, Persistence/, Time/) — 4 files
- `src/VTTranslate.Backend.Api/**` (Program.cs rewritten; appsettings.json rewritten; launchSettings.json/`.http` file cleaned of template leftovers; default template files `WeatherForecast.cs`/`Controllers/` removed)
- `tests/VTTranslate.Backend.Tests/**` — 7 test files (default `UnitTest1.cs` removed)
- `docs/phase-6.3-backend-foundation.md` (this file)

**Modified:** `VTTranslate.sln` (five new project entries added).

**Untouched:** every file under `src/VTTranslate.Core/`, `src/VTTranslate.App/`, `tools/VTTranslate.LiveTest/`, and `tests/VTTranslate.Core.Tests/` (verified — see §16).

---

## Confirmations

- **No Entra integration, login, registration, email verification, or password reset was implemented** — `IIdentityProvider`'s only implementation throws `NotImplementedException`.
- **No Paddle or payment integration was implemented** — `IBillingProvider`'s only implementation throws `NotImplementedException`.
- **No production database was deployed or created** — only in-memory, test/dev-only repository implementations exist.
- **No customer UI integration, mobile work, real-time-translation backend, or provider API gateway was built.**
- **Existing translation/audio code was not modified** — confirmed in §16.

**STOP — Phase 6.3 backend foundation complete. Waiting for review before Phase 6.4.**
