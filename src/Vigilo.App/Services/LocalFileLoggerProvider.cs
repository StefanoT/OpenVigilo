using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Vigilo.App.Services;

internal sealed class LocalFileLoggerProvider : ILoggerProvider
{
    private readonly object _gate = new();
    private readonly string _path;
    private readonly AppLogStore _logStore;
    private readonly LogLevel _minimumLevel;
    private readonly LogLevel _storeMinimumLevel;
    private StreamWriter? _writer;
    private bool _disposed;

    public LocalFileLoggerProvider(
        string path,
        AppLogStore logStore,
        LogLevel minimumLevel = LogLevel.Trace,
        LogLevel storeMinimumLevel = LogLevel.Information)
    {
        _path = path;
        _logStore = logStore;
        _minimumLevel = minimumLevel;
        _storeMinimumLevel = storeMinimumLevel;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _writer = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
        {
            AutoFlush = true
        };
    }

    public ILogger CreateLogger(string categoryName) => new LocalFileLogger(this, categoryName);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _writer?.Dispose();
            _writer = null;
        }
    }

    public static void DeleteOldLogs(string logDirectory, int retentionDays)
    {
        if (!Directory.Exists(logDirectory))
        {
            return;
        }

        var cutoff = DateTimeOffset.Now.AddDays(-retentionDays);
        foreach (var file in Directory.EnumerateFiles(logDirectory, "vigilo-*.*"))
        {
            if (!file.EndsWith(".log", StringComparison.OrdinalIgnoreCase)
                && !file.EndsWith(".debug-inferences.jsonl", StringComparison.OrdinalIgnoreCase)
                && !file.EndsWith(".debug-decisions.jsonl", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                if (File.GetLastWriteTime(file) < cutoff)
                {
                    File.Delete(file);
                }
            }
            catch
            {
                // Logging cleanup must never prevent application startup.
            }
        }
    }

    private void Write<TState>(
        string categoryName,
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        var timestamp = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture);
        var message = formatter(state, exception);
        var entry = new StringBuilder()
            .Append(timestamp)
            .Append(" [")
            .Append(logLevel)
            .Append("] ")
            .Append(categoryName);

        if (eventId.Id != 0 || !string.IsNullOrWhiteSpace(eventId.Name))
        {
            entry.Append(" (").Append(eventId.Id);
            if (!string.IsNullOrWhiteSpace(eventId.Name))
            {
                entry.Append(':').Append(eventId.Name);
            }

            entry.Append(')');
        }

        entry.Append(": ").AppendLine(message);
        if (exception is not null)
        {
            entry.AppendLine(exception.ToString());
        }

        var formattedEntry = entry.ToString();

        lock (_gate)
        {
            if (_disposed || _writer is null)
            {
                return;
            }

            _writer.Write(formattedEntry);
        }

        if (logLevel >= _storeMinimumLevel)
        {
            _logStore.Append(formattedEntry);
        }
    }

    private bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && logLevel >= _minimumLevel;

    private sealed class LocalFileLogger(LocalFileLoggerProvider provider, string categoryName) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => provider.IsEnabled(logLevel);

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            provider.Write(categoryName, logLevel, eventId, state, exception, formatter);
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
