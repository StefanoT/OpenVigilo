using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using MailKit;
using MailKit.Net.Imap;
using Vigilo.Core;

namespace Vigilo.Email;

internal sealed class EmailMonitorService(
    IServiceScopeFactory scopeFactory,
    EmailScanQueue scanQueue,
    ILogger<EmailMonitorService> logger) : BackgroundService
{
    private static readonly TimeSpan IdleReconnectDelay = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var runScheduledScan = true;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var accountSettings = scope.ServiceProvider.GetRequiredService<IAccountSettingsService>();
                var scanner = scope.ServiceProvider.GetRequiredService<IEmailScanner>();
                var maintenance = scope.ServiceProvider.GetRequiredService<ILocalDataMaintenanceService>();
                var sentReplyDetector = scope.ServiceProvider.GetRequiredService<ISentReplyDetectionService>();

                var ranQueuedScan = false;
                while (scanQueue.TryDequeue(out var queuedScan))
                {
                    await RunQueuedScanAsync(queuedScan, scanner, stoppingToken);
                    ranQueuedScan = true;
                }

                if (ranQueuedScan)
                {
                    runScheduledScan = false;
                }

                var accounts = await GetAccountsToMonitorAsync(accountSettings, stoppingToken);
                if (runScheduledScan)
                {
                    await RunScheduledScanAsync(scanner, sentReplyDetector, maintenance, stoppingToken);
                }

                runScheduledScan = true;

                var nextManualScan = await WaitForNextScanAsync(accounts, accountSettings, scanner, stoppingToken);
                if (nextManualScan is not null)
                {
                    await RunQueuedScanAsync(nextManualScan, scanner, stoppingToken);
                    runScheduledScan = false;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Background email scan failed.");
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
        }
    }

    private async Task RunScheduledScanAsync(
        IEmailScanner scanner,
        ISentReplyDetectionService sentReplyDetector,
        ILocalDataMaintenanceService maintenance,
        CancellationToken stoppingToken)
    {
        await scanner.ScanAsync(stoppingToken);
        await sentReplyDetector.DetectSentRepliesAsync(stoppingToken);
        await maintenance.ApplyRetentionAsync(stoppingToken);
    }

    private async Task RunQueuedScanAsync(
        QueuedEmailScan queuedScan,
        IEmailScanner scanner,
        CancellationToken stoppingToken)
    {
        using (queuedScan)
        {
            if (queuedScan.IsCancellationRequested)
            {
                return;
            }

            try
            {
                using var scanCts = CancellationTokenSource.CreateLinkedTokenSource(
                    queuedScan.CancellationToken,
                    stoppingToken);
                var result = await scanner.ScanAsync(scanCts.Token, queuedScan.Progress);
                queuedScan.SetResult(result);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                queuedScan.SetCanceled(stoppingToken);
                throw;
            }
            catch (OperationCanceledException) when (queuedScan.IsCancellationRequested)
            {
                queuedScan.SetCanceled(queuedScan.CancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Manual email scan failed.");
                queuedScan.SetException(ex);
            }
        }
    }

    private static async Task<IReadOnlyList<EmailAccount>> GetAccountsToMonitorAsync(
        IAccountSettingsService accountSettings,
        CancellationToken cancellationToken)
    {
        var accounts = await accountSettings.GetAccountsAsync(cancellationToken);
        if (accounts.Count > 0)
        {
            return accounts;
        }

        return [await accountSettings.GetOrCreateDefaultAccountAsync(cancellationToken)];
    }

    private async Task<QueuedEmailScan?> WaitForNextScanAsync(
        IReadOnlyList<EmailAccount> accounts,
        IAccountSettingsService accountSettings,
        IEmailScanner scanner,
        CancellationToken stoppingToken)
    {
        using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var scheduledScanTask = WaitForNextScheduledScanAsync(accounts, accountSettings, scanner, waitCts.Token);
        var manualScanTask = scanQueue.DequeueAsync(waitCts.Token).AsTask();

        try
        {
            var completedTask = await Task.WhenAny(scheduledScanTask, manualScanTask);
            if (completedTask == manualScanTask)
            {
                return await manualScanTask;
            }

            await scheduledScanTask;
            return null;
        }
        finally
        {
            await waitCts.CancelAsync();
            await ObserveExpectedCancellationAsync(scheduledScanTask, stoppingToken);
            await ObserveExpectedCancellationAsync(manualScanTask, stoppingToken);
        }
    }

    private async Task WaitForNextScheduledScanAsync(
        IReadOnlyList<EmailAccount> accounts,
        IAccountSettingsService accountSettings,
        IEmailScanner scanner,
        CancellationToken stoppingToken)
    {
        if (accounts.Count == 0)
        {
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            return;
        }

        using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var waitTasks = accounts
            .Select(account => WaitForNextScanAsync(account, accountSettings, scanner, waitCts.Token))
            .ToArray();

        try
        {
            await await Task.WhenAny(waitTasks);
        }
        finally
        {
            await waitCts.CancelAsync();
            try
            {
                await Task.WhenAll(waitTasks);
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                // Another account woke the scan loop.
            }
        }
    }

    private static async Task ObserveExpectedCancellationAsync(Task task, CancellationToken stoppingToken)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task WaitForNextScanAsync(
        EmailAccount account,
        IAccountSettingsService accountSettings,
        IEmailScanner scanner,
        CancellationToken stoppingToken)
    {
        if (!account.UseImapIdle)
        {
            await Task.Delay(TimeSpan.FromMinutes(Math.Max(1, account.PollingFallbackMinutes)), stoppingToken);
            return;
        }

        var password = await accountSettings.GetPasswordAsync(account.Id, stoppingToken);
        if (string.IsNullOrWhiteSpace(password))
        {
            await Task.Delay(TimeSpan.FromMinutes(Math.Max(1, account.PollingFallbackMinutes)), stoppingToken);
            return;
        }

        var folderName = account.Folders.FirstOrDefault() ?? "Inbox";
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var client = new ImapClient();
                await ImapOperation.ExecuteWithTimeoutAsync(
                    token => client.ConnectAsync(account.ImapHost, account.ImapPort, account.UseSsl, token),
                    "connection",
                    ImapOperation.ConnectionTimeout,
                    stoppingToken);
                await ImapOperation.ExecuteWithTimeoutAsync(
                    token => client.AuthenticateAsync(account.Username, password, token),
                    "authentication",
                    ImapOperation.ConnectionTimeout,
                    stoppingToken);

                if (!client.Capabilities.HasFlag(ImapCapabilities.Idle))
                {
                    await client.DisconnectAsync(true, stoppingToken);
                    await Task.Delay(TimeSpan.FromMinutes(Math.Max(1, account.PollingFallbackMinutes)), stoppingToken);
                    return;
                }

                var folder = await client.GetFolderAsync(folderName, stoppingToken);
                await folder.OpenAsync(FolderAccess.ReadOnly, stoppingToken);

                using var idleDone = new CancellationTokenSource(TimeSpan.FromMinutes(Math.Max(1, account.PollingFallbackMinutes)));
                using var linkedDone = CancellationTokenSource.CreateLinkedTokenSource(idleDone.Token, stoppingToken);
                var countChanged = false;
                folder.CountChanged += OnCountChanged;
                try
                {
                    await client.IdleAsync(linkedDone.Token, stoppingToken);
                }
                catch (OperationCanceledException) when (idleDone.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
                {
                    // Polling interval elapsed while IDLE was active.
                }
                finally
                {
                    folder.CountChanged -= OnCountChanged;
                }

                if (countChanged && !stoppingToken.IsCancellationRequested)
                {
                    await scanner.ScanFolderAsync(account, folderName, stoppingToken);
                }

                await client.DisconnectAsync(true, stoppingToken);
                return;

                void OnCountChanged(object? sender, EventArgs args)
                {
                    countChanged = true;
                    idleDone.Cancel();
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "IMAP IDLE failed for account {AccountName} ({AccountId}); retrying in {RetryDelaySeconds} seconds.",
                    account.Name,
                    account.Id,
                    IdleReconnectDelay.TotalSeconds);
                await Task.Delay(IdleReconnectDelay, stoppingToken);
                await scanner.ScanFolderAsync(account, folderName, stoppingToken);
            }
        }
    }

}
