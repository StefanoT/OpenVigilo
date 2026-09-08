using Vigilo.Core;

namespace Vigilo.Storage;

internal sealed class SettingsTransactionRecoveryBootstrapper(
    SettingsTransactionRecoveryService recoveryService) : IAppBootstrapper
{
    public Task InitializeAsync(CancellationToken cancellationToken) =>
        recoveryService.RecoverPendingAsync(cancellationToken);
}
