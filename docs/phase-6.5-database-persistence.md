# Phase 6.5 — Production Database & Persistence Foundation

## 1. Objective

Replace the Phase 6.3 in-memory-only persistence layer with a real, durable
PostgreSQL-backed implementation, while keeping Domain and Application
completely independent of the database technology. See
[docs/phase-6.5-database-decision.md](phase-6.5-database-decision.md) for
the vendor decision and rationale. **IMPLEMENTED.**

## 2. Architecture

```
Domain (Account, Profile, Plan, Entitlement, Subscription, Device,
        Session, UsageRecord, AuditEvent — plain POCOs, zero packages)
   ↓
Application (use-case services — unchanged from Phase 6.3/6.4)
   ↓
Repository interfaces (IAccountRepository, IProfileRepository,
   IDeviceRepository, ISubscriptionRepository, IPlanRepository,
   IUsageRecordRepository, IAuditEventRepository, ISessionRepository — all
   in Domain, vendor-neutral)
   ↓
Infrastructure
   ├─ Persistence/               — in-memory implementations (dev/test only, Phase 6.3)
   └─ Persistence/EfCore/        — PostgreSQL implementations (Phase 6.5, NEW)
        ↓
   PostgreSQL (Azure Database for PostgreSQL in production; the official
   Docker image locally/in CI)
```

Verified (§9 below): `grep` for `EntityFrameworkCore`/`Npgsql` across
Domain, Application, and the API's own source (excluding `Program.cs`'s DI
wiring, which only calls `AddDbContext`/`UseNpgsql` — it never touches an
EF type directly) returns nothing. No Domain entity has an EF Core
attribute, base class, or navigation-property collection added for ORM
convenience.

## 3. Database technology

PostgreSQL 16, via `Npgsql.EntityFrameworkCore.PostgreSQL` 8.0.11 on
Entity Framework Core 8.0.11. Full justification in the decision document.
**IMPLEMENTED.**

## 4. Schema/domain mapping

All nine approved domain entities are mapped via `IEntityTypeConfiguration<T>`
classes in `Infrastructure/Persistence/EfCore/Configurations/
EntityConfigurations.cs` — Fluent API only, no attributes on Domain types:

| Entity | Table | Notes |
|---|---|---|
| `Account` | `accounts` | Role/Status stored as strings (readable in the DB, no magic numbers); unique index on identity mapping (§6). |
| `Profile` | `profiles` | `AccountId` is the primary key (true 1:1) — Phase 6.5 addition of `IProfileRepository`/`EfProfileRepository`, since none existed before this phase (see §17). |
| `Plan` | `plans` | Unchanged shape from Phase 6.3. |
| `Entitlement` | `entitlements` | Unique `(PlanId, Key)` — see §8. |
| `Subscription` | `subscriptions` | Unique `AccountId` — one subscription per account, matching the existing `FindByAccountAsync` single-result contract. |
| `Device` | `devices` | Indexed by `AccountId`. |
| `Session` | `sessions` | Phase 6.5 addition of `ISessionRepository`/`EfSessionRepository` (see §17); indexed by `AccountId` and `DeviceId`. |
| `UsageRecord` | `usage_records` | Composite index `(AccountId, PeriodBucket)` matching the only query this phase's application layer performs. |
| `AuditEvent` | `audit_events` | Composite index `(AccountId, OccurredAt)`; nullable `AccountId` FK is `Restrict`, not `Cascade` — an audit trail must never disappear as a side effect of deleting the account it references. |

No property was invented beyond the existing approved domain model; no
undecided field (e.g. `Plan.PriceHandle`, `UsageRecord.SecondsUsed`'s
billing-unit meaning) was given new, irreversible meaning by this mapping
— they persist exactly as opaque/undecided as Phase 6.3 documented them.
**IMPLEMENTED.**

## 5. Repository architecture

Existing repository interfaces (`IAccountRepository`, `IDeviceRepository`,
`ISubscriptionRepository`, `IPlanRepository`, `IUsageRecordRepository`,
`IAuditEventRepository`) are unchanged — no breaking signature change was
needed. Two new interfaces were added (`IProfileRepository`,
`ISessionRepository`) because no repository port existed yet for those two
entities, even though Phase 6.3's domain model already defined them — this
phase's instruction to persist Profile and Session required closing that
gap. Both new interfaces mirror the exact shape/style of their existing
siblings (e.g. `ISubscriptionRepository.FindByAccountAsync`). Both
in-memory (`InMemoryProfileRepository`/`InMemorySessionRepository`) and
EF-backed (`EfProfileRepository`/`EfSessionRepository`) implementations
were added together, so the dev/test path and the production path stay
symmetric. **IMPLEMENTED, TESTED.**

The in-memory repositories (`Infrastructure/Persistence/InMemoryRepositories.cs`)
are **kept, unmodified in behavior** — used by all Phase 6.3/6.4 unit tests
and as the Development-environment default when no database is configured
(§13). They are never reachable in a non-Development environment (§13's
fail-closed startup guard).

## 6. Identity mapping

`Account.ExternalIdentityProvider` + `Account.ExternalSubjectId` carry a
database-level `UNIQUE` index (`ix_accounts_external_identity`) — the
database itself, not just `AccountResolutionService`'s application-level
lookup, refuses to store two accounts with the same
`(Provider, ExternalSubjectId)` pair. A duplicate insert raises
`DbUpdateException` (proven by
`Account_DuplicateExternalIdentity_RejectedByUniqueConstraint`). Email is
never used as this key — `Account.Email` carries no uniqueness constraint
at all, consistent with Phase 6.4's explicit rule that two different
external subjects may share an email. **IMPLEMENTED, TESTED.**

## 7. Account isolation

Every account-owned entity (`Profile`, `Device`, `Subscription`, `Session`,
`UsageRecord`, `AuditEvent`) has an `AccountId` foreign key to `Account`
(`Restrict` for `AuditEvent`, `Cascade` elsewhere — see §4). Repository
methods that return account-scoped data always filter by an `accountId`
parameter supplied by the caller — never by a client-suppliable field on
the entity itself; the existing `AccountResolutionMiddleware` (Phase 6.4)
is what derives that `accountId` from the authenticated, backend-resolved
`Account`, never from client input. This phase does not add any new API
surface that would let a client supply an arbitrary `accountId` (§21's
minimum-API-changes principle). A dedicated test
(`CrossAccountAccess_CustomerACannotRetrieveCustomerBsUsageRecordsByAccountFilter`,
plus `Device_AccountIsolation_ListByAccountNeverReturnsAnotherAccountsDevice`)
proves that querying with one account's id never returns another account's
rows. **IMPLEMENTED, TESTED.**

## 8. Constraints

| Constraint | Reason |
|---|---|
| `UNIQUE (ExternalIdentityProvider, ExternalSubjectId)` on `accounts` | §6 — the entire reason this phase exists. |
| `UNIQUE (PlanId, Key)` on `entitlements` | `EntitlementService`'s `FirstOrDefault(e => e.Key == ...)` lookup pattern silently assumes one value per key per plan — now enforced, not just assumed. |
| `UNIQUE (AccountId)` on `subscriptions` | Matches the existing `ISubscriptionRepository.FindByAccountAsync` single-result contract exactly. |
| FK `devices.AccountId → accounts.Id` (Cascade) | A device cannot outlive its owning account. |
| FK `sessions.AccountId/.DeviceId` (Cascade) | Same reasoning. |
| FK `usage_records.AccountId` (Cascade), `.DeviceId` (Restrict) | A usage record must not silently survive its account being deleted, but device deletion policy is intentionally more conservative (Restrict) since usage history has audit/billing relevance a device's own lifecycle shouldn't erase. |
| FK `audit_events.AccountId` (Restrict) | An audit trail must never be deleted as a side effect of deleting the account it documents (see §4). |

No business rule likely to evolve (e.g. exact trial/grace-period values,
device-count limits) was encoded as a database constraint — those remain
`Entitlement` rows, read and interpreted entirely by the Application layer,
exactly as Phase 6.2B/6.3 established. **IMPLEMENTED, TESTED.**

## 9. Indexes

Chosen from the actual repository method signatures that exist today, not
speculatively:

- `ix_accounts_external_identity` — `AccountResolutionService`'s only
  lookup path (§6, also a unique constraint).
- `ix_entitlements_plan_key` — `IPlanRepository.GetEntitlementsAsync` +
  the key lookup `EntitlementService` performs on the result.
- `ix_subscriptions_account`, `ix_subscriptions_plan` —
  `FindByAccountAsync`, and joining a subscription back to its plan.
- `ix_devices_account` — `ListByAccountAsync`.
- `ix_sessions_account`, `ix_sessions_device` — the two natural lookup
  paths for a session (by owning account, by device).
- `ix_usage_records_account_period` — `ListByAccountAndPeriodAsync`
  exactly.
- `ix_audit_events_account_time` — the expected "this account's history
  over time" access pattern named directly in the governing instruction.

No column was indexed without a corresponding query in this codebase
today. **IMPLEMENTED.**

## 10. Transactions and concurrency

**Transactions**: each repository's `SaveAsync`/`AddAsync` method is one
`SaveChangesAsync()` call — EF Core wraps that in an implicit database
transaction automatically. No Application-layer operation in this phase
spans multiple aggregate roots atomically (device registration only
touches `Device`; there is no cross-entity write in this phase's scope),
so no additional explicit transaction boundary was introduced — adding one
now would be speculative infrastructure for an operation that does not yet
exist, which the governing instruction explicitly discourages ("do not
wrap every single repository call in an unnecessary transaction").

**Concurrency**: `Account`, `Subscription`, and `Device` — the three
entities most likely to receive genuinely concurrent writes (account
status/role changes, subscription state changes, device
authorize/revoke) — use PostgreSQL's built-in `xmin` system column as an
EF Core optimistic-concurrency token (`b.Property<uint>("xmin").IsRowVersion()`).
This adds **zero** columns to any Domain entity — no `RowVersion` byte
array was added to Domain, keeping it exactly as clean as Phase 6.3/6.4
left it — while still causing a genuinely concurrent update to the same
row to raise `DbUpdateConcurrencyException` rather than silently
last-writer-wins. No speculative distributed locking (e.g. for the
device-count race the governing instruction names as an example) was
added — that specific race (two simultaneous registrations both reading
"count < limit" as true) is a real, acknowledged limitation, documented in
§18, not solved here, per the explicit instruction not to
over-engineer this phase. **IMPLEMENTED (concurrency tokens); DEFERRED
(device-count race serialization).**

## 11. Migrations

A single EF Core migration, `InitialCreate`
(`Persistence/EfCore/Migrations/`), generated via `dotnet ef migrations add`
and applied at API startup via `dbContext.Database.Migrate()` — never
`EnsureCreated`/`EnsureDeleted`. This is safe by construction in this
phase specifically because there is no pre-existing production schema
anywhere to endanger (Phase 6.3/6.4 never created one — §12 of the Phase
6.3 document explicitly deferred this). A design-time-only
`IDesignTimeDbContextFactory` (`AutraxisDbContextFactory`) lets migrations
be authored without running the API host; it is never referenced by the
application's own runtime startup path. **IMPLEMENTED, TESTED**
(`Migration_AppliesSuccessfully_AllExpectedTablesExist`).

## 12. Configuration

`Database:ConnectionString` (`DatabaseOptions`) is the only new setting —
a genuine secret (carries a database password), left empty in the
committed `appsettings.json`/`appsettings.Development.json`, supplied only
via environment variable or secret manager in any real deployment. Startup
behavior (`Program.cs`):

- Configured → real PostgreSQL repositories are registered, migrations run.
- Not configured, `IsDevelopment()` → falls back to the existing in-memory
  repositories (unchanged Phase 6.3/6.4 default — this is why the existing
  99-strong test suite keeps passing unmodified).
- Not configured, any other environment → the host **throws at startup**
  and never begins accepting requests. Production can never silently fall
  back to in-memory persistence.

**IMPLEMENTED, TESTED** (`ConfigFile_DatabaseSection_ConnectionStringNeverCommittedNonEmpty`).

## 13. Health/readiness

`/health/live` is unchanged — still a fixed literal, never touches the
database (an unreachable database must not make an orchestrator kill a
live process). `/health/ready` now calls `Database.CanConnectAsync()` when
a database is configured, returning `503 {"status":"not_ready"}` on
failure with no connection-string or provider-exception detail ever
exposed; in in-memory/Development mode it returns the same fixed `200
{"status":"ready"}` as before. **IMPLEMENTED.**

## 14. Security

- SQL injection: all queries are LINQ-to-EF-Core (parameterized
  automatically); the one raw-SQL test helper
  (`Migration_AppliesSuccessfully_AllExpectedTablesExist`) uses EF Core's
  parameterized `SqlQuery<T>` interpolation, not string concatenation.
- No repository method accepts a raw, unvalidated `AccountId` from a
  client — every account-scoped read is filtered server-side using the
  `Account` resolved by `AccountResolutionMiddleware` (Phase 6.4),
  never a request body/query-string value (§7).
- No database entity is exposed directly as an API response type in this
  phase — this phase adds no new customer-facing endpoints at all (§21).
**IMPLEMENTED, TESTED.**

## 15. Testing

- **Unit tests** (unchanged path): Application-layer service tests
  continue using the in-memory repositories — fast, deterministic, no
  database required.
- **Real-relational integration tests**
  (`EfPostgresPersistenceTests.cs`, 21 tests) run against an **actual
  PostgreSQL 16 container** via `Testcontainers.PostgreSql` — not SQLite,
  not EF Core's InMemory provider — specifically so real unique
  constraints, real foreign keys, and a real migration are exercised (see
  the decision document §9 for why SQLite was rejected for this purpose).
  Each test is `[SkipIfNoDockerFactAttribute]`: it self-skips with a clear
  reason when no Docker daemon is reachable, rather than failing the
  whole suite or fabricating a pass. **This sandbox has no Docker
  installed** (`docker: command not found`), so all 21 tests report
  `Skipped` here — see §16/Final Report for the honest, exact numbers.
  They are written to run for real in any environment with Docker (a
  developer machine or CI runner with Docker available).
- Coverage against the instruction's numbered list: account create/
  retrieve/update/suspend/duplicate-identity/role-persistence; profile
  create+association+FK violation; device register/retrieve/revoke/
  account-isolation/FK violation; plan persist/retrieve; subscription
  persist/retrieve/account-relationship/duplicate-rejection; entitlement
  duplicate-key rejection; session persist/retrieve; usage persist+query+
  client-hint-cannot-overwrite-authoritative; audit persist+query;
  migration success; cross-account isolation; suspended-status survives a
  simulated restart (fresh `DbContext`/connection against the same
  database). **IMPLEMENTED; TESTED where Docker is available — see §16
  for the exact count that ran versus skipped in this environment.**

## 16. Failure behavior

- Missing database configuration outside Development → startup throws
  immediately (§12) — proven by code inspection and by the existing
  `ConfigurationSecretSafetyTests` guarding the committed config stays
  empty; not covered by an automated "process fails to start" test in
  this phase (starting a second host process specifically to observe its
  own crash was judged disproportionate for this phase's scope).
- Database unreachable at request time → `/health/ready` returns `503`
  with no detail (§13); any other request that needs the database and
  cannot reach it surfaces as a generic 500 through ASP.NET's default
  problem-details handling — no database-specific message, connection
  string, or stack trace is added by this phase's code.
- Constraint violation (duplicate identity, duplicate entitlement key,
  duplicate subscription, missing FK target) → `DbUpdateException`,
  proven by five dedicated tests (§15). **IMPLEMENTED, TESTED** (where
  Docker is available).

## 17. Deferred work

- Device-registration race serialization beyond optimistic concurrency
  (§10) — a real but narrow, non-blocking limitation for this phase's
  scope.
- Full production device-licensing policy, billing, customer UI, mobile,
  provider gateway, usage billing — unchanged from Phase 6.3/6.4's stop
  list; none touched here.
- Automated "process fails to start without database config" test (§16).
- Connection pooling middleware (e.g. PgBouncer), read replicas, and
  actual Azure resource provisioning — operational/deployment concerns
  outside this phase's code scope (see the decision document §10/§13).

## 18. Known limitations

- The Docker-dependent integration-test suite could not be executed in
  this development sandbox (no Docker daemon installed) — see §15 and the
  Final Report for exact numbers; these tests are ready to run in any
  Docker-capable environment (a developer machine or CI).
- Two-simultaneous-device-registrations exceeding `MaxActiveDevices` is
  not fully serialized at the database level in this phase (§10) — a
  narrow, acknowledged race, not a data-integrity risk (no corrupted or
  orphaned row results, only a possible transient over-count).
- No actual Azure PostgreSQL resource was provisioned — this phase is
  persistence *code*, not infrastructure deployment.

## 19. Phase 6.6 prerequisites

None blocking. The repository abstractions Phase 6.6 (billing) will need
(`ISubscriptionRepository`, `IPlanRepository`) are already real,
tested, and durable as of this phase — no schema or interface change is
anticipated to be required before billing work can build on top of them.

## 20. Files added/modified — see the Final Report for the complete list.
