using VTTranslate.Backend.Application.Identity;
using VTTranslate.Backend.Domain.Entities;
using VTTranslate.Backend.Domain.Enums;
using VTTranslate.Backend.Infrastructure.Persistence;

namespace VTTranslate.Backend.Tests;

public class AccountLifecycleServiceTests
{
    private readonly InMemoryAccountRepository _accounts = new();
    private readonly InMemoryAuditEventRepository _audit = new();
    private readonly FakeClock _clock = new();
    private readonly AccountLifecycleService _service;

    public AccountLifecycleServiceTests() => _service = new(_accounts, _audit, _clock);

    private async Task<Account> SeedAsync(AccountStatus status)
    {
        var account = new Account
        {
            Id = Guid.NewGuid(),
            Email = "lifecycle@example.com",
            EmailVerified = true,
            ExternalIdentityProvider = "EntraExternalId",
            ExternalSubjectId = Guid.NewGuid().ToString(),
            Role = Role.Customer,
            Status = status,
            CreatedAt = _clock.UtcNow,
        };
        await _accounts.SaveAsync(account, CancellationToken.None);
        return account;
    }

    [Theory]
    [InlineData(AccountStatus.Pending)]
    [InlineData(AccountStatus.Active)]
    [InlineData(AccountStatus.Suspended)]
    [InlineData(AccountStatus.Disabled)]
    public async Task Activate_FromNonTerminalState_Succeeds(AccountStatus from)
    {
        var account = await SeedAsync(from);
        var result = await _service.ActivateAsync(account.Id, CancellationToken.None);

        Assert.Equal(AccountLifecycleOutcome.Success, result.Outcome);
        Assert.Equal(AccountStatus.Active, result.Account!.Status);
    }

    [Fact]
    public async Task Activate_FromClosed_IsInvalidTransition()
    {
        var account = await SeedAsync(AccountStatus.Closed);
        var result = await _service.ActivateAsync(account.Id, CancellationToken.None);

        Assert.Equal(AccountLifecycleOutcome.InvalidTransition, result.Outcome);
        Assert.Equal(AccountStatus.Closed, result.Account!.Status); // unchanged
    }

    [Fact]
    public async Task Suspend_FromPending_IsInvalidTransition()
    {
        // Pending's only outward transitions are Active/Disabled/Closed, per the
        // approved matrix — Suspended is not one of them (Suspended is an
        // administrative hold on an otherwise-usable account, not a pre-usable one).
        var account = await SeedAsync(AccountStatus.Pending);
        var result = await _service.SuspendAsync(account.Id, CancellationToken.None);

        Assert.Equal(AccountLifecycleOutcome.InvalidTransition, result.Outcome);
    }

    [Fact]
    public async Task Suspend_FromActive_Succeeds()
    {
        var account = await SeedAsync(AccountStatus.Active);
        var result = await _service.SuspendAsync(account.Id, CancellationToken.None);

        Assert.Equal(AccountLifecycleOutcome.Success, result.Outcome);
        Assert.Equal(AccountStatus.Suspended, result.Account!.Status);
    }

    [Fact]
    public async Task Disable_FromSuspended_Succeeds()
    {
        var account = await SeedAsync(AccountStatus.Suspended);
        var result = await _service.DisableAsync(account.Id, CancellationToken.None);

        Assert.Equal(AccountLifecycleOutcome.Success, result.Outcome);
        Assert.Equal(AccountStatus.Disabled, result.Account!.Status);
    }

    [Fact]
    public async Task Close_FromAnyNonTerminalState_Succeeds()
    {
        foreach (var from in new[] { AccountStatus.Pending, AccountStatus.Active, AccountStatus.Suspended, AccountStatus.Disabled })
        {
            var account = await SeedAsync(from);
            var result = await _service.CloseAsync(account.Id, CancellationToken.None);
            Assert.Equal(AccountLifecycleOutcome.Success, result.Outcome);
            Assert.Equal(AccountStatus.Closed, result.Account!.Status);
        }
    }

    [Fact]
    public async Task Close_IsTerminal_NoTransitionOut()
    {
        var account = await SeedAsync(AccountStatus.Closed);

        Assert.Equal(AccountLifecycleOutcome.InvalidTransition, (await _service.ActivateAsync(account.Id, CancellationToken.None)).Outcome);
        Assert.Equal(AccountLifecycleOutcome.InvalidTransition, (await _service.SuspendAsync(account.Id, CancellationToken.None)).Outcome);
        Assert.Equal(AccountLifecycleOutcome.InvalidTransition, (await _service.DisableAsync(account.Id, CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task Close_RepeatedCall_IsIdempotent()
    {
        var account = await SeedAsync(AccountStatus.Active);
        var first = await _service.CloseAsync(account.Id, CancellationToken.None);
        var second = await _service.CloseAsync(account.Id, CancellationToken.None);

        Assert.Equal(AccountLifecycleOutcome.Success, first.Outcome);
        Assert.Equal(AccountLifecycleOutcome.Success, second.Outcome);
        // Only ONE AccountClosed audit event — the idempotent repeat does not re-audit.
        Assert.Single(_audit.Events, e => e.EventType == "AccountClosed" && e.AccountId == account.Id);
    }

    [Fact]
    public async Task Activate_RepeatedCall_OnAlreadyActive_IsIdempotentNoOp()
    {
        var account = await SeedAsync(AccountStatus.Active);
        var result = await _service.ActivateAsync(account.Id, CancellationToken.None);

        Assert.Equal(AccountLifecycleOutcome.Success, result.Outcome);
        Assert.Empty(_audit.Events.Where(e => e.EventType == "AccountActivated")); // no-op transition, no new audit
    }

    [Fact]
    public async Task NotFound_Account_ReturnsNotFound()
    {
        var result = await _service.SuspendAsync(Guid.NewGuid(), CancellationToken.None);
        Assert.Equal(AccountLifecycleOutcome.NotFound, result.Outcome);
    }

    [Fact]
    public async Task EveryTransition_EmitsCorrectAuditEvent()
    {
        var account = await SeedAsync(AccountStatus.Active);
        await _service.SuspendAsync(account.Id, CancellationToken.None);
        await _service.ActivateAsync(account.Id, CancellationToken.None);
        await _service.DisableAsync(account.Id, CancellationToken.None);
        await _service.ActivateAsync(account.Id, CancellationToken.None);
        await _service.CloseAsync(account.Id, CancellationToken.None);

        Assert.Contains(_audit.Events, e => e.EventType == "AccountSuspended");
        Assert.Contains(_audit.Events, e => e.EventType == "AccountActivated");
        Assert.Contains(_audit.Events, e => e.EventType == "AccountDisabled");
        Assert.Contains(_audit.Events, e => e.EventType == "AccountClosed");
    }

    [Fact]
    public async Task HistoricalAuditRecords_SurviveClosure()
    {
        var account = await SeedAsync(AccountStatus.Active);
        await _service.SuspendAsync(account.Id, CancellationToken.None);
        await _service.ActivateAsync(account.Id, CancellationToken.None);
        var eventsBeforeClose = _audit.Events.Count(e => e.AccountId == account.Id);

        await _service.CloseAsync(account.Id, CancellationToken.None);

        var eventsAfterClose = _audit.Events.Count(e => e.AccountId == account.Id);
        Assert.True(eventsAfterClose > eventsBeforeClose); // closure is additive, never destructive
        Assert.True(_audit.Events.Count(e => e.AccountId == account.Id) >= eventsBeforeClose);
    }
}
