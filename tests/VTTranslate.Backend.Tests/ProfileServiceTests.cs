using VTTranslate.Backend.Application.Profiles;
using VTTranslate.Backend.Infrastructure.Persistence;

namespace VTTranslate.Backend.Tests;

public class ProfileServiceTests
{
    private readonly InMemoryProfileRepository _profiles = new();
    private readonly FakeClock _clock = new();
    private readonly ProfileService _service;

    public ProfileServiceTests() => _service = new(_profiles, _clock);

    [Fact]
    public async Task Get_NoProfileYet_ReturnsEmptyViewRatherThanThrowing()
    {
        var view = await _service.GetAsync(Guid.NewGuid(), CancellationToken.None);
        Assert.Null(view.DisplayName);
        Assert.Null(view.PreferredLanguagePair);
    }

    [Fact]
    public async Task Update_ValidDisplayName_Succeeds()
    {
        var accountId = Guid.NewGuid();
        var result = await _service.UpdateAsync(accountId, "Sanan", null, CancellationToken.None);

        Assert.Equal(ProfileUpdateOutcome.Success, result.Outcome);
        Assert.Equal("Sanan", result.Profile!.DisplayName);
    }

    [Fact]
    public async Task Update_ValidLanguagePair_Succeeds()
    {
        var accountId = Guid.NewGuid();
        var result = await _service.UpdateAsync(accountId, null, "en-US:de-DE", CancellationToken.None);

        Assert.Equal(ProfileUpdateOutcome.Success, result.Outcome);
        Assert.Equal("en-US:de-DE", result.Profile!.PreferredLanguagePair);
    }

    [Fact]
    public async Task Update_InvalidLanguagePairShape_Rejected()
    {
        var accountId = Guid.NewGuid();
        var result = await _service.UpdateAsync(accountId, null, "not-a-valid-pair", CancellationToken.None);

        Assert.Equal(ProfileUpdateOutcome.ValidationFailed, result.Outcome);
    }

    [Fact]
    public async Task Update_DisplayNameTooLong_Rejected()
    {
        var accountId = Guid.NewGuid();
        var tooLong = new string('a', 201);
        var result = await _service.UpdateAsync(accountId, tooLong, null, CancellationToken.None);

        Assert.Equal(ProfileUpdateOutcome.ValidationFailed, result.Outcome);
    }

    [Fact]
    public async Task Update_SetsServerControlledTimestamp()
    {
        var accountId = Guid.NewGuid();
        _clock.UtcNow = new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero);

        var result = await _service.UpdateAsync(accountId, "Name", null, CancellationToken.None);
        Assert.Equal(_clock.UtcNow, result.Profile!.UpdatedAt);
    }

    [Fact]
    public async Task Update_ThenGet_RoundTrips()
    {
        var accountId = Guid.NewGuid();
        await _service.UpdateAsync(accountId, "Round Trip", "en-US:de-DE", CancellationToken.None);

        var view = await _service.GetAsync(accountId, CancellationToken.None);
        Assert.Equal("Round Trip", view.DisplayName);
        Assert.Equal("en-US:de-DE", view.PreferredLanguagePair);
    }

    [Fact]
    public async Task Update_DifferentAccounts_NeverCrossContaminate()
    {
        var accountA = Guid.NewGuid();
        var accountB = Guid.NewGuid();

        await _service.UpdateAsync(accountA, "Account A", null, CancellationToken.None);
        await _service.UpdateAsync(accountB, "Account B", null, CancellationToken.None);

        var viewA = await _service.GetAsync(accountA, CancellationToken.None);
        var viewB = await _service.GetAsync(accountB, CancellationToken.None);

        Assert.Equal("Account A", viewA.DisplayName);
        Assert.Equal("Account B", viewB.DisplayName);
    }
}
