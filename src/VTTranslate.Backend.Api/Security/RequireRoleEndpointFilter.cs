using VTTranslate.Backend.Application.Authorization;
using VTTranslate.Backend.Domain.Entities;
using VTTranslate.Backend.Domain.Enums;

namespace VTTranslate.Backend.Api.Security;

/// <summary>
/// Minimal-API endpoint filter enforcing a minimum <see cref="Role"/> against the
/// <see cref="Account"/> resolved by <see cref="AccountResolutionMiddleware"/> — the
/// ASP.NET-layer wiring for AUTRAXIS's own <see cref="IAuthorizationService"/>, kept
/// deliberately separate from ASP.NET's built-in claims-based role/policy system (which
/// would require putting a role CLAIM on the ClaimsPrincipal — something this
/// architecture explicitly avoids, since AUTRAXIS role is never Entra-claim-sourced).
///
/// If <see cref="AccountResolutionMiddleware"/> did not run or did not resolve an
/// account for this request (should not happen for a route with
/// <c>.RequireAuthorization()</c>, but checked defensively), this fails closed with 401
/// rather than 403 — "we don't know who this is" is an authentication gap, not an
/// authorization denial.
/// </summary>
public sealed class RequireRoleEndpointFilter(Role minimumRole) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        if (context.HttpContext.Items.TryGetValue(AccountResolutionMiddleware.AccountItemsKey, out var value) && value is Account account)
        {
            var authorization = context.HttpContext.RequestServices.GetRequiredService<IAuthorizationService>();
            if (!authorization.HasAtLeastRole(account, minimumRole))
                return Results.Json(new { status = "insufficient_role" }, statusCode: StatusCodes.Status403Forbidden);

            return await next(context);
        }

        // Defensive fail-closed: no resolved account context at all.
        return Results.Json(new { status = "unauthenticated" }, statusCode: StatusCodes.Status401Unauthorized);
    }
}

public static class RequireRoleEndpointFilterExtensions
{
    public static TBuilder RequireAutraxisRole<TBuilder>(this TBuilder builder, Role minimumRole) where TBuilder : IEndpointConventionBuilder
    {
        builder.AddEndpointFilter(new RequireRoleEndpointFilter(minimumRole));
        return builder;
    }
}
