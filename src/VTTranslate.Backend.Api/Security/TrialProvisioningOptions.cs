namespace VTTranslate.Backend.Api.Security;

/// <summary>
/// Phase 25 — non-secret, operator-controlled configuration for the free-Trial provisioning path
/// (<c>POST /subscription/trial</c>). Bound from <c>Subscriptions:Trial</c>.
/// <para>
/// <see cref="Enabled"/> defaults to <c>false</c>: unless an operator explicitly turns it on AND names an
/// existing trial plan, the endpoint is not mapped at all and no subscription can ever be granted through it.
/// A customer never supplies, and cannot influence, the plan, status, quota or period — the plan comes only
/// from here and its limits only from that plan's persisted entitlements.
/// </para>
/// </summary>
public sealed class TrialProvisioningOptions
{
    public const string SectionName = "Subscriptions:Trial";

    public bool Enabled { get; set; }

    /// <summary>Id of the operator-created Trial <c>Plan</c> row. Required when <see cref="Enabled"/>.</summary>
    public Guid? PlanId { get; set; }

    public bool IsUsable => Enabled && PlanId is { } id && id != Guid.Empty;
}
