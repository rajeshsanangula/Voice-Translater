using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace VTTranslate.Backend.Infrastructure.Persistence.EfCore;

/// <summary>
/// Design-time-only factory used by `dotnet ef migrations add/remove` so migrations can
/// be authored without running the API host. Never used at application runtime (the API
/// registers <see cref="AutraxisDbContext"/> itself, from real configuration — see
/// Program.cs). The connection string here is used ONLY to let the Npgsql provider
/// generate migration SQL that targets PostgreSQL syntax; no design-time command
/// connects to a real database or reads real configuration/secrets.
/// </summary>
public sealed class AutraxisDbContextFactory : IDesignTimeDbContextFactory<AutraxisDbContext>
{
    public AutraxisDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<AutraxisDbContext>();
        optionsBuilder.UseNpgsql("Host=localhost;Database=autraxis_design_time_only;Username=postgres;Password=postgres");
        return new AutraxisDbContext(optionsBuilder.Options);
    }
}
