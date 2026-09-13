using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using VTTranslate.Backend.Api.Security;
using VTTranslate.Backend.Application.Authorization;
using VTTranslate.Backend.Application.Devices;
using VTTranslate.Backend.Application.Entitlements;
using VTTranslate.Backend.Application.Identity;
using VTTranslate.Backend.Application.Usage;
using VTTranslate.Backend.Domain.Abstractions;
using VTTranslate.Backend.Domain.Enums;
using VTTranslate.Backend.Infrastructure.Billing;
using VTTranslate.Backend.Infrastructure.Identity;
using VTTranslate.Backend.Infrastructure.Persistence;
using VTTranslate.Backend.Infrastructure.Persistence.EfCore;
using VTTranslate.Backend.Infrastructure.Time;

var builder = WebApplication.CreateBuilder(args);

// ---- Configuration boundary ----
// Neither Authority nor Audience is a secret (see EntraIdentityOptions's own doc
// comment) — no client secret is required anywhere in this phase because this API only
// VALIDATES bearer tokens; it never acquires them. Real values come from
// appsettings.{Environment}.json / environment variables / a secret manager in a real
// deployment — never a committed value (see appsettings.json's own placeholder
// comment). Missing/empty configuration does NOT result in fake successful
// authentication: the real JwtBearer handler fails every authentication attempt closed
// (401) if it cannot resolve valid signing/issuer/audience configuration — see
// docs/phase-6.4-entra-authentication.md §14 for why this is safe by construction
// rather than by an extra guard this code would otherwise need to add.
var identityOptions = builder.Configuration.GetSection(EntraIdentityOptions.SectionName).Get<EntraIdentityOptions>()
                       ?? new EntraIdentityOptions();
if (string.IsNullOrWhiteSpace(identityOptions.Authority) || string.IsNullOrWhiteSpace(identityOptions.Audience))
{
    // Informational only — never blocks startup (tests and local dev may not have
    // real Entra configuration; the JwtBearer handler's own behavior below still fails
    // every real authentication attempt closed regardless of this log line).
    Console.WriteLine("[startup] Identity:Authority/Identity:Audience are not configured — authentication will fail closed for every request until configured.");
}

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = identityOptions.Authority;
        options.Audience = identityOptions.Audience;
        options.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
        // Without this, JwtBearerHandler silently remaps short claim types ("sub" ->
        // a legacy XML-namespace URI, etc. — the historical WIF claim-type mapping) on
        // the resulting ClaimsPrincipal. EntraIdentityProvider reads the standard OIDC
        // claim names directly ("sub", "email", "iat", "exp"), so mapping must be
        // disabled — otherwise every token would fail identity-claim extraction despite
        // passing cryptographic validation, which is exactly the kind of silent,
        // hard-to-diagnose fail-closed-for-the-wrong-reason bug this line prevents.
        options.MapInboundClaims = false;
        // ValidateIssuer / ValidateAudience / ValidateLifetime / ValidateIssuerSigningKey
        // all default to true in TokenValidationParameters — left at their standard,
        // Microsoft-supported defaults deliberately (see Phase 6.4 instruction: "do not
        // implement custom cryptographic JWT validation... use standard
        // framework-supported validation"). Signing-key rotation is handled
        // automatically by the framework's own OIDC metadata refresh (ConfigurationManager),
        // not by any code in this repository.

        options.Events = new JwtBearerEvents
        {
            // SAFE LOGGING ONLY — never logs the token, its claims, or the Authorization
            // header itself (Phase 6.4 instruction N). "ex" here is the validation
            // exception's TYPE/message from the framework (e.g. "token expired"), not
            // token content.
            OnAuthenticationFailed = context =>
            {
                context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                    .CreateLogger("Authentication")
                    .LogInformation("Bearer authentication failed: {Reason}", context.Exception.GetType().Name);
                return Task.CompletedTask;
            },
        };
    });

builder.Services.AddAuthorization();

// ---- Dependency injection: domain abstractions -> infrastructure implementations ----
builder.Services.AddSingleton<IClock, SystemClock>();

// ---- Phase 6.5: persistence boundary ----
// Database:ConnectionString is a SECRET (carries a database password) and is never
// committed — see DatabaseOptions's own doc comment and appsettings.json. Its presence
// or absence decides which repository implementations are registered:
//   - configured  -> real PostgreSQL-backed repositories (production path).
//   - missing, Development environment -> in-memory repositories (local dev/test only,
//     exactly as Phase 6.3/6.4 already did — unchanged default for this repository's
//     existing test suite).
//   - missing, any OTHER environment (Staging/Production/unset-but-not-Development)
//     -> FAIL STARTUP. Production must never silently fall back to in-memory
//     persistence (Phase 6.5 instruction, "Database Configuration" section).
var databaseOptions = builder.Configuration.GetSection(DatabaseOptions.SectionName).Get<DatabaseOptions>()
                       ?? new DatabaseOptions();
var databaseConfigured = !string.IsNullOrWhiteSpace(databaseOptions.ConnectionString);

if (!databaseConfigured && !builder.Environment.IsDevelopment())
{
    // Fail fast, before the host ever starts accepting requests. No connection string,
    // no database details, no exception detail beyond this fixed message is logged.
    throw new InvalidOperationException(
        "Database:ConnectionString is not configured. Refusing to start outside the " +
        "Development environment rather than silently falling back to in-memory, " +
        "non-durable persistence.");
}

if (databaseConfigured)
{
    builder.Services.AddDbContext<AutraxisDbContext>(options =>
        options.UseNpgsql(databaseOptions.ConnectionString));

    builder.Services.AddScoped<IAccountRepository, EfAccountRepository>();
    builder.Services.AddScoped<IProfileRepository, EfProfileRepository>();
    builder.Services.AddScoped<IDeviceRepository, EfDeviceRepository>();
    builder.Services.AddScoped<ISubscriptionRepository, EfSubscriptionRepository>();
    builder.Services.AddScoped<IPlanRepository, EfPlanRepository>();
    builder.Services.AddScoped<IUsageRecordRepository, EfUsageRecordRepository>();
    builder.Services.AddScoped<IAuditEventRepository, EfAuditEventRepository>();
    builder.Services.AddScoped<ISessionRepository, EfSessionRepository>();
}
else
{
    Console.WriteLine("[startup] Database:ConnectionString is not configured — using in-memory, non-durable repositories (Development environment only).");

    builder.Services.AddSingleton<IAccountRepository, InMemoryAccountRepository>();
    builder.Services.AddSingleton<IProfileRepository, InMemoryProfileRepository>();
    builder.Services.AddSingleton<IDeviceRepository, InMemoryDeviceRepository>();
    builder.Services.AddSingleton<ISubscriptionRepository, InMemorySubscriptionRepository>();
    builder.Services.AddSingleton<InMemoryPlanRepository>();
    builder.Services.AddSingleton<IPlanRepository>(sp => sp.GetRequiredService<InMemoryPlanRepository>());
    builder.Services.AddSingleton<IUsageRecordRepository, InMemoryUsageRecordRepository>();
    builder.Services.AddSingleton<IAuditEventRepository, InMemoryAuditEventRepository>();
    builder.Services.AddSingleton<ISessionRepository, InMemorySessionRepository>();
}

// Phase 6.4: real identity provider (claims-mapping only — see EntraIdentityProvider's
// own doc comment for why cryptographic verification lives in the JwtBearer middleware
// above, not here).
builder.Services.AddSingleton<IIdentityProvider, EntraIdentityProvider>();
builder.Services.AddSingleton<IAccountResolutionService, AccountResolutionService>();

// Billing remains a deliberate placeholder — out of scope for Phase 6.4 (see
// docs/phase-6.4-entra-authentication.md "Deferred work").
builder.Services.AddSingleton<IBillingProvider, NotImplementedBillingProvider>();

builder.Services.AddSingleton<IAuthorizationService, AuthorizationService>();
builder.Services.AddSingleton<IDeviceRegistrationService, DeviceRegistrationService>();
builder.Services.AddSingleton<IUsageService, UsageService>();
builder.Services.AddSingleton<IEntitlementService, EntitlementService>();

var app = builder.Build();

if (databaseConfigured)
{
    // Deterministic schema creation via real EF Core migrations — never
    // EnsureCreated/EnsureDeleted (Phase 6.5 instruction, "Migrations" section). Applying
    // migrations here (rather than requiring a separate manual step) keeps this phase's
    // verification reproducible; it is purely additive (a single InitialCreate migration
    // against what is, today, a brand-new schema — no existing production data exists to
    // put at risk).
    using var migrationScope = app.Services.CreateScope();
    migrationScope.ServiceProvider.GetRequiredService<AutraxisDbContext>().Database.Migrate();
}

// ---- Health / readiness (anonymous — no authentication required for monitoring) ----
// Liveness deliberately does NOT touch the database — a slow/unreachable database must
// not make the process report itself dead and get killed/restarted by an orchestrator;
// that is exactly what readiness is for.
app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
app.MapGet("/health/ready", async (IServiceProvider services) =>
{
    if (!databaseConfigured)
    {
        // In-memory mode (Development only, per the startup guard above) — nothing to
        // check; "ready" reflects the environment's own deliberate configuration.
        return Results.Ok(new { status = "ready" });
    }

    try
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AutraxisDbContext>();
        var canConnect = await db.Database.CanConnectAsync();
        return canConnect
            ? Results.Ok(new { status = "ready" })
            : Results.Json(new { status = "not_ready" }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    catch
    {
        // Never leak connection details, provider exception text, or a stack trace.
        return Results.Json(new { status = "not_ready" }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

// ---- Authentication / AUTRAXIS account resolution / authorization pipeline ----
// Order matters: Authentication (real JwtBearer — verifies the token) -> AUTRAXIS
// account resolution (this repository's own middleware — resolves the AUTRAXIS Account
// and its Status/Role; never touches Entra claims for role) -> Authorization (ASP.NET's
// own RequireAuthorization() policy evaluation, which only checks "is this request
// authenticated at all" — the deeper CUSTOMER/ADMIN/SUPER_ADMIN check happens via
// RequireAutraxisRole() endpoint filters, AFTER this point, reading the Account this
// middleware placed in HttpContext.Items).
app.UseAuthentication();
app.UseAutraxisAccountResolution();
app.UseAuthorization();

// ---- API foundation: contract placeholders only, now behind real authentication ----
// Every route below is EXPLICITLY a not-yet-implemented contract placeholder (Phase
// 6.3) — Phase 6.4 adds ONLY the authentication/authorization boundary in front of
// them; no placeholder becomes real functionality in this phase.
NotImplementedPlaceholder(app, "/account").RequireAuthorization();
NotImplementedPlaceholder(app, "/devices").RequireAuthorization();
NotImplementedPlaceholder(app, "/subscription").RequireAuthorization();
NotImplementedPlaceholder(app, "/entitlements").RequireAuthorization();
NotImplementedPlaceholder(app, "/usage").RequireAuthorization();

// Phase 6.4 AUTHORIZATION-BOUNDARY PROOF ONLY — not a product feature. Exists solely so
// the CUSTOMER-vs-ADMIN-vs-SUPER_ADMIN role gate can be exercised end-to-end over real
// HTTP in an integration test (see VTTranslate.Backend.Tests). Still returns the same
// 501 placeholder body as every other route once past the role gate — no functionality
// is implemented here either.
NotImplementedPlaceholder(app, "/internal/diagnostics")
    .RequireAuthorization()
    .RequireAutraxisRole(Role.SuperAdmin);

app.Run();

static RouteHandlerBuilder NotImplementedPlaceholder(WebApplication app, string routePrefix) =>
    app.Map(routePrefix + "/{**catchAll}", () => Results.Json(
        new
        {
            status = "not_implemented",
            phase = "6.3",
            reason = $"'{routePrefix}' is a contract placeholder only — no production functionality exists yet.",
        },
        statusCode: StatusCodes.Status501NotImplemented));

/// <summary>Exposed for VTTranslate.Backend.Tests' WebApplicationFactory-based integration tests (health, placeholder, and authentication/authorization boundary tests).</summary>
public partial class Program;
