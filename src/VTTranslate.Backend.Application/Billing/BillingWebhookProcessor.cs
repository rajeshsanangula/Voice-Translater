using System.Security.Cryptography;
using System.Text;
using VTTranslate.Backend.Domain.Abstractions;
using VTTranslate.Backend.Domain.Entities;
using VTTranslate.Backend.Domain.Enums;

namespace VTTranslate.Backend.Application.Billing;

public sealed class BillingWebhookProcessor(
    IBillingProvider billingProvider,
    IBillingEventRepository billingEvents,
    ISubscriptionLifecycleService lifecycle,
    IUnitOfWork unitOfWork,
    IAuditEventRepository audit,
    IClock clock) : IBillingWebhookProcessor
{
    public async Task<WebhookProcessingResult> ProcessAsync(string rawPayload, IReadOnlyDictionary<string, string> headers, CancellationToken ct)
    {
        // Step 1 (Phase 6.6 §7/§8): signature verification happens BEFORE any database
        // write. An invalid/missing signature never touches billing_events at all — no
        // row, no correlation attempt, nothing trusted from the payload.
        var normalized = await billingProvider.TryVerifyAndNormalizeWebhookAsync(rawPayload, headers, ct);
        if (normalized is null)
        {
            await AuditAsync(null, "WebhookSignatureRejected", null, ct);
            return new WebhookProcessingResult(400);
        }

        var payloadHash = Hash(rawPayload);

        // Step 2: first, independent transaction — durably record receipt, before any
        // attempt to apply it. This is the row that makes idempotency possible even if
        // everything after this crashes.
        var billingEvent = new BillingEvent
        {
            Id = Guid.NewGuid(),
            Provider = normalized.Provider,
            ProviderEventId = normalized.ProviderEventId,
            EventType = normalized.EventType.ToString(),
            ReceivedAt = clock.UtcNow,
            ProcessingStatus = BillingEventProcessingStatus.Received,
            RawPayloadHash = payloadHash,
        };

        try
        {
            await billingEvents.SaveAsync(billingEvent, ct);
        }
        catch (Domain.DuplicateBillingEventException)
        {
            // Duplicate delivery. Resume from whatever the existing row's status says —
            // never re-insert, never silently reprocess a terminal event.
            var existing = await billingEvents.FindByProviderEventIdAsync(normalized.Provider, normalized.ProviderEventId, ct)
                ?? throw new InvalidOperationException("Duplicate reported but the existing BillingEvent could not be found.");

            if (existing.ProcessingStatus is BillingEventProcessingStatus.Processed or BillingEventProcessingStatus.Rejected or BillingEventProcessingStatus.Superseded)
                return new WebhookProcessingResult(200); // already terminal — acknowledge, never reprocess

            billingEvent = existing; // Received or Failed — re-attempt step 3 against the SAME row
        }

        // Step 3: second, separate transaction — the application attempt. The
        // BillingEvent status update and the Subscription write (if any) commit
        // together, atomically, via IUnitOfWork.
        try
        {
            return await unitOfWork.ExecuteInTransactionAsync(async innerCt =>
            {
                var result = await lifecycle.ApplyBillingEventAsync(normalized, innerCt);

                billingEvent.ProcessingStatus = result.Outcome switch
                {
                    BillingEventApplicationOutcome.Applied => BillingEventProcessingStatus.Processed,
                    BillingEventApplicationOutcome.Superseded => BillingEventProcessingStatus.Superseded,
                    _ => BillingEventProcessingStatus.Rejected,
                };
                billingEvent.ProcessedAt = clock.UtcNow;
                billingEvent.RejectionReason = result.Reason;
                billingEvent.SubscriptionId = result.Subscription?.Id;
                billingEvent.AccountId = result.Subscription?.AccountId;
                await billingEvents.SaveAsync(billingEvent, innerCt);

                // Every outcome (Applied/Superseded/Rejected) is acknowledged 200 — none
                // of these should cause the provider to retry (Phase 6.6 §7/§8 trust
                // boundary table).
                return new WebhookProcessingResult(200);
            }, ct);
        }
        catch (Exception)
        {
            // Transient infrastructure failure during the application attempt ONLY
            // (never a content/authenticity/ordering problem, which are handled inside
            // the transaction above and never throw). The already-durable Received row
            // is updated to Failed in a THIRD, minimal transaction, and a 5xx is
            // returned so the provider retries.
            billingEvent.ProcessingStatus = BillingEventProcessingStatus.Failed;
            await billingEvents.SaveAsync(billingEvent, ct);
            return new WebhookProcessingResult(500);
        }
    }

    private async Task AuditAsync(Guid? accountId, string eventType, string? metadata, CancellationToken ct) =>
        await audit.AddAsync(new AuditEvent
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            EventType = eventType,
            Metadata = metadata,
            OccurredAt = clock.UtcNow,
        }, ct);

    private static string Hash(string rawPayload)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(rawPayload));
        return Convert.ToHexString(bytes);
    }
}
