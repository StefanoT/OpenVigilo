using Vigilo.Core;

namespace Vigilo.Email;

internal sealed class EmailScanProgressNotifier : IEmailScanProgressNotifier
{
    private readonly object _lock = new();
    private Action<EmailScanProgress>? _progressChanged;

    public event Action<EmailScanProgress>? ProgressChanged
    {
        add
        {
            lock (_lock)
            {
                _progressChanged += value;
            }
        }
        remove
        {
            lock (_lock)
            {
                _progressChanged -= value;
            }
        }
    }

    public void NotifyProgress(EmailScanProgress progress)
    {
        Action<EmailScanProgress>? handler;
        lock (_lock)
        {
            handler = _progressChanged;
        }

        handler?.Invoke(progress);
    }
}
