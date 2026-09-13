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

        // The existing ISubscriptionRepository.FindByAccountAsync contract returns a
        // single Subscription per account (Phase 6.3 in-memory repo keyed the same way)
        // — enforced here rather than left as an unstated application assumption.
        b.HasIndex(s => s.AccountId).IsUnique().HasDatabaseName("ix_subscriptions_account");
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
