namespace Vigilo.Email;

internal static class ImapOperation
{
    internal static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(60);

    internal static async Task ExecuteWithTimeoutAsync(
        Func<CancellationToken, Task> operation,
        string operationName,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        try
        {
            await operation(linkedCts.Token);
        }
        catch (OperationCanceledException ex) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"IMAP {operationName} timed out after {timeout.TotalSeconds:0} seconds.", ex);
        }
    }
}
