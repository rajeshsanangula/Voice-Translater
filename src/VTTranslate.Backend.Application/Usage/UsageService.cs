using VTTranslate.Backend.Domain.Abstractions;
using VTTranslate.Backend.Domain.Entities;
using VTTranslate.Backend.Domain.Enums;

namespace VTTranslate.Backend.Application.Usage;

public sealed class UsageService(IUsageRecordRepository records, IClock clock) : IUsageService
{
    public Task RecordServerDerivedUsageAsync(Guid accountId, Guid deviceId, string direction, double secondsUsed, string? provider, CancellationToken ct) =>
        AddAsync(accountId, deviceId, direction, secondsUsed, provider, UsageRecordSource.ServerDerived, ct);

    public Task RecordClientReportedHintAsync(Guid accountId, Guid deviceId, string direction, double secondsUsed, CancellationToken ct) =>
        AddAsync(accountId, deviceId, direction, secondsUsed, provider: null, UsageRecordSource.ClientReportedHint, ct);

    public async Task<double> GetAuthoritativeUsageSecondsAsync(Guid accountId, string periodBucket, CancellationToken ct)
    {
        var all = await records.ListByAccountAndPeriodAsync(accountId, periodBucket, ct);
        return all.Where(r => r.Source == UsageRecordSource.ServerDerived).Sum(r => r.SecondsUsed);
    }

    public async Task<UsageSummary> GetSummaryAsync(Guid accountId, string periodBucket, CancellationToken ct)
    {
        var all = await records.ListByAccountAndPeriodAsync(accountId, periodBucket, ct);
        var serverDerived = all.Where(r => r.Source == UsageRecordSource.ServerDerived).Sum(r => r.SecondsUsed);
        var clientReported = all.Where(r => r.Source == UsageRecordSource.ClientReportedHint).Sum(r => r.SecondsUsed);
        return new UsageSummary(periodBucket, serverDerived, clientReported);
    }

    private async Task AddAsync(Guid accountId, Guid deviceId, string direction, double secondsUsed, string? provider, UsageRecordSource source, CancellationToken ct)
    {
        if (secondsUsed < 0)
            throw new ArgumentOutOfRangeException(nameof(secondsUsed), "usage duration cannot be negative");

        var record = new UsageRecord
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            DeviceId = deviceId,
            Direction = direction,
            SecondsUsed = secondsUsed,
            Provider = provider,
            Source = source,
            PeriodBucket = clock.UtcNow.ToString("yyyy-MM"),
            RecordedAt = clock.UtcNow,
        };
        await records.AddAsync(record, ct);
    }
}
