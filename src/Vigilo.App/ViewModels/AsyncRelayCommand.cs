using System.Windows.Input;
using Microsoft.Extensions.Logging;

namespace Vigilo.App.ViewModels;

public sealed class AsyncRelayCommand(
    Func<object?, Task> execute,
    Predicate<object?>? canExecute = null,
    ILogger? logger = null,
    string? commandName = null) : ICommand
{
    private bool _isRunning;

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => !_isRunning && (canExecute?.Invoke(parameter) ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        _isRunning = true;
        RaiseCanExecuteChanged();
        var startedAt = DateTimeOffset.UtcNow;
        var name = commandName ?? execute.Method.Name;
        logger?.LogDebug("UI command {CommandName} started.", name);
        try
        {
            await execute(parameter);
            logger?.LogInformation(
                "UI command {CommandName} completed in {ElapsedMilliseconds} ms.",
                name,
                (DateTimeOffset.UtcNow - startedAt).TotalMilliseconds);
        }
        catch (Exception ex)
        {
            logger?.LogError(
                ex,
                "UI command {CommandName} failed after {ElapsedMilliseconds} ms.",
                name,
                (DateTimeOffset.UtcNow - startedAt).TotalMilliseconds);
        }
        finally
        {
            _isRunning = false;
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
