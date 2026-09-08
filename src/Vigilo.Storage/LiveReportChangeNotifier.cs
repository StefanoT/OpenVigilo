using Vigilo.Core;

namespace Vigilo.Storage;

public sealed class LiveReportChangeNotifier
{
    private readonly object _lock = new();
    private EventHandler<LiveReportChangedEventArgs>? _reportChanged;

    public event EventHandler<LiveReportChangedEventArgs>? ReportChanged
    {
        add
        {
            lock (_lock)
            {
                _reportChanged += value;
            }
        }
        remove
        {
            lock (_lock)
            {
                _reportChanged -= value;
            }
        }
    }

    public void NotifyChanged(object sender, LiveReportChange changes)
    {
        if (changes == LiveReportChange.None)
        {
            return;
        }

        EventHandler<LiveReportChangedEventArgs>? handler;
        lock (_lock)
        {
            handler = _reportChanged;
        }

        handler?.Invoke(sender, new LiveReportChangedEventArgs(changes));
    }
}
