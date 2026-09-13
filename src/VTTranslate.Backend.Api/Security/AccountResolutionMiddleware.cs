using VTTranslate.Backend.Application.Identity;
using VTTranslate.Backend.Domain.Abstractions;
using VTTranslate.Backend.Domain.Entities;
using VTTranslate.Backend.Infrastructure.Identity;

namespace VTTranslate.Backend.Api.Security;

/// <summary>
/// The API-composition-root glue implementing:
/// <c>AuthenticatedPrincipal → AUTRAXIS Account lookup → Account status check</c>
/// (docs/phase-6.4-entra-authentication.md §6/§9). Runs AFTER
/// <c>app.UseAuthentication()</c> (so <c>HttpContext.User</c> reflects a
/// cryptographically-verified JwtBearer result, if any) and BEFORE
/// <c>app.UseAuthorization()</c>.
///
/// This is the ONLY place <see cref="System.Security.Claims.ClaimsPrincipal"/> is read
/// in the entire request pipeline outside the JwtBearer middleware itself — everything
/// downstream (endpoint filters, application services) works with the plain
/// <see cref="Account"/> domain entity stashed in <see cref="HttpContext.Items"/>, never
/// with claims directly. This is the concrete mechanism that keeps "Entra proved this
/// identity" (authentication) separate from "AUTRAXIS says this account may act"
/// (authorization) — an Entra claim is NEVER used downstream to decide a role; only
/// <see cref="Account.Role"/>, read here from the resolved account, is.
///
/// Unauthenticated requests are NOT touched here — they pass through untouched and are
/// rejected uniformly by <c>app.UseAuthorization()</c>'s <c>RequireAuthorization()</c>
/// policy on the endpoint (HTTP 401), so every 401 in this API has exactly one code path,
/// not two.
/// </summary>
public sealed class AccountResolutionMiddleware(RequestDelegate next, ILogger<AccountResolutionMiddleware> logger)
{
    public const string AccountItemsKey = "AutraxisAccount";

    public async Task InvokeAsync(HttpContext context, IIdentityProvider identityProvider, IAccountResolutionService accountResolution, IAccountProvisioningService provisioning)
    {
        if (context.User.Identity?.IsAuthenticated != true)
        {
            // Not authenticated — let app.UseAuthorization()'s RequireAuthorization()
            // reject this uniformly (401). Nothing to resolve.
            await next(context);
            return;
        }

        var claims = context.User.ToClaimsDictionary();
        var principal = identityProvider.TryCreatePrincipal(claims);
        if (principal is null)
        {
            // Authenticated (token cryptographically valid) but missing/malformed the
            // specific claims this provider requires (e.g. no "sub"/"oid") — fail closed.
            // SAFE LOG: no claim VALUES are logged, only the fact that this happened.
            logger.LogWarning("Authentication succeeded but required identity claims were missing or malformed.");
            await WriteProblemAsync(context, StatusCodes.Status401Unauthorized, "invalid_identity_claims");
            return;
        }

        var result = await accountResolution.ResolveAsync(principal, context.RequestAborted);
        switch (result.Outcome)
        {
            case AccountResolutionOutcome.Resolved:
                context.Items[AccountItemsKey] = result.Account;
                await next(context);
                return;

            case AccountResolutionOutcome.AccountNotFound:
                // Phase 7.0 — gated JIT provisioning (docs/phase-7.0-production-identity-
                // and-account-lifecycle.md §7/§25). This is the ONLY call site for
                // provisioning: a controlled, explicit, observable decision point, never
                // a side effect buried inside AccountResolutionService's own lookup
                // (which stays pure resolution-only, unchanged, so its existing tests
                // remain valid).
                var provisioningResult = await provisioning.ProvisionAsync(principal, context.RequestAborted);
                if (provisioningResult.Outcome == AccountProvisioningOutcome.Provisioned && provisioningResult.Account is not null)
                {
                    context.Items[AccountItemsKey] = provisioningResult.Account;
                    await next(context);
                    return;
                }

                // Deliberately the SAME response as before provisioning existed —
                // anti-enumeration (§17): a client cannot distinguish "no account and
                // provisioning denied" from the pre-Phase-7.0 "no account at all" outcome.
                logger.LogInformation("Authenticated identity has no AUTRAXIS account and provisioning did not apply.");
                await WriteProblemAsync(context, StatusCodes.Status403Forbidden, "account_not_found");
                return;

            case AccountResolutionOutcome.AccountSuspended:
                logger.LogWarning("Authenticated identity resolved to a suspended/unusable account.");
                await WriteProblemAsync(context, StatusCodes.Status403Forbidden, "account_suspended");
                return;

            default:
                // Unreachable with the current enum, but fail closed rather than fall
                // through to next() if this enum is ever extended without updating here.
                logger.LogError("Unhandled account resolution outcome — failing closed.");
                await WriteProblemAsync(context, StatusCodes.Status403Forbidden, "account_resolution_failed");
                return;
        }
    }

    private static Task WriteProblemAsync(HttpContext context, int statusCode, string status)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsJsonAsync(new { status });
    }
}

public static class AccountResolutionMiddlewareExtensions
{
    public static IApplicationBuilder UseAutraxisAccountResolution(this IApplicationBuilder app) =>
        app.UseMiddleware<AccountResolutionMiddleware>();
}
