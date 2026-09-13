# Phase 6.5 — CI-Based Database Verification

## Why this exists

Phase 6.5 added 21 real-PostgreSQL integration tests
(`tests/VTTranslate.Backend.Tests/EfPostgresPersistenceTests.cs`), run via
`Testcontainers.PostgreSql`, which requires a working Docker daemon. Two
independent environments used during Phase 6.5's own implementation and
review (the local development sandbox, and a remote cloud verification
agent) both lack Docker, so those 21 tests could only self-skip
(`[SkipIfNoDockerFactAttribute]`) rather than actually execute — leaving
Phase 6.5's real-database behavior implemented and reviewed, but not yet
verified by an actual run against PostgreSQL.

This workflow closes that gap using a Docker-capable **hosted** runner
instead of changing the tests, the persistence code, or the database
technology in any way.

## What was added

`.github/workflows/phase-6.5-db-verification.yml` — a single GitHub
Actions workflow, scoped to exactly one job:

1. Check out the repository.
2. Install .NET 8.
3. Confirm Docker is present (`docker version`) — GitHub's `ubuntu-latest`
   hosted runners ship with a running Docker daemon by default, which is
   exactly what `Testcontainers.PostgreSql` needs.
4. `dotnet build VTTranslate.sln` (Release).
5. `dotnet test` the backend suite — **unmodified**, no test filter, no
   environment variable disabling Docker, no substitute connection string.
   The same `postgres:16-alpine` container Testcontainers starts locally
   is started here too.
6. `dotnet test` the existing `VTTranslate.Core.Tests` suite (399-test
   baseline).
7. Upload the `.trx` result files as a build artifact, so exact per-test
   pass/skip/fail status is inspectable after the run, not just a
   pass/fail summary.

Triggers: push/PR to `main`/`master`, plus `workflow_dispatch` (manual
run) so this specific verification can be re-run on demand without
needing a new commit.

## What was deliberately NOT done

- No change to `EfPostgresPersistenceTests.cs`, `SkipIfNoDockerFactAttribute.cs`,
  `AutraxisDbContext`, the EF configurations, or any other Phase 6.5
  application/persistence code.
- No SQLite, EF Core InMemory provider, mock repository, or any other
  substitute for real PostgreSQL — the workflow's only job is making a
  real Docker daemon available; the tests decide for themselves (via
  Testcontainers) what to do with it, exactly as they already did.
- No self-hosted runner, no Docker-in-Docker layer, no new database
  vendor — `ubuntu-latest`'s already-running Docker daemon is used as-is.
- No deployment, publishing, or release step — this workflow only verifies.
- No Phase 6.6 (or any other future-phase) work.

## How to get a result from it

This workflow needs to actually run on GitHub's infrastructure to produce
results — an agent cannot fabricate what a hosted runner would report.
Once this repository has a GitHub remote and this workflow file is
pushed (or `workflow_dispatch`-triggered) there, the **Actions** tab on
GitHub — or `gh run list` / `gh run view --log` — shows the exact
pass/skip/fail counts for both test suites, plus the downloadable `.trx`
artifacts for per-test detail.
