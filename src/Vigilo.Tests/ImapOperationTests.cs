using Vigilo.Email;

namespace Vigilo.Tests;

public sealed class ImapOperationTests
{
    [Fact]
    public async Task ExecuteWithTimeoutAsync_stops_a_stuck_operation()
    {
        var exception = await Assert.ThrowsAsync<TimeoutException>(() =>
            ImapOperation.ExecuteWithTimeoutAsync(
                token => Task.Delay(Timeout.InfiniteTimeSpan, token),
                "connection",
                TimeSpan.FromMilliseconds(20),
                CancellationToken.None));

        Assert.Contains("IMAP connection timed out", exception.Message);
    }

    [Fact]
    public async Task ExecuteWithTimeoutAsync_preserves_caller_cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ImapOperation.ExecuteWithTimeoutAsync(
                token => Task.Delay(Timeout.InfiniteTimeSpan, token),
                "connection",
                TimeSpan.FromMinutes(1),
                cancellation.Token));
    }
}
