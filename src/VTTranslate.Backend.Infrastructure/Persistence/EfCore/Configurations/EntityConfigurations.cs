using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using VTTranslate.Backend.Domain.Entities;

namespace VTTranslate.Backend.Infrastructure.Persistence.EfCore.Configurations;

// All Fluent API — Domain entities carry zero EF Core attributes or base classes (Phase
// 6.5 instruction: "the database is a persistence mechanism... must not become the
// business-logic layer"). Every index/constraint here is documented as to WHY it exists
// (docs/phase-6.5-database-persistence.md §9).

public sealed class AccountConfiguration : IEntityTypeConfiguration<Account>
{
    public void Configure(EntityTypeBuilder<Account> b)
    {
        b.ToTable("accounts");
        b.HasKey(a => a.Id);
        b.Property(a => a.Email).IsRequired().HasMaxLength(320);
        b.Property(a => a.ExternalIdentityProvider).IsRequired().HasMaxLength(100);
        b.Property(a => a.ExternalSubjectId).IsRequired().HasMaxLength(256);
        b.Property(a => a.Role).HasConversion<string>().HasMaxLength(20);
        b.Property(a => a.Status).HasConversion<string>().HasMaxLength(20);

        // The one constraint this entire phase exists to enforce at the storage layer:
        // Phase 6.4's identity-resolution key (Provider, ExternalSubjectId) must be
        // globally unique so two accounts can never map to the same external identity,
        // and account resolution can never be ambiguous.
        b.HasIndex(a => new { a.ExternalIdentityProvider, a.ExternalSubjectId })
            .IsUnique()
            .HasDatabaseName("ix_accounts_external_identity");

        // PostgreSQL's built-in row-versioning column, used as an EF Core optimistic
        // concurrency token with no synthetic column added to the Domain entity — see
        // docs/phase-6.5-database-persistence.md §11.
        b.Property<uint>("xmin").IsRowVersion();
    }
}

public sealed class ProfileConfiguration : IEntityTypeConfiguration<Profile>
{
    public void Configure(EntityTypeBuilder<Profile> b)
    {
        b.ToTable("profiles");
        // 1:1 with Account — AccountId IS the primary key, not a separate surrogate key,
        // since a Profile cannot exist without exactly one owning Account.
        b.HasKey(p => p.AccountId);
        b.Property(p => p.DisplayName).HasMaxLength(200);
        b.Property(p => p.PreferredLanguagePair).HasMaxLength(50);

        b.HasOne<Account>().WithOne().HasForeignKey<Profile>(p => p.AccountId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class PlanConfiguration : IEntityTypeConfiguration<Plan>
{
    public void Configure(EntityTypeBuilder<Plan> b)
    {
        b.ToTable("plans");
        b.HasKey(p => p.Id);
        b.Property(p => p.Name).IsRequired().HasMaxLength(100);
        b.Property(p => p.PriceHandle).HasMaxLength(200);
    }
}

public sealed class EntitlementConfiguration : IEntityTypeConfiguration<Entitlement>
{
    public void Configure(EntityTypeBuilder<Entitlement> b)
    {
        b.ToTable("entitlements");
        b.HasKey(e => e.Id);
        b.Property(e => e.Key).IsRequired().HasMaxLength(100);
        b.Property(e => e.Value).IsRequired().HasMaxLength(1000);

        // A plan must never define the same entitlement key twice — the application
        // layer's FirstOrDefault(e => e.Key == ...) lookup pattern silently assumes this.
        b.HasIndex(e => new { e.PlanId, e.Key }).IsUnique().HasDatabaseName("ix_entitlements_plan_key");

        b.HasOne<Plan>().WithMany().HasForeignKey(e => e.PlanId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class SubscriptionConfiguration : IEntityTypeConfiguration<Subscription>
{
    public void Configure(EntityTypeBuilder<Subscription> b)
    {
        b.ToTable("subscriptions");
        b.HasKey(s => s.Id);
        b.Property(s => s.Status).HasConversion<string>().HasMaxLength(20);
        b.Property(s => s.BillingProviderSubscriptionId).HasMaxLength(200);

        // Phase 6.6: Subscription is no longer 1:1 with Account (an account may have many
        // historical rows — cancelled, expired, refunded, then re-subscribed) — but at
        // most one LIVE (currently-effective) subscription must still exist per account.
        // A plain unique index would make re-subscription impossible; a partial/filtered
        // unique index enforces the real invariant at the database level instead of only
        // in application code. See docs/phase-6.6-billing-subscription.md "Subscription
        // Cardinality".
        b.HasIndex(s => s.AccountId)
            .IsUnique()
            .HasFilter("\"Status\" IN ('Trial','Active','PastDue','GracePeriod')")
            .HasDatabaseName("ix_subscriptions_account_live_unique");
        b.HasIndex(s => s.PlanId).HasDatabaseName("ix_subscriptions_plan");

        b.HasOne<Account>().WithMany().HasForeignKey(s => s.AccountId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Plan>().WithMany().HasForeignKey(s => s.PlanId).OnDelete(DeleteBehavior.Restrict);

        b.Property<uint>("xmin").IsRowVersion();
    }
}

public sealed class DeviceConfiguration : IEntityTypeConfiguration<Device>
{
    public void Configure(EntityTypeBuilder<Device> b)
    {
        b.ToTable("devices");
        b.HasKey(d => d.Id);
        b.Property(d => d.Platform).HasConversion<string>().HasMaxLength(20);
        b.Property(d => d.Status).HasConversion<string>().HasMaxLength(20);
        b.Property(d => d.DisplayName).HasMaxLength(200);

        // Primary lookup path (IDeviceRepository.ListByAccountAsync) — a device-management
        // screen or the entitlement/device-count check both filter by AccountId.
        b.HasIndex(d => d.AccountId).HasDatabaseName("ix_devices_account");

        b.HasOne<Account>().WithMany().HasForeignKey(d => d.AccountId).OnDelete(DeleteBehavior.Cascade);

        b.Property<uint>("xmin").IsRowVersion();
    }
}

public sealed class SessionConfiguration : IEntityTypeConfiguration<Session>
{
    public void Configure(EntityTypeBuilder<Session> b)
    {
        b.ToTable("sessions");
        b.HasKey(s => s.Id);
        b.Property(s => s.IpAddress).HasMaxLength(64);

        b.HasIndex(s => s.AccountId).HasDatabaseName("ix_sessions_account");
        b.HasIndex(s => s.DeviceId).HasDatabaseName("ix_sessions_device");

        b.HasOne<Account>().WithMany().HasForeignKey(s => s.AccountId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Device>().WithMany().HasForeignKey(s => s.DeviceId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class UsageRecordConfiguration : IEntityTypeConfiguration<UsageRecord>
{
    public void Configure(EntityTypeBuilder<UsageRecord> b)
    {
        b.ToTable("usage_records");
        b.HasKey(u => u.Id);
        b.Property(u => u.Direction).IsRequired().HasMaxLength(50);
        b.Property(u => u.Provider).HasMaxLength(50);
        b.Property(u => u.Source).HasConversion<string>().HasMaxLength(30);
        b.Property(u => u.PeriodBucket).IsRequired().HasMaxLength(20);

        // The only query this phase's application layer actually performs
        // (IUsageRecordRepository.ListByAccountAndPeriodAsync) — a composite index matches
        // it exactly, rather than indexing every column speculatively.
        b.HasIndex(u => new { u.AccountId, u.PeriodBucket }).HasDatabaseName("ix_usage_records_account_period");

        b.HasOne<Account>().WithMany().HasForeignKey(u => u.AccountId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Device>().WithMany().HasForeignKey(u => u.DeviceId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class AuditEventConfiguration : IEntityTypeConfiguration<AuditEvent>
{
    public void Configure(EntityTypeBuilder<AuditEvent> b)
    {
        b.ToTable("audit_events");
        b.HasKey(a => a.Id);
        b.Property(a => a.EventType).IsRequired().HasMaxLength(100);
        b.Property(a => a.Metadata).HasMaxLength(2000);

        // Audit lookups are always "this account's history over time" — matches the
        // expected access pattern named in the governing instruction directly.
        b.HasIndex(a => new { a.AccountId, a.OccurredAt }).HasDatabaseName("ix_audit_events_account_time");

        // Nullable FK (system-level events with no account) — Restrict, not Cascade:
        // an audit trail must never be silently deleted as a side effect of deleting the
        // account it references.
        b.HasOne<Account>().WithMany().HasForeignKey(a => a.AccountId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class BillingEventConfiguration : IEntityTypeConfiguration<BillingEvent>
{
    public void Configure(EntityTypeBuilder<BillingEvent> b)
    {
        b.ToTable("billing_events");
        b.HasKey(e => e.Id);
        b.Property(e => e.Provider).IsRequired().HasMaxLength(100);
        b.Property(e => e.ProviderEventId).IsRequired().HasMaxLength(200);
        b.Property(e => e.EventType).IsRequired().HasMaxLength(100);
        b.Property(e => e.ProcessingStatus).HasConversion<string>().HasMaxLength(20);
        b.Property(e => e.RejectionReason).HasMaxLength(500);
        b.Property(e => e.RawPayloadHash).IsRequired().HasMaxLength(128);

        // The entire idempotency mechanism (Phase 6.6) — a duplicate delivery is caught
        // as a unique-constraint violation on insert, never as an application-level
        // pre-check race.
        b.HasIndex(e => new { e.Provider, e.ProviderEventId }).IsUnique().HasDatabaseName("ix_billing_events_provider_event_unique");
        b.HasIndex(e => new { e.AccountId, e.ReceivedAt }).HasDatabaseName("ix_billing_events_account_time");

        // Nullable FKs, both Restrict — an event received for an account/subscription
        // that is later deleted must never be silently deleted itself (same discipline
        // as audit_events).
        b.HasOne<Account>().WithMany().HasForeignKey(e => e.AccountId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Subscription>().WithMany().HasForeignKey(e => e.SubscriptionId).OnDelete(DeleteBehavior.Restrict);
    }
}
