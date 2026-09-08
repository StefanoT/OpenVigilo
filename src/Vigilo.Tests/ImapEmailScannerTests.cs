using Vigilo.Core;
using Vigilo.Email;

namespace Vigilo.Tests;

public sealed class ImapEmailScannerTests
{
    [Fact]
    public async Task ScanAccountsAsync_continues_after_an_account_fails()
    {
        var accounts = new[]
        {
            new EmailAccount { Name = "Broken" },
            new EmailAccount { Name = "Healthy" }
        };
        var attemptedAccounts = new List<Guid>();
        var reportedFailures = new List<Guid>();

        var result = await ImapEmailScanner.ScanAccountsAsync(
            accounts,
            (account, _, _, _) =>
            {
                attemptedAccounts.Add(account.Id);
                return account.Id == accounts[0].Id
                    ? Task.FromException<ScanResult>(new IOException("Mailbox unavailable."))
                    : Task.FromResult(new ScanResult(1, 1, 0, 0));
            },
            (account, _, _, _) => reportedFailures.Add(account.Id),
            CancellationToken.None);

        Assert.Equal(accounts.Select(account => account.Id), attemptedAccounts);
        Assert.Equal(new[] { accounts[0].Id }, reportedFailures);
        Assert.Equal(new ScanResult(1, 1, 0, 1), result);
    }

    [Fact]
    public async Task ScanAccountsAsync_preserves_caller_cancellation()
    {
        var accounts = new[]
        {
            new EmailAccount { Name = "First" },
            new EmailAccount { Name = "Second" }
        };
        using var cancellation = new CancellationTokenSource();
        var attemptedAccounts = new List<Guid>();
        var failureReported = false;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ImapEmailScanner.ScanAccountsAsync(
                accounts,
                (account, _, _, token) =>
                {
                    attemptedAccounts.Add(account.Id);
                    cancellation.Cancel();
                    return Task.FromCanceled<ScanResult>(token);
                },
                (_, _, _, _) => failureReported = true,
                cancellation.Token));

        Assert.Equal(new[] { accounts[0].Id }, attemptedAccounts);
        Assert.False(failureReported);
    }
}
