# Development/UAT Environment Configuration

This document lists exactly which environment variables the Windows client and the
AUTRAXIS backend read at runtime, and where each is read from in source. **No real
tenant ID, client ID, key, secret, or URL is given here — every example below is an
illustrative placeholder only.** Neither the client nor the backend loads a `.env`
file — both read real OS environment variables directly (`Environment.GetEnvironmentVariable`),
so there is nothing to "load"; a value only takes effect once it's actually set in
the environment the process launches from, then the process is restarted.

## Client (`VTTranslate.App`)

Read by `AuthenticationOptions.FromEnvironment()` (`src/VTTranslate.App/Authentication/AuthenticationOptions.cs`),
process-scope environment only. All four are non-secret (public client metadata, not
credentials) but must be real values from an actual provisioned Entra External ID
tenant + app registration for sign-in to succeed — see
`docs/phase-7.1-customer-authentication-client-and-entra-integration.md` for the
architecture these values plug into.

| Variable | Meaning | Example shape (placeholder only) |
|---|---|---|
| `AUTRAXIS_CUSTOMER_AUTHORITY` | The tenant's OIDC issuer URL | `https://<tenant-name>.ciamlogin.com/<tenant-id>/v2.0` |
| `AUTRAXIS_CUSTOMER_CLIENT_ID` | The registered public client application's ID | `<guid>` |
| `AUTRAXIS_CUSTOMER_API_SCOPE` | The delegated scope the client requests for the AUTRAXIS API | `api://<api-app-id>/access_as_user` |
| `AUTRAXIS_API_BASE_URL` | Where the AUTRAXIS backend is reachable | `https://localhost:7237` (local dev, per `src/VTTranslate.Backend.Api/Properties/launchSettings.json`) or a real deployed URL |

Separately, `AppSettings` (`src/VTTranslate.Core/Config/AppSettings.cs`) reads
`AZURE_SPEECH_KEY`/`AZURE_SPEECH_REGION` — **these are no longer required to start an
authenticated production session** (Phase 8C; see `SessionValidator`'s own doc
comment). They remain relevant only to non-production experimental/live-test code
(`VTTranslate.Core.Tests`, `tools/VTTranslate.LiveTest`, `VTTranslate.Core/Streaming`)
that talks to Azure directly outside the authenticated customer path.

## Backend (`VTTranslate.Backend.Api`)

Bound from the `Identity`/`ProviderCredentials` configuration sections (environment
variables or an untracked `appsettings.Development.json` — never a committed value;
see `appsettings.json`'s own `_comment` fields).

| Key | Meaning |
|---|---|
| `Identity__Authority` (or `Identity:Authority` in a JSON config file) | Same tenant's OIDC issuer URL as the client's `AUTRAXIS_CUSTOMER_AUTHORITY` |
| `Identity__Audience` | This API's own application/client ID (the token audience the backend validates against) |
| `ProviderCredentials__AzureSpeech__SubscriptionKey` | The long-lived Azure Cognitive Services master key — **a secret**, backend-only, never sent to the client (Phase 6.8) |
| `ProviderCredentials__AzureSpeech__Region` | The Azure Speech resource's region — not a secret |

Local dev URLs (already committed, not invented here): `http://localhost:5067` /
`https://localhost:7237` (`src/VTTranslate.Backend.Api/Properties/launchSettings.json`).
With no `Database:ConnectionString` set and `ASPNETCORE_ENVIRONMENT=Development`, the
backend runs entirely in-memory — no Docker/PostgreSQL required for basic local
startup (`Program.cs`).

## What this document does NOT provide

No tenant has been provisioned by this document. Setting these variables to
placeholder or made-up values will not make sign-in work — a real Entra External ID
tenant and app registration must exist first (see
`docs/windows-mvp-production-readiness-uat.md` and the Phase 8B discovery report for
the current provisioning status).
