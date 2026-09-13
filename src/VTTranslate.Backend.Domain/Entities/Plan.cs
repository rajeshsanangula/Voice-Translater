namespace VTTranslate.Backend.Domain.Entities;

/// <summary>
/// A sellable SKU. <see cref="PriceHandle"/> is an OPAQUE reference into whichever
/// <see cref="Abstractions.IBillingProvider"/> is eventually configured — never a
/// hard-coded price. No pricing is decided in Phase 6.3; see
/// docs/phase-6.2b-resolved-architecture-decisions.md §4/§19.
/// </summary>
public sealed class Plan
{
    public required Guid Id { get; init; }

    /// <summary>Placeholder naming only (e.g. "Free", "Pro") — final commercial names are NOT decided (Phase 6.1 §18 item 9).</summary>
    public required string Name { get; init; }

    /// <summary>Opaque reference to the billing provider's own price/product object. Null until a billing provider is actually integrated (not in this phase).</summary>
    public string? PriceHandle { get; set; }

    public bool IsPubliclyPurchasable { get; init; }
}
