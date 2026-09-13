using VTTranslate.Backend.Application.Usage;
using VTTranslate.Backend.Infrastructure.Persistence;

namespace VTTranslate.Backend.Tests;

public class UsageServiceTests
{
    private readonly InMemoryUsageRecordRepository _records = new();
    private readonly FakeClock _clock = new();
    private readonly UsageService _service;

    public UsageServiceTests()
    {
        _service = new UsageService(_records, _clock);
    }

    [Fact]
    public async Task RecordServerDerivedUsage_CountsTowardAuthoritativeTotal()
    {
        var accountId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();

        await _service.RecordServerDerivedUsageAsync(accountId, deviceId, "en-US:de-DE", 30, "AzureSpeech", CancellationToken.None);
        await _service.RecordServerDerivedUsageAsync(accountId, deviceId, "de-DE:en-US", 15, "AzureSpeech", CancellationToken.None);

        var total = await _service.GetAuthoritativeUsageSecondsAsync(accountId, "2026-01", CancellationToken.None);
        Assert.Equal(45, total);
    }

    [Fact]
    public async Task RecordClientReportedHint_NeverAffectsAuthoritativeTotal()
    {
        var accountId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();

        await _service.RecordClientReportedHintAsync(accountId, deviceId, "en-US:de-DE", 999, CancellationToken.None);

        var total = await _service.GetAuthoritativeUsageSecondsAsync(accountId, "2026-01", CancellationToken.None);
        Assert.Equal(0, total);
    }

    [Fact]
    public async Task GetSummary_ReportsBothSourcesSeparately()
    {
        var accountId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();

        await _service.RecordServerDerivedUsageAsync(accountId, deviceId, "en-US:de-DE", 40, "AzureSpeech", CancellationToken.None);
        await _service.RecordClientReportedHintAsync(accountId, deviceId, "en-US:de-DE", 55, CancellationToken.None);

        var summary = await _service.GetSummaryAsync(accountId, "2026-01", CancellationToken.None);

        Assert.Equal(40, summary.ServerDerivedSeconds);
        Assert.Equal(55, summary.ClientReportedSeconds);
    }

    [Fact]
    public async Task DifferentAccounts_AreNotConflated()
    {
        var accountA = Guid.NewGuid();
        var accountB = Guid.NewGuid();
        var deviceId = Guid.NewGuid();

        await _service.RecordServerDerivedUsageAsync(accountA, deviceId, "en-US:de-DE", 10, null, CancellationToken.None);
        await _service.RecordServerDerivedUsageAsync(accountB, deviceId, "en-US:de-DE", 20, null, CancellationToken.None);

        Assert.Equal(10, await _service.GetAuthoritativeUsageSecondsAsync(accountA, "2026-01", CancellationToken.None));
        Assert.Equal(20, await _service.GetAuthoritativeUsageSecondsAsync(accountB, "2026-01", CancellationToken.None));
    }

    [Fact]
    public async Task DifferentPeriods_AreNotConflated()
    {
        var accountId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();
        await _service.RecordServerDerivedUsageAsync(accountId, deviceId, "en-US:de-DE", 10, null, CancellationToken.None);

        _clock.UtcNow = _clock.UtcNow.AddMonths(1);
        await _service.RecordServerDerivedUsageAsync(accountId, deviceId, "en-US:de-DE", 99, null, CancellationToken.None);

        Assert.Equal(10, await _service.GetAuthoritativeUsageSecondsAsync(accountId, "2026-01", CancellationToken.None));
        Assert.Equal(99, await _service.GetAuthoritativeUsageSecondsAsync(accountId, "2026-02", CancellationToken.None));
    }

    [Fact]
    public async Task NegativeDuration_IsRejected()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            _service.RecordServerDerivedUsageAsync(Guid.NewGuid(), Guid.NewGuid(), "en-US:de-DE", -1, null, CancellationToken.None));
    }
}
