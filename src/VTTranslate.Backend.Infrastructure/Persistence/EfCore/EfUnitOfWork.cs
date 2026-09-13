using Microsoft.EntityFrameworkCore.Storage;
using VTTranslate.Backend.Domain.Abstractions;

namespace VTTranslate.Backend.Infrastructure.Persistence.EfCore;

/// <summary>
/// Phase 6.6 — wraps a real PostgreSQL transaction around a delegate that may call
/// SaveAsync on multiple repositories backed by the same (scoped) AutraxisDbContext.
/// Because every EfXxxRepository.SaveAsync call in this codebase calls
/// db.SaveChangesAsync() on the SAME scoped context instance, an ambient
/// Database.BeginTransactionAsync() here makes those calls commit or roll back together
/// — no repository code itself needs to know a transaction is in progress.
/// </summary>
public sealed class EfUnitOfWork(AutraxisDbContext db) : IUnitOfWork
{
    public async Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken ct)
    {
        await using IDbContextTransaction transaction = await db.Database.BeginTransactionAsync(ct);
        var result = await operation(ct);
        await transaction.CommitAsync(ct);
        return result;
    }
}
