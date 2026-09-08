using System.Windows;
using Vigilo.Core;

namespace Vigilo.App.Services;

public sealed class WpfUserApprovalService : IUserApprovalService
{
    public Task<bool> ConfirmAsync(string title, string message, CancellationToken cancellationToken)
    {
        var result = System.Windows.MessageBox.Show(
            message,
            title,
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        return Task.FromResult(result == MessageBoxResult.Yes);
    }
}
