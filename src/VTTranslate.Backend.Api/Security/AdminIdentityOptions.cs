namespace VTTranslate.Backend.Api.Security;

/// <summary>
/// Phase 7.0 — bound from the "Identity:Admin" configuration section, deliberately
/// separate from "Identity" (customer, <see cref="EntraIdentityOptions"/>) rather than
/// renaming the existing customer keys — see
/// docs/phase-7.0-production-identity-and-account-lifecycle.md §12/§14. Neither field is
/// a secret (same reasoning as <see cref="EntraIdentityOptions"/>). The preferred
/// production model is a SEPARATE Microsoft Entra workforce tenant/application
/// registration from the customer Entra External ID tenant, so a customer-issued token
/// can never satisfy this audience and an admin-issued token can never satisfy the
/// customer audience — see the "AdminScheme" JwtBearer registration in Program.cs.
/// </summary>
public sealed class AdminIdentityOptions
{
    public const string SectionName = "Identity:Admin";

    /// <summary>The named JwtBearer authentication scheme + authorization policy for the admin/workforce boundary — deliberately distinct from the default ("Bearer") customer scheme so the two can never be conflated by ASP.NET's own scheme resolution.</summary>
    public const string SchemeName = "AdminScheme";

    public string? Authority { get; set; }
    public string? Audience { get; set; }
}
