using VTTranslate.Backend.Api.Security;
using VTTranslate.Backend.Application.Subscriptions;
using VTTranslate.Backend.Domain.Entities;

namespace VTTranslate.Backend.Api.Subscriptions;

/// <summary>
/// Phase 25 — <c>POST /subscription/trial</c>. Mapped ONLY when an operator has explicitly enabled and configured
/// the Trial (<see cref="TrialProvisioningOptions.IsUsable"/>); otherwise the route does not exist (404) in every
/// environment. The request carries no body and no parameters: the account is derived exclusively from
/// <see cref="AccountResolutionMiddleware"/>, and the plan/status/period/quota are decided server-side.
/// This grants a free, bounded entitlement — it does NOT take payment and no payment provider is involved.
/// </summary>
public static class TrialEndpoints
{
    public static void MapTrialSubscriptionEndpoint(this WebApplication app)
    {
        app.MapPost("/subscription/trial", async (HttpContext ctx, ISubscriptionProvisioningService provisioning) =>
        {
            var account = (Account)ctx.Items[AccountResolutionMiddleware.AccountItemsKey]!;
            var result = await provisioning.StartTrialAsync(account.Id, ctx.RequestAborted);

            return result.Outcome switch
            {
                TrialProvisioningOutcome.Started => Results.Json(Describe(result.Subscription!, created: true), statusCode: StatusCodes.Status201Created),
                TrialProvisioningOutcome.AlreadySubscribed => Results.Ok(Describe(result.Subscription!, created: false)),
                TrialProvisioningOutcome.TrialAlreadyUsed => Results.Json(new { status = "trial_not_available", code = "trial_already_used", reason = "a subscription already existed for this account" }, statusCode: StatusCodes.Status409Conflict),
                TrialProvisioningOutcome.AccountNotEligible => Results.Json(new { status = "account_not_eligible" }, statusCode: StatusCodes.Status403Forbidden),
                // Operator misconfiguration: never fabricate an entitlement, never leak plan details.
                _ => Results.Json(new { status = "trial_unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable),
            };
        }).RequireAuthorization();
    }

    private static object Describe(Subscription s, bool created) => new
    {
        status = s.Status.ToString(),
        planId = s.PlanId,
        currentPeriodStart = s.CurrentPeriodStart,
        currentPeriodEnd = s.CurrentPeriodEnd,
        created,
    };
}
