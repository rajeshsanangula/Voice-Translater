namespace VTTranslate.Backend.Domain.Enums;

/// <summary>
/// Backend-authoritative account usability state (Phase 6.4). Distinct from
/// <see cref="Entities.Account.DeletionRequestedAt"/>-derived soft-delete — a
/// <see cref="Suspended"/> account is a deliberate administrative action (e.g. abuse,
/// fraud, chargeback), not a customer-initiated deletion. Never inferred from an Entra
/// claim, never client-settable — only ever changed by trusted backend/admin logic
/// (none of which is implemented yet; this phase only adds the field and its
/// fail-closed enforcement at account resolution).
/// </summary>
public enum AccountStatus
{
    Active,
    Suspended,
}
