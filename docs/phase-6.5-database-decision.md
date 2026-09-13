# Phase 6.5 — Database Decision

## 1. Requirements

Derived from the approved Phase 6 documents and the actual domain model
(`Account`, `Profile`, `Plan`, `Subscription`, `Entitlement`, `Device`,
`Session`, `UsageRecord`, `AuditEvent`):

- Strong relational integrity — every non-`Account` entity above is
  owned by exactly one `Account` (FK relationships), and identity
  mapping (`ExternalIdentityProvider` + `ExternalSubjectId` → `Account`)
  must be enforced as unique at the storage layer, not just in
  application code.
- ACID transactions for multi-row consistency (subscription/entitlement
  state changes, future billing-webhook handling).
- Real, repeatable schema migrations — no `EnsureCreated`/`EnsureDeleted`
  in any environment that matters.
- .NET 8 first-class support, since the backend (Domain/Application/
  Infrastructure/Api) already deliberately targets plain `net8.0` (not
  `net8.0-windows`) specifically so it can run on non-Windows
  infrastructure (`docs/phase-6.3-backend-foundation.md` §1) — this is a
  hard portability constraint the database choice must respect.
- Reasonable operational cost for an early-stage SaaS product with
  unknown transaction volume.
- Low vendor lock-in — Phase 6.2's D3 raised Azure SQL vs. PostgreSQL but
  the product owner never approved a vendor (`docs/phase-6.3-backend-
  foundation.md` §12, `docs/phase-6.2b-resolved-architecture-decisions.md`
  §19). This decision must be made responsibly from the architecture
  itself, since the governing Phase 6.5 instruction explicitly permits
  that when the information available supports it.
- Testability without hiding real relational behavior.

## 2. Candidate databases considered

| Candidate | Notes |
|---|---|
| **PostgreSQL** | Open-source, mature relational engine; first-class .NET/EF Core support via Npgsql; runs identically on Azure (Azure Database for PostgreSQL Flexible Server), AWS, GCP, on-prem, or a developer's laptop; no per-core licensing. |
| **Azure SQL / SQL Server** | Mature, first-class .NET support (it's Microsoft's own database); tightly integrated with Azure tooling; T-SQL feature set exceeds what this domain model needs. |
| **A NoSQL/document store** (e.g. Cosmos DB) | Considered and rejected outright — the domain model (§4 below) is fundamentally relational (Account → Device/Subscription/UsageRecord/AuditEvent, Plan → Entitlement, Subscription → Plan), with several fields whose integrity depends on genuine foreign keys and a genuine unique constraint (external identity mapping). Modeling this in a document database would mean re-implementing relational integrity in application code — the opposite of what this phase is meant to establish. |

## 3. Comparison

| Criterion | PostgreSQL | Azure SQL / SQL Server |
|---|---|---|
| Relational integrity, FKs, transactions | Full support | Full support |
| .NET 8 / EF Core support | First-class (Npgsql.EntityFrameworkCore.PostgreSQL, actively maintained, tracks EF Core releases) | First-class (Microsoft's own provider) |
| Migrations | EF Core Migrations, identical workflow | EF Core Migrations, identical workflow |
| Concurrency control | Native `xmin` system column usable directly as an EF Core concurrency token — no extra column needed | Requires an explicit `rowversion`/`timestamp` column |
| Portability / vendor lock-in | Runs unmodified on any cloud or on-prem Postgres; matches this backend's existing deliberate `net8.0` (non-Windows-only) portability stance | Effectively ties the backend to Azure (or a licensed SQL Server instance) for the life of the product |
| Cost | Free engine; managed-Postgres pricing (e.g. Azure Flexible Server Burstable tier) is materially cheaper than Azure SQL DTU/vCore tiers at low/early volume | Azure SQL has a meaningfully higher cost floor, plus SQL Server licensing if ever run outside Azure |
| Operational complexity | Comparable — both are fully managed database-as-a-service offerings on Azure | Comparable |
| Local/dev/test story | Official Postgres Docker image, trivially reproducible in CI; Testcontainers has a dedicated, well-maintained Postgres module | SQL Server Docker image exists but is heavier and Linux-container support has historically been less consistent than Postgres's |
| Azure compatibility | Fully supported first-party Azure service (Azure Database for PostgreSQL) | Fully supported (it's Microsoft's own product) |
| Backup/recovery | Automated backups, point-in-time restore on Azure Database for PostgreSQL Flexible Server — same operational tier as Azure SQL | Automated backups, point-in-time restore |
| Suitability for this domain's relationships | Directly suitable — nothing in the domain (Account/Subscription/Entitlement/Device/Usage/Audit) requires a SQL Server-specific feature | Also directly suitable |

Both engines are technically capable of everything this phase needs. The
deciding factors are portability (this backend's own architecture already
chose platform-neutrality — `net8.0`, not `net8.0-windows` — specifically
to avoid being locked to one OS/cloud) and cost, both of which favor
PostgreSQL, with no corresponding capability PostgreSQL lacks for this
domain model.

## 4. Selected database

**PostgreSQL** (targeted via Azure Database for PostgreSQL — Flexible
Server — in production, with the same engine used locally/in CI via the
official Docker image).

## 5. Why it was selected

- It is the only option that does not narrow the deployment story this
  backend has already deliberately kept open (Phase 6.3's explicit choice
  of `net8.0` over `net8.0-windows`, precisely so the backend isn't
  Windows/Azure-SQL-coupled by accident).
- Materially lower cost floor for an early-stage product with unknown
  transaction volume, with a clear, well-trodden upgrade path (larger
  Flexible Server tiers, read replicas) if volume grows.
- Native `xmin`-based optimistic concurrency (§10 of the implementation
  doc) means no synthetic `RowVersion` column needs to be added to any
  Domain entity — keeping Domain exactly as clean as Phase 6.3/6.4 left it.
- Excellent, actively maintained EF Core provider (Npgsql) with no
  meaningful capability gap against SQL Server for this domain's needs.
- No existing approved decision (Phase 6.2/6.2B) commits this project to
  Azure SQL — Phase 6.2's D3 raised it as one option among several and
  Phase 6.2B did not resolve D3 at all — so choosing PostgreSQL does not
  contradict or silently override any prior product-owner decision.

## 6. Why alternatives were rejected

- **Azure SQL / SQL Server**: technically adequate but not chosen, because
  choosing it would newly lock the project to Azure/SQL Server licensing
  for no corresponding functional benefit for this domain model, working
  against the portability stance the backend has already taken.
- **A NoSQL/document store**: rejected — see §2. The domain model's
  integrity requirements (unique external-identity mapping, FK-owned
  Account-scoped rows) are exactly what a relational database exists to
  enforce at the storage layer; recreating that in application code over a
  document store would be strictly worse and riskier.
- **SQLite**: considered only as a *test* database, and rejected for that
  purpose too — see the implementation document's Testing section. Using
  it as the actual production engine was never seriously considered: it
  has no real concurrent-write story and no managed-cloud-hosting option
  suitable for a multi-customer SaaS backend.

## 7. ORM / data-access decision

**Entity Framework Core 8**, via `Npgsql.EntityFrameworkCore.PostgreSQL`.
Justification: it is the standard, Microsoft-supported .NET data-access
technology, already assumed by this repository's own dependency-discipline
convention (Phase 6.4 §T: "Microsoft-supported/current compatible
packages... avoid unnecessary dependencies... no convenience packages when
framework functionality suffices" — Dapper/raw ADO.NET would mean hand
-writing SQL and mapping for nine entities with no offsetting benefit; EF
Core's migration tooling is also required by this phase's own instruction).
All EF-specific configuration (the `DbContext`, `IEntityTypeConfiguration<T>`
classes, the Npgsql provider registration) lives in
`VTTranslate.Backend.Infrastructure` only — Domain entities are plain
POCOs with zero EF Core references, zero attributes, and no base class.

## 8. Migration strategy

EF Core Migrations (`dotnet ef migrations add`), applied via
`dbContext.Database.Migrate()` at startup — deterministic, versioned,
additive-only in this phase (a single `InitialCreate` migration; no
existing production schema to migrate away from, since Phase 6.3/6.4 never
created one). `EnsureCreated`/`EnsureDeleted` are never called anywhere in
this codebase. See the implementation document §12 for the exact startup
behavior and its safety guarantees.

## 9. Testing strategy

See the implementation document §16 for full detail. Summary: EF Core
repository/constraint/migration behavior is tested against a **real**
PostgreSQL instance via `Testcontainers.PostgreSql`, not SQLite — SQLite's
type system, constraint enforcement, and concurrency model differ from
PostgreSQL's in ways that would hide exactly the behaviors (unique
constraints, real transactions, `xmin` concurrency) this phase is meant to
prove. Existing in-memory repositories remain for fast unit tests of
Application-layer logic that doesn't need real persistence.

## 10. Deployment implications

Production deployment targets Azure Database for PostgreSQL (Flexible
Server), configured entirely through `Database:ConnectionString`
(environment/secret-manager supplied — never committed; see the
implementation document §13). No infrastructure-as-code or actual Azure
resource was provisioned in this phase — that is an operational/deployment
task outside this phase's scope (persistence *code*, not deployment).

## 11. Backup/recovery considerations

Azure Database for PostgreSQL Flexible Server provides automated daily
backups with point-in-time restore (standard managed-service capability,
same tier of guarantee Azure SQL would provide) — no custom backup logic
is implemented or required by this phase. Actual backup-retention/restore
configuration is an operational deployment concern, not addressed by
application code.

## 12. Vendor lock-in considerations

PostgreSQL is the specific choice made here to *minimize* lock-in relative
to Azure SQL: the wire protocol, SQL dialect, and Npgsql driver are
identical whether the database runs on Azure, another cloud, or self
-hosted, so a future infrastructure change (e.g. moving off Azure) would
not require an application rewrite — only a connection-string and
hosting-provider change.

## 13. Open questions

- Exact Azure Database for PostgreSQL tier/sizing — an operational,
  cost-driven decision outside this phase's engineering scope, to be made
  when actual production traffic estimates exist.
- Whether read replicas or connection pooling middleware (e.g. PgBouncer)
  are needed — not decided; premature at current (zero) production
  traffic, consistent with this phase's instruction not to prematurely
  optimize.
- Multi-region/data-residency requirements (GDPR/CCPA) remain, as flagged
  in `docs/phase-6.2b-resolved-architecture-decisions.md` §20, a
  legal/business question outside this document's authority.
