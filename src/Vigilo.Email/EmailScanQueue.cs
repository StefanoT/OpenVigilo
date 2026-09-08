using System.Threading.Channels;
using Vigilo.Core;

namespace Vigilo.Email;

internal sealed class EmailScanQueue : IEmailScanQueue
{
    private readonly Channel<QueuedEmailScan> _requests = Channel.CreateUnbounded<QueuedEmailScan>(
        new UnboundedChannelOptions
        {
            SingleReader = true
        });

    public async Task<ScanResult> QueueScanAsync(
        CancellationToken cancellationToken,
        IProgress<EmailScanProgress>? progress = null)
    {
        var request = new QueuedEmailScan(progress);
        try
        {
            await _requests.Writer.WriteAsync(request, cancellationToken);
        }
        catch
        {
            request.Dispose();
            throw;
        }

        using var cancellationRegistration = cancellationToken.Register(
            static state => ((QueuedEmailScan)state!).Cancel(),
            request);
        return await request.Completion.Task.WaitAsync(cancellationToken);
    }

    public bool TryDequeue(out QueuedEmailScan request) => _requests.Reader.TryRead(out request!);

    public ValueTask<QueuedEmailScan> DequeueAsync(CancellationToken cancellationToken) =>
        _requests.Reader.ReadAsync(cancellationToken);
}

internal sealed class QueuedEmailScan(IProgress<EmailScanProgress>? progress) : IDisposable
{
    private readonly CancellationTokenSource _cancellationTokenSource = new();

    public IProgress<EmailScanProgress>? Progress { get; } = progress;

    public CancellationToken CancellationToken => _cancellationTokenSource.Token;

    public TaskCompletionSource<ScanResult> Completion { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool IsCancellationRequested => _cancellationTokenSource.IsCancellationRequested;

    public void Cancel()
    {
        _cancellationTokenSource.Cancel();
        Completion.TrySetCanceled(_cancellationTokenSource.Token);
    }

    public void SetResult(ScanResult result) => Completion.TrySetResult(result);

    public void SetException(Exception exception) => Completion.TrySetException(exception);

    public void SetCanceled(CancellationToken cancellationToken) => Completion.TrySetCanceled(cancellationToken);

    public void Dispose() => _cancellationTokenSource.Dispose();
}
