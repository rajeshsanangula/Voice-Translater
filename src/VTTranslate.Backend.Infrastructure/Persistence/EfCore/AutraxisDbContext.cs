using Microsoft.EntityFrameworkCore;
using VTTranslate.Backend.Domain.Entities;

namespace VTTranslate.Backend.Infrastructure.Persistence.EfCore;

/// <summary>
/// Phase 6.5 production persistence — the ONLY place Entity Framework Core / Npgsql
/// types appear in this codebase. Domain entities are plain POCOs with zero EF
/// attributes and no base class; every mapping decision (table names, keys, indexes,
/// concurrency tokens) lives in the <see cref="Configurations"/> classes referenced from
/// <see cref="OnModelCreating"/>, never on the Domain types themselves.
/// </summary>
public sealed class AutraxisDbContext(DbContextOptions<AutraxisDbContext> options) : DbContext(options)
{
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<Profile> Profiles => Set<Profile>();
    public DbSet<Plan> Plans => Set<Plan>();
    public DbSet<Entitlement> Entitlements => Set<Entitlement>();
    public DbSet<Subscription> Subscriptions => Set<Subscription>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<Session> Sessions => Set<Session>();
    public DbSet<UsageRecord> UsageRecords => Set<UsageRecord>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AutraxisDbContext).Assembly);
    }
}
