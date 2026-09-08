using System.IO;

namespace Vigilo.App.Services;

public sealed class AppLogStore
{
    private const int MaxDisplayedLines = 10_000;

    private readonly object _gate = new();
    private readonly Queue<string> _lines = new();

    public event EventHandler? Changed;

    public string Text
    {
        get
        {
            lock (_gate)
            {
                return string.Join(Environment.NewLine, _lines);
            }
        }
    }

    internal void Append(string entry)
    {
        lock (_gate)
        {
            using var reader = new StringReader(entry);
            while (reader.ReadLine() is { } line)
            {
                _lines.Enqueue(line);
            }

            while (_lines.Count > MaxDisplayedLines)
            {
                _lines.Dequeue();
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}
