using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using VTTranslate.Backend.Api.Security;
using VTTranslate.Backend.Application.Authorization;
using VTTranslate.Backend.Application.Billing;
using VTTranslate.Backend.Application.Subscriptions;
using VTTranslate.Backend.Api.Subscriptions;
using VTTranslate.Backend.Application.Devices;
using VTTranslate.Backend.Application.Entitlements;
using VTTranslate.Backend.Application.Identity;
using VTTranslate.Backend.Application.ProviderAccess;
using VTTranslate.Backend.Application.Sessions;
using VTTranslate.Backend.Application.Usage;
using VTTranslate.Backend.Domain;
using VTTranslate.Backend.Domain.Abstractions;
using VTTranslate.Backend.Domain.Entities;
using VTTranslate.Backend.Domain.Enums;
using VTTranslate.Backend.Application.Profiles;
using VTTranslate.Backend.Infrastructure.Billing;
using VTTranslate.Backend.Infrastructure.Identity;
using VTTranslate.Backend.Infrastructure.Persistence;
using VTTranslate.Backend.Infrastructure.Persistence.EfCore;
using VTTranslate.Backend.Infrastructure.ProviderAccess;
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

// Phase 7.0 — separate admin/workforce authentication boundary
// (docs/phase-7.0-production-identity-and-account-lifecycle.md §12/§13). A completely
// separate, non-default JwtBearer scheme with its own Authority/Audience — never the
// customer "Identity:Authority"/"Identity:Audience" keys, never renamed, per the
// explicit instruction to preserve those unchanged. No admin API endpoint uses this
// scheme yet in Phase 7.0 (by design — see the master prompt's explicit "do not build a
// full admin API" instruction); the scheme+policy exist only so the boundary itself is
// safe and testable ahead of any future admin surface.
var adminIdentityOptions = builder.Configuration.GetSection(AdminIdentityOptions.SectionName).Get<AdminIdentityOptions>()
                            ?? new AdminIdentityOptions();
if (string.IsNullOrWhiteSpace(adminIdentityOptions.Authority) || string.IsNullOrWhiteSpace(adminIdentityOptions.Audience))
{
    Console.WriteLine("[startup] Identity:Admin:Authority/Identity:Admin:Audience are not configured — admin-scheme authentication will fail closed until configured.");
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
    })
    .AddJwtBearer(AdminIdentityOptions.SchemeName, options =>
    {
        options.Authority = adminIdentityOptions.Authority;
        options.Audience = adminIdentityOptions.Audience;
        options.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
        options.MapInboundClaims = false;
        options.Events = new JwtBearerEvents
        {
            OnAuthenticationFailed = context =>
            {
                context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                    .CreateLogger("AdminAuthentication")
                    .LogInformation("Admin-scheme bearer authentication failed: {Reason}", context.Exception.GetType().Name);
                return Task.CompletedTask;
            },
        };
    });

builder.Services.AddAuthorization(options =>
{
    // Explicit scheme selection, fail closed: this policy ONLY accepts a token
    // validated under the admin scheme above — a customer-audience token can never
    // satisfy it (wrong issuer/audience/signing key), and the default scheme's own
    // RequireAuthorization() policy is never consulted for a route using this policy.
    options.AddPolicy(AdminIdentityOptions.SchemeName, policy =>
    {
        policy.AuthenticationSchemes.Add(AdminIdentityOptions.SchemeName);
        policy.RequireAuthenticatedUser();
    });
});

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
    builder.Services.AddScoped<IBillingEventRepository, EfBillingEventRepository>();
    builder.Services.AddScoped<ITranslationSessionRepository, EfTranslationSessionRepository>();
    builder.Services.AddScoped<IUnitOfWork, EfUnitOfWork>();
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
    builder.Services.AddSingleton<IBillingEventRepository, InMemoryBillingEventRepository>();
    builder.Services.AddSingleton<ITranslationSessionRepository, InMemoryTranslationSessionRepository>();
    builder.Services.AddSingleton<IUnitOfWork, InMemoryUnitOfWork>();
}

// Phase 6.4: real identity provider (claims-mapping only — see EntraIdentityProvider's
// own doc comment for why cryptographic verification lives in the JwtBearer middleware
// above, not here).
builder.Services.AddSingleton<IIdentityProvider, EntraIdentityProvider>();

// Phase 6.6 correctness fix: each service below depends (directly or transitively) on
// at least one repository interface, which is registered Scoped when a real database is
// configured (Phase 6.5's EfXxxRepository registrations, above). A Singleton service
// cannot consume a Scoped one — the DI container throws the first time such a service is
// actually constructed. This was latent since Phase 6.4/6.5 (no HTTP endpoint actually
// invoked these services yet, so it never surfaced) and is fixed here, incidentally,
// because Phase 6.6 is the first phase to wire real endpoints that do. Registering these
// as Scoped is always safe regardless of which repository set (in-memory Singleton, or
// EF Scoped) is active. IAuthorizationService is DELIBERATELY EXCLUDED from this fix —
// AuthorizationService has zero constructor dependencies (pure logic over the Account
// parameter it's called with), so it has no scoped-repository lifetime mismatch to fix
// and remains Singleton, unchanged from Phase 6.4.
builder.Services.AddScoped<IAccountResolutionService, AccountResolutionService>();

// ---- Phase 7.0: gated JIT account provisioning, account lifecycle, profile ----
// AccountProvisioning:* is non-secret (see AccountProvisioningOptions's own doc
// comment). The rate limiter is registered Singleton because it deliberately holds
// small in-memory state ACROSS requests (that is its entire purpose) — see
// InMemoryProvisioningRateLimiter's own doc comment for why this is explicitly NOT a
// production-grade implementation for a multi-instance deployment.
var accountProvisioningOptions = builder.Configuration.GetSection(AccountProvisioningOptions.SectionName).Get<AccountProvisioningOptions>()
                                  ?? new AccountProvisioningOptions();
var provisioningRateLimitWindow = TimeSpan.FromSeconds(accountProvisioningOptions.RateLimitWindowSeconds > 0 ? accountProvisioningOptions.RateLimitWindowSeconds : 3600);

builder.Services.AddSingleton<IProvisioningRateLimiter, InMemoryProvisioningRateLimiter>();
builder.Services.AddScoped<IAccountProvisioningService>(sp => new AccountProvisioningService(
    sp.GetRequiredService<IAccountRepository>(),
    sp.GetRequiredService<IProfileRepository>(),
    sp.GetRequiredService<IAuditEventRepository>(),
    sp.GetRequiredService<IUnitOfWork>(),
    sp.GetRequiredService<IClock>(),
    sp.GetRequiredService<IProvisioningRateLimiter>(),
    accountProvisioningOptions.Enabled,
    accountProvisioningOptions.RequireEmailVerified,
    accountProvisioningOptions.RateLimitMaxAttempts,
    provisioningRateLimitWindow));

builder.Services.AddScoped<IAccountLifecycleService, AccountLifecycleService>();
builder.Services.AddScoped<IProfileService, ProfileService>();

// Billing remains a deliberate placeholder — out of scope for Phase 6.4 (see
// docs/phase-6.4-entra-authentication.md "Deferred work"). Phase 6.6 extends the
// interface (webhook verification) but does NOT provide a real implementation — no
// Paddle credentials, no live billing.
builder.Services.AddSingleton<IBillingProvider, NotImplementedBillingProvider>();

builder.Services.AddSingleton<IAuthorizationService, AuthorizationService>();
builder.Services.AddScoped<IDeviceRegistrationService, DeviceRegistrationService>();
builder.Services.AddScoped<IUsageService, UsageService>();
builder.Services.AddScoped<IEntitlementService, EntitlementService>();

// Phase 6.6: billing/subscription lifecycle — ISubscriptionLifecycleService is the sole
// writer of Subscription.Status/LastBillingEventAt/LastBillingEventPrecedence/
// LocallyCancelledAt/CancelAtPeriodEnd (see docs/phase-6.6-billing-subscription.md §9).
builder.Services.AddScoped<ISubscriptionLifecycleService, SubscriptionLifecycleService>();
builder.Services.AddScoped<IBillingWebhookProcessor, BillingWebhookProcessor>();

// ---- Phase 25: operator-controlled free-Trial provisioning (entitlement only — NO payment) ----
// Off by default. Subscriptions:Trial:{Enabled,PlanId} are non-secret. The customer endpoint
// (POST /subscription/trial) and its service are registered ONLY when an operator has enabled the
// Trial AND named a plan; otherwise no route exists and no subscription can be granted this way.
var trialOptions = builder.Configuration.GetSection(TrialProvisioningOptions.SectionName).Get<TrialProvisioningOptions>()
                   ?? new TrialProvisioningOptions();
if (trialOptions.Enabled && !trialOptions.IsUsable)
{
    Console.WriteLine("[startup] Subscriptions:Trial:Enabled is true but Subscriptions:Trial:PlanId is missing — Trial provisioning stays DISABLED (fail closed).");
}
// Reporting an existing trial's usage/period (My Account) is always available; only STARTING a trial is operator-gated.
builder.Services.AddScoped<ITrialUsageReporter, TrialUsageReporter>();
if (trialOptions.IsUsable)
{
    builder.Services.AddScoped<ISubscriptionProvisioningService>(sp => new SubscriptionProvisioningService(
        sp.GetRequiredService<IAccountRepository>(),
        sp.GetRequiredService<ISubscriptionRepository>(),
        sp.GetRequiredService<IPlanRepository>(),
        sp.GetRequiredService<IAuditEventRepository>(),
        sp.GetRequiredService<ISubscriptionLifecycleService>(),
        sp.GetRequiredService<IUnitOfWork>(),
        sp.GetRequiredService<IClock>(),
        trialOptions.PlanId!.Value));
}

// ---- Phase 6.8: provider-access gateway ----
// ProviderCredentials:{AzureSpeech,AzureTranslator}:{SubscriptionKey,Region} are SECRETS
// (the long-lived master keys) — always empty in committed config (see appsettings.json's
// own comment). Each provider's IProviderCredentialIssuer is registered ONLY when its own
// SubscriptionKey/Region are both configured — an unconfigured provider is simply never
// registered, so ProviderAccessGateway's issuer lookup naturally reports
// "unsupported provider" for it (never a fake/fabricated credential, never a fallback to
// a long-lived key). ProviderAccess:CredentialLifetimeSeconds is non-secret, defaults
// conservatively if unset — see ProviderAccessOptions's own doc comment.
builder.Services.AddHttpClient();

var azureSpeechOptions = builder.Configuration.GetSection("ProviderCredentials:AzureSpeech").Get<AzureProviderOptions>() ?? new AzureProviderOptions();
var azureTranslatorOptions = builder.Configuration.GetSection("ProviderCredentials:AzureTranslator").Get<AzureProviderOptions>() ?? new AzureProviderOptions();
var providerAccessOptions = builder.Configuration.GetSection(ProviderAccessOptions.SectionName).Get<ProviderAccessOptions>() ?? new ProviderAccessOptions();
var credentialLifetime = TimeSpan.FromSeconds(providerAccessOptions.CredentialLifetimeSeconds > 0 ? providerAccessOptions.CredentialLifetimeSeconds : 600);

if (databaseConfigured)
{
    builder.Services.AddScoped<IProviderAccessRepository, EfProviderAccessRepository>();
}
else
{
    builder.Services.AddSingleton<IProviderAccessRepository, InMemoryProviderAccessRepository>();
}

builder.Services.AddSingleton<IEnumerable<IProviderCredentialIssuer>>(sp =>
{
    var httpClientFactory = sp.GetRequiredService<IHttpClientFactory>();
    var issuers = new List<IProviderCredentialIssuer>();

    if (!string.IsNullOrWhiteSpace(azureSpeechOptions.SubscriptionKey) && !string.IsNullOrWhiteSpace(azureSpeechOptions.Region))
    {
        issuers.Add(new AzureProviderCredentialIssuer(
            httpClientFactory.CreateClient(), Provider.AzureSpeech, azureSpeechOptions.SubscriptionKey, azureSpeechOptions.Region,
            new HashSet<ProviderCapability> { ProviderCapability.SpeechRecognition, ProviderCapability.SpeechSynthesis }));
    }
    if (!string.IsNullOrWhiteSpace(azureTranslatorOptions.SubscriptionKey) && !string.IsNullOrWhiteSpace(azureTranslatorOptions.Region))
    {
        issuers.Add(new AzureProviderCredentialIssuer(
            httpClientFactory.CreateClient(), Provider.AzureTranslator, azureTranslatorOptions.SubscriptionKey, azureTranslatorOptions.Region,
            new HashSet<ProviderCapability> { ProviderCapability.TextTranslation }));
    }

    if (issuers.Count == 0)
    {
        Console.WriteLine("[startup] No ProviderCredentials configured — /provider-access will report every request as unsupported-provider (fail closed, never a fabricated credential).");
    }
    return issuers;
});

builder.Services.AddScoped<IProviderAccessGateway>(sp => new ProviderAccessGateway(
    sp.GetRequiredService<IDeviceRegistrationService>(),
    sp.GetRequiredService<IEntitlementService>(),
    sp.GetRequiredService<IEnumerable<IProviderCredentialIssuer>>(),
    sp.GetRequiredService<IProviderAccessRepository>(),
    sp.GetRequiredService<IAuditEventRepository>(),
    sp.GetRequiredService<IUnitOfWork>(),
    sp.GetRequiredService<IClock>(),
    credentialLifetime));

// ---- Phase 6.9: authoritative translation-session accounting ----
// TranslationSessions:LeaseSeconds is non-secret; defaults conservatively (60s) if unset
// — see TranslationSessionOptions's own doc comment. Provider credential issuance
// (Phase 6.8, above) is deliberately NOT wired into this service in any way — issuing a
// short-lived Azure token never starts/extends/ends a session and never creates usage.
var translationSessionOptions = builder.Configuration.GetSection(TranslationSessionOptions.SectionName).Get<TranslationSessionOptions>() ?? new TranslationSessionOptions();
var sessionLease = TimeSpan.FromSeconds(translationSessionOptions.LeaseSeconds > 0 ? translationSessionOptions.LeaseSeconds : 60);

builder.Services.AddScoped<ITranslationSessionService>(sp => new TranslationSessionService(
    sp.GetRequiredService<IDeviceRegistrationService>(),
    sp.GetRequiredService<IEntitlementService>(),
    sp.GetRequiredService<ITranslationSessionRepository>(),
    sp.GetRequiredService<IAccountRepository>(),
    sp.GetRequiredService<IUsageService>(),
    sp.GetRequiredService<IAuditEventRepository>(),
    sp.GetRequiredService<IUnitOfWork>(),
    sp.GetRequiredService<IClock>(),
    sessionLease));

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

// ---- Phase 6.7: real customer device endpoints ----
// Account derived exclusively from AccountResolutionMiddleware's resolved Account — never
// from a route parameter, request body, or query string. See
// docs/phase-6.7-device-licensing-policy.md §4/§5.

app.MapGet("/devices", async (HttpContext ctx, IDeviceRegistrationService deviceService) =>
{
    var account = (Account)ctx.Items[AccountResolutionMiddleware.AccountItemsKey]!;
    var devices = await deviceService.ListDevicesAsync(account.Id, ctx.RequestAborted);
    return Results.Ok(devices.Select(d => new
    {
        id = d.Id,
        platform = d.Platform.ToString(),
        displayName = d.DisplayName,
        status = d.Status.ToString(),
        registeredAt = d.RegisteredAt,
        lastSeenAt = d.LastSeenAt,
        revokedAt = d.RevokedAt,
    }));
}).RequireAuthorization();

app.MapPost("/devices", async (HttpContext ctx, RegisterDeviceRequest request, IDeviceRegistrationService deviceService) =>
{
    if (!Enum.TryParse<DevicePlatform>(request.Platform, ignoreCase: true, out var platform))
        return Results.BadRequest(new { status = "invalid_platform" });
    if (request.DisplayName is { Length: > 200 })
        return Results.BadRequest(new { status = "display_name_too_long" });

    var account = (Account)ctx.Items[AccountResolutionMiddleware.AccountItemsKey]!;
    try
    {
        var device = await deviceService.RegisterDeviceAsync(account.Id, platform, request.DisplayName, ctx.RequestAborted);
        return Results.Json(new
        {
            id = device.Id,
            platform = device.Platform.ToString(),
            displayName = device.DisplayName,
            status = device.Status.ToString(),
            registeredAt = device.RegisteredAt,
        }, statusCode: StatusCodes.Status201Created);
    }
    catch (DeviceLimitExceededException)
    {
        return Results.Json(new { status = "device_limit_exceeded" }, statusCode: StatusCodes.Status403Forbidden);
    }
}).RequireAuthorization();

// ---- Phase 25E: explicit, customer-confirmed device replacement ----
// No request body — deliberately: the account comes exclusively from AccountResolutionMiddleware (never a client-
// supplied accountId), and there is no client-supplied "old device id" either (see DeviceRegistrationService.
// ReplaceDeviceAsync's own doc comment — it revokes the calling account's OWN device set, nothing a client points
// at). Platform/displayName are the same client-supplied, non-authorization values POST /devices already accepts.
app.MapPost("/devices/replace", async (HttpContext ctx, RegisterDeviceRequest request, IDeviceRegistrationService deviceService) =>
{
    if (!Enum.TryParse<DevicePlatform>(request.Platform, ignoreCase: true, out var platform))
        return Results.BadRequest(new { status = "invalid_platform" });
    if (request.DisplayName is { Length: > 200 })
        return Results.BadRequest(new { status = "display_name_too_long" });

    var account = (Account)ctx.Items[AccountResolutionMiddleware.AccountItemsKey]!;
    try
    {
        var device = await deviceService.ReplaceDeviceAsync(account.Id, platform, request.DisplayName, ctx.RequestAborted);
        return Results.Json(new
        {
            id = device.Id,
            platform = device.Platform.ToString(),
            displayName = device.DisplayName,
            status = device.Status.ToString(),
            registeredAt = device.RegisteredAt,
        }, statusCode: StatusCodes.Status201Created);
    }
    catch (DeviceLimitExceededException)
    {
        // Only reachable when the plan's own MaxActiveDevices is 0 — replacement is never a way around the limit.
        return Results.Json(new { status = "device_limit_exceeded" }, statusCode: StatusCodes.Status403Forbidden);
    }
}).RequireAuthorization();

app.MapPost("/devices/{id:guid}/revoke", async (HttpContext ctx, Guid id, IDeviceRegistrationService deviceService) =>
{
    var account = (Account)ctx.Items[AccountResolutionMiddleware.AccountItemsKey]!;
    try
    {
        await deviceService.RevokeDeviceAsync(account.Id, id, ctx.RequestAborted);
    }
    catch (DeviceNotOwnedException)
    {
        // Same response whether the device doesn't exist at all or belongs to another
        // account — never lets a caller distinguish the two and enumerate device IDs
        // belonging to other accounts (docs/phase-6.7-device-licensing-policy.md §5).
        return Results.NotFound(new { status = "device_not_found" });
    }

    var devices = await deviceService.ListDevicesAsync(account.Id, ctx.RequestAborted);
    var revoked = devices.First(d => d.Id == id);
    return Results.Ok(new { id, status = revoked.Status.ToString(), revokedAt = revoked.RevokedAt });
}).RequireAuthorization();
NotImplementedPlaceholder(app, "/subscription").RequireAuthorization();
// ---- Phase 6.6: real customer subscription/entitlement/usage endpoints ----
// All four derive the account exclusively from AccountResolutionMiddleware's resolved
// Account (HttpContext.Items) — never from any client-supplied id/role/status value.
// GET /subscription and GET /usage first reconcile purely time-based transitions (see
// ISubscriptionLifecycleService.ReconcileTimeBasedTransitionsAsync) so no caller ever
// observes a stale, time-crossed subscription status.

app.MapGet("/subscription", async (HttpContext ctx, ISubscriptionRepository subscriptions, ISubscriptionLifecycleService lifecycle, IClock clock) =>
{
    var account = (Account)ctx.Items[AccountResolutionMiddleware.AccountItemsKey]!;
    var subscription = await subscriptions.FindByAccountAsync(account.Id, ctx.RequestAborted);
    if (subscription is null) return Results.NotFound(new { status = "no_subscription" });

    subscription = await lifecycle.ReconcileTimeBasedTransitionsAsync(subscription, clock.UtcNow, ctx.RequestAborted);
    return Results.Ok(new
    {
        status = subscription.Status.ToString(),
        planId = subscription.PlanId,
        currentPeriodStart = subscription.CurrentPeriodStart,
        currentPeriodEnd = subscription.CurrentPeriodEnd,
        cancelAtPeriodEnd = subscription.CancelAtPeriodEnd,
    });
}).RequireAuthorization();

app.MapPost("/subscription/cancel", async (HttpContext ctx, CancelSubscriptionRequest request, ISubscriptionRepository subscriptions, ISubscriptionLifecycleService lifecycle, IClock clock) =>
{
    var account = (Account)ctx.Items[AccountResolutionMiddleware.AccountItemsKey]!;
    var subscription = await subscriptions.FindByAccountAsync(account.Id, ctx.RequestAborted);
    if (subscription is null) return Results.NotFound(new { status = "no_subscription" });

    var updated = await lifecycle.ApplyCustomerCancellationAsync(subscription, request.Immediate, clock.UtcNow, ctx.RequestAborted);
    return Results.Ok(new
    {
        status = updated.Status.ToString(),
        cancelAtPeriodEnd = updated.CancelAtPeriodEnd,
    });
}).RequireAuthorization();

// Phase 25: mapped only when the Trial has been explicitly enabled and configured by an operator.
if (trialOptions.IsUsable)
{
    app.MapTrialSubscriptionEndpoint();
}

app.MapGet("/entitlements", async (HttpContext ctx, ISubscriptionRepository subscriptions, IPlanRepository plans) =>
{
    var account = (Account)ctx.Items[AccountResolutionMiddleware.AccountItemsKey]!;
    var subscription = await subscriptions.FindByAccountAsync(account.Id, ctx.RequestAborted);
    if (subscription is null) return Results.NotFound(new { status = "no_subscription" });

    var entitlements = await plans.GetEntitlementsAsync(subscription.PlanId, ctx.RequestAborted);
    return Results.Ok(new
    {
        subscriptionStatus = subscription.Status.ToString(),
        entitlements = entitlements.ToDictionary(e => e.Key, e => e.Value),
    });
}).RequireAuthorization();

app.MapGet("/usage", async (HttpContext ctx, IUsageService usage, ITrialUsageReporter trialUsage, IClock clock) =>
{
    var account = (Account)ctx.Items[AccountResolutionMiddleware.AccountItemsKey]!;
    var periodBucket = clock.UtcNow.ToString("yyyy-MM");
    var summary = await usage.GetSummaryAsync(account.Id, periodBucket, ctx.RequestAborted);
    // Phase 25B: unchanged monthly summary fields, plus (only for a trial customer) the trial-lifetime allowance —
    // clients must show `trial` (not the monthly figure / paid limit) for the trial allowance. Null when there is no trial.
    var trial = await trialUsage.GetAsync(account.Id, ctx.RequestAborted);
    return Results.Ok(new
    {
        summary.PeriodBucket,
        summary.ServerDerivedSeconds,
        summary.ClientReportedSeconds,
        trial,
    });
}).RequireAuthorization();

// ---- Phase 6.6: billing webhook — anonymous at the ASP.NET layer (no customer JWT
// exists here); authenticated instead by the provider's own signature, verified inside
// IBillingWebhookProcessor before anything else happens. See
// docs/phase-6.6-billing-subscription.md §7/§8 for the exact trust boundary.
app.MapPost("/webhooks/billing", async (HttpContext ctx, IBillingWebhookProcessor processor) =>
{
    using var reader = new StreamReader(ctx.Request.Body);
    var rawPayload = await reader.ReadToEndAsync(ctx.RequestAborted);
    var headers = ctx.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString());

    var result = await processor.ProcessAsync(rawPayload, headers, ctx.RequestAborted);
    return Results.StatusCode(result.HttpStatusCode);
});

// ---- Phase 6.8: provider-access gateway endpoint ----
// deviceId/provider/capability come from the request body (validated, parsed to closed
// enums); the ACCOUNT never does — derived exclusively from AccountResolutionMiddleware,
// exactly like every other customer endpoint. Every failure path returns a generic,
// safe reason string — never provider/internal details, never the requested credential.
app.MapPost("/provider-access", async (HttpContext ctx, ProviderAccessRequest request, IProviderAccessGateway gateway) =>
{
    if (!Enum.TryParse<Provider>(request.Provider, ignoreCase: true, out var provider))
        return Results.BadRequest(new { status = "unsupported_provider" });
    if (!Enum.TryParse<ProviderCapability>(request.Capability, ignoreCase: true, out var capability))
        return Results.BadRequest(new { status = "unsupported_capability" });
    if (!Guid.TryParse(request.DeviceId, out var deviceId))
        return Results.BadRequest(new { status = "invalid_device_id" });

    var account = (Account)ctx.Items[AccountResolutionMiddleware.AccountItemsKey]!;
    var result = await gateway.RequestAccessAsync(account.Id, deviceId, provider, capability, ctx.RequestAborted);

    return result.Outcome switch
    {
        ProviderAccessOutcome.Granted => Results.Ok(new
        {
            provider = provider.ToString(),
            capability = capability.ToString(),
            accessToken = result.Credential!.AccessToken,
            region = result.Credential.Region,
            expiresAt = result.Credential.ExpiresAt,
            correlationId = result.CorrelationId,
        }),
        ProviderAccessOutcome.DeviceNotAuthorized => Results.Json(new { status = "device_not_authorized" }, statusCode: StatusCodes.Status403Forbidden),
        ProviderAccessOutcome.EntitlementDenied => Results.Json(new { status = "entitlement_denied" }, statusCode: StatusCodes.Status403Forbidden),
        ProviderAccessOutcome.UsageDenied => Results.Json(new { status = "usage_denied" }, statusCode: StatusCodes.Status403Forbidden),
        ProviderAccessOutcome.UnsupportedProvider => Results.BadRequest(new { status = "unsupported_provider" }),
        ProviderAccessOutcome.UnsupportedCapability => Results.BadRequest(new { status = "unsupported_capability" }),
        ProviderAccessOutcome.ProviderUnavailable => Results.Json(new { status = "provider_unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable),
        _ => Results.Json(new { status = "denied" }, statusCode: StatusCodes.Status403Forbidden),
    };
}).RequireAuthorization();

// ---- Phase 6.9: translation-session accounting endpoints ----
// Account derived exclusively from AccountResolutionMiddleware — never from the request
// body. deviceId is client-supplied (must belong to the authenticated account, verified
// by the service); accountId/usage/duration are never accepted from the client at all.
app.MapPost("/translation-sessions", async (HttpContext ctx, StartTranslationSessionRequest request, ITranslationSessionService sessionService) =>
{
    if (!Guid.TryParse(request.DeviceId, out var deviceId))
        return Results.BadRequest(new { status = "invalid_device_id" });

    var account = (Account)ctx.Items[AccountResolutionMiddleware.AccountItemsKey]!;
    var result = await sessionService.StartSessionAsync(account.Id, deviceId, request.ClientSessionId, request.Direction, ctx.RequestAborted);

    return result.Outcome switch
    {
        SessionStartOutcome.Started => Results.Json(new
        {
            sessionId = result.Session!.Id,
            state = result.Session.State.ToString(),
            startedAt = result.Session.StartedAt,
            resumed = false,
        }, statusCode: StatusCodes.Status201Created),
        SessionStartOutcome.Resumed => Results.Ok(new
        {
            sessionId = result.Session!.Id,
            state = result.Session.State.ToString(),
            startedAt = result.Session.StartedAt,
            resumed = true,
        }),
        SessionStartOutcome.DeviceNotAuthorized => Results.Json(new { status = "device_not_authorized" }, statusCode: StatusCodes.Status403Forbidden),
        // Phase 25B: `code` is a stable, machine-readable reason (e.g. trial_expired / trial_usage_exhausted) the client maps
        // to an upgrade message. It is null for denials with no dedicated reason. No payment/upgrade URL is implied here.
        SessionStartOutcome.EntitlementDenied => Results.Json(new { status = "entitlement_denied", code = result.Code }, statusCode: StatusCodes.Status403Forbidden),
        SessionStartOutcome.UsageDenied => Results.Json(new { status = "usage_limit_exceeded", code = result.Code }, statusCode: StatusCodes.Status403Forbidden),
        _ => Results.Json(new { status = "denied" }, statusCode: StatusCodes.Status403Forbidden),
    };
}).RequireAuthorization();

app.MapPost("/translation-sessions/{id:guid}/heartbeat", async (HttpContext ctx, Guid id, ITranslationSessionService sessionService) =>
{
    var account = (Account)ctx.Items[AccountResolutionMiddleware.AccountItemsKey]!;
    var result = await sessionService.HeartbeatAsync(account.Id, id, ctx.RequestAborted);

    return result.Outcome switch
    {
        SessionOperationOutcome.Success => Results.Ok(new { sessionId = id, state = result.Session!.State.ToString(), lastActivityAt = result.Session.LastActivityAt }),
        SessionOperationOutcome.NotFound => Results.NotFound(new { status = "session_not_found" }),
        SessionOperationOutcome.AlreadyTerminal => Results.Json(new { status = "session_terminal", state = result.Session?.State.ToString() }, statusCode: StatusCodes.Status409Conflict),
        _ => Results.Json(new { status = "denied" }, statusCode: StatusCodes.Status403Forbidden),
    };
}).RequireAuthorization();

app.MapPost("/translation-sessions/{id:guid}/end", async (HttpContext ctx, Guid id, ITranslationSessionService sessionService) =>
{
    var account = (Account)ctx.Items[AccountResolutionMiddleware.AccountItemsKey]!;
    var result = await sessionService.EndAsync(account.Id, id, ctx.RequestAborted);

    return result.Outcome switch
    {
        SessionOperationOutcome.Success => Results.Ok(new { sessionId = id, state = result.Session!.State.ToString(), terminalAt = result.Session.TerminalAt }),
        SessionOperationOutcome.NotFound => Results.NotFound(new { status = "session_not_found" }),
        SessionOperationOutcome.AlreadyTerminal => Results.Ok(new { sessionId = id, state = result.Session?.State.ToString(), terminalAt = result.Session?.TerminalAt }), // idempotent — same success shape, not an error
        _ => Results.Json(new { status = "denied" }, statusCode: StatusCodes.Status403Forbidden),
    };
}).RequireAuthorization();

// ---- Phase 7.0: customer profile self-service ----
// AccountId always comes from AccountResolutionMiddleware — never from the request body,
// a route parameter, or a query string; these two endpoints do not accept an AccountId
// at all (docs/phase-7.0-production-identity-and-account-lifecycle.md §33).
app.MapGet("/profile", async (HttpContext ctx, IProfileService profileService) =>
{
    var account = (Account)ctx.Items[AccountResolutionMiddleware.AccountItemsKey]!;
    var profile = await profileService.GetAsync(account.Id, ctx.RequestAborted);
    return Results.Ok(new { displayName = profile.DisplayName, preferredLanguagePair = profile.PreferredLanguagePair, updatedAt = profile.UpdatedAt });
}).RequireAuthorization();

app.MapPut("/profile", async (HttpContext ctx, UpdateProfileRequest request, IProfileService profileService) =>
{
    var account = (Account)ctx.Items[AccountResolutionMiddleware.AccountItemsKey]!;
    var result = await profileService.UpdateAsync(account.Id, request.DisplayName, request.PreferredLanguagePair, ctx.RequestAborted);

    return result.Outcome switch
    {
        ProfileUpdateOutcome.Success => Results.Ok(new { displayName = result.Profile!.DisplayName, preferredLanguagePair = result.Profile.PreferredLanguagePair, updatedAt = result.Profile.UpdatedAt }),
        _ => Results.BadRequest(new { status = "invalid_profile" }),
    };
}).RequireAuthorization();

// Phase 6.4 AUTHORIZATION-BOUNDARY PROOF ONLY — not a product feature. Exists solely so
// the CUSTOMER-vs-ADMIN-vs-SUPER_ADMIN role gate can be exercised end-to-end over real
// HTTP in an integration test (see VTTranslate.Backend.Tests). Still returns the same
// 501 placeholder body as every other route once past the role gate — no functionality
// is implemented here either.
NotImplementedPlaceholder(app, "/internal/diagnostics")
    .RequireAuthorization()
    .RequireAutraxisRole(Role.SuperAdmin);

// Phase 7.0 ADMIN/CUSTOMER BOUNDARY PROOF ONLY — not a product feature, and explicitly
// NOT an admin API (docs/phase-7.0-production-identity-and-account-lifecycle.md §12/§32
// forbid building one in this phase). Exists solely so the customer-vs-admin-scheme
// separation can be exercised end-to-end over real HTTP in an integration test — a
// customer-audience token can never satisfy the "AdminScheme" policy, and this route
// never touches AccountResolutionMiddleware/Account at all (admin identities are not
// AUTRAXIS customer accounts).
NotImplementedPlaceholder(app, "/internal/admin-boundary")
    .RequireAuthorization(AdminIdentityOptions.SchemeName);

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

/// <summary>Phase 6.6: request body for POST /subscription/cancel. Deliberately carries no account/status/plan value — the account is always derived from AccountResolutionMiddleware, never from the request.</summary>
public sealed record CancelSubscriptionRequest(bool Immediate);

/// <summary>Phase 6.7: request body for POST /devices. Deliberately carries no account ID or device ID — the account comes from AccountResolutionMiddleware, and the device ID is always server-generated (Guid.NewGuid()), never client-supplied.</summary>
public sealed record RegisterDeviceRequest(string Platform, string? DisplayName);

/// <summary>Phase 6.8: request body for POST /provider-access. Deliberately carries no account ID and no role/entitlement/usage value — the account is always derived from AccountResolutionMiddleware; only the target device (which must belong to that account) and the requested provider/capability are client-supplied.</summary>
public sealed record ProviderAccessRequest(string DeviceId, string Provider, string Capability);

/// <summary>Phase 6.9: request body for POST /translation-sessions. Deliberately carries no accountId/entitlementId/subscriptionId/usageAmount/usageDuration/allowedMinutes/providerSecret — the account comes from AccountResolutionMiddleware; ClientSessionId is an optional correlation key for idempotency/reconnect only, never the database primary identity.</summary>
public sealed record StartTranslationSessionRequest(string DeviceId, string? ClientSessionId, string? Direction);

/// <summary>Phase 7.0: request body for PUT /profile. Deliberately carries no AccountId — the account comes from AccountResolutionMiddleware; both fields are optional, matching the entity's own nullable shape.</summary>
public sealed record UpdateProfileRequest(string? DisplayName, string? PreferredLanguagePair);

/// <summary>Exposed for VTTranslate.Backend.Tests' WebApplicationFactory-based integration tests (health, placeholder, and authentication/authorization boundary tests).</summary>
public partial class Program;
