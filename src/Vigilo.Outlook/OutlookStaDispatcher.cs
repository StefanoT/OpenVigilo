using System.Collections.Concurrent;
using Vigilo.Core;

namespace Vigilo.Outlook;

public sealed class OutlookStaDispatcher : IOutlookStaDispatcher
{
    private readonly BlockingCollection<IWorkItem> _queue = new();
    private readonly Thread _thread;
    private bool _disposed;

    public OutlookStaDispatcher()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "Vigilo Outlook COM STA" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public Task<T> InvokeAsync<T>(Func<T> action, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        var item = new WorkItem<T>(action, cancellationToken);
        _queue.Add(item, cancellationToken);
        return item.Task;
    }

    public Task InvokeAsync(Action action, CancellationToken cancellationToken) =>
        InvokeAsync(() => { action(); return true; }, cancellationToken);

    private void Run()
    {
        foreach (var item in _queue.GetConsumingEnumerable()) item.Execute();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _queue.CompleteAdding();
        await Task.Run(_thread.Join);
        _queue.Dispose();
    }

    private interface IWorkItem { void Execute(); }

    private sealed class WorkItem<T>(Func<T> action, CancellationToken cancellationToken) : IWorkItem
    {
        private readonly TaskCompletionSource<T> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<T> Task => _completion.Task;

        public void Execute()
        {
            if (cancellationToken.IsCancellationRequested)
            {
                _completion.TrySetCanceled(cancellationToken);
                return;
            }
            try { _completion.TrySetResult(action()); }
            catch (Exception ex) { _completion.TrySetException(ex); }
        }
    }
}
