using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Vigilo.Core;

namespace Vigilo.Outlook;

public sealed class OutlookCategorySyncWorker(
    IServiceScopeFactory scopeFactory,
    IOutlookClient client,
    ILogger<OutlookCategorySyncWorker> logger) : BackgroundService
{
    private readonly HashSet<string> _initializedStores = new(StringComparer.Ordinal);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RecoverAsync(stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        do
        {
            try { await ProcessCycleAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "Outlook synchronization cycle failed without affecting email classification."); }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task RecoverAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IOutlookSyncStore>();
        var recovered = await store.RecoverStaleInProgressAsync(DateTimeOffset.UtcNow.AddMinutes(-10), cancellationToken);
        if (recovered > 0) logger.LogInformation("Recovered stale Outlook synchronization operations. Count={Count}", recovered);
    }

    internal async Task ProcessCycleAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IOutlookSyncStore>();
        var due = await store.GetDueAsync(20, DateTimeOffset.UtcNow, cancellationToken);
        if (due.Count == 0) return;

        var connection = await client.ProbeAsync(cancellationToken);
        if (connection.Status != OutlookIntegrationStatus.Connected)
        {
            var (status, code) = connection.Status switch
            {
                OutlookIntegrationStatus.OutlookNotRunning => (OutlookSyncStatus.OutlookNotRunning, OutlookErrorCodes.NotRunning),
                OutlookIntegrationStatus.UnsupportedPlatform => (OutlookSyncStatus.UnsupportedOutlook, OutlookErrorCodes.UnsupportedPlatform),
                OutlookIntegrationStatus.ClassicOutlookNotInstalled => (OutlookSyncStatus.UnsupportedOutlook, OutlookErrorCodes.ClassicNotInstalled),
                OutlookIntegrationStatus.PermissionDenied => (OutlookSyncStatus.PermanentFailure, OutlookErrorCodes.PermissionDenied),
                _ => (OutlookSyncStatus.RetryScheduled, OutlookErrorCodes.ComUnavailable)
            };
            foreach (var operation in due)
            {
                DateTimeOffset? next = status is OutlookSyncStatus.OutlookNotRunning or OutlookSyncStatus.RetryScheduled
                    ? DateTimeOffset.UtcNow.AddMinutes(2) : null;
                await store.MarkFailedAsync(operation.Id, status, code,
                    connection.SanitizedError ?? "Classic Outlook is unavailable.", next, cancellationToken);
            }
            return;
        }

        foreach (var operation in due)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ProcessOperationAsync(store, operation, cancellationToken);
        }
    }

    private async Task ProcessOperationAsync(IOutlookSyncStore store, OutlookCategorySyncOperation operation, CancellationToken cancellationToken)
    {
        await store.MarkInProgressAsync(operation.Id, cancellationToken);
        try
        {
            var binding = await store.GetBindingForMessageAsync(operation.VigiloMessageId, cancellationToken);
            if (binding is null)
            {
                DateTimeOffset? next = operation.AttemptCount < 7
                    ? DateTimeOffset.UtcNow.Add(Backoff(operation.AttemptCount + 1))
                    : null;
                await store.MarkFailedAsync(operation.Id, OutlookSyncStatus.MailboxNotFound, OutlookErrorCodes.StoreNotFound,
                    "No enabled Outlook store is configured for this mailbox.", next, cancellationToken);
                return;
            }

            if (_initializedStores.Add(binding.OutlookStoreId))
                await client.EnsureManagedCategoriesAsync(binding, cancellationToken);

            var identity = await store.GetIdentityAsync(operation.VigiloMessageId, cancellationToken);
            if (identity is null)
            {
                await store.MarkFailedAsync(operation.Id, OutlookSyncStatus.PermanentFailure, OutlookErrorCodes.MessageNotFound,
                    "The local message record no longer exists.", null, cancellationToken);
                return;
            }

            var match = await client.FindMessageAsync(binding, identity, cancellationToken);
            if (match.Status == OutlookMessageMatchStatus.Ambiguous)
            {
                await store.MarkFailedAsync(operation.Id, OutlookSyncStatus.AmbiguousMatch, OutlookErrorCodes.MessageAmbiguous,
                    "More than one Outlook message matched; no item was changed.", null, cancellationToken);
                return;
            }
            if (match.Item is null)
            {
                DateTimeOffset? next = operation.AttemptCount < 7 ? DateTimeOffset.UtcNow.Add(Backoff(operation.AttemptCount + 1)) : null;
                await store.MarkFailedAsync(operation.Id, OutlookSyncStatus.MessageNotFound, OutlookErrorCodes.MessageNotFound,
                    "The message was not found in the configured Outlook Inbox.", next, cancellationToken);
                return;
            }

            await client.ApplyCategoriesAsync(match.Item, operation.GetDesiredCategories(), cancellationToken);
            await store.SaveItemBindingAsync(operation.VigiloMessageId, identity.InternetMessageId, match.Item, cancellationToken);
            await store.MarkCompletedAsync(operation.Id, cancellationToken);
            logger.LogInformation("Outlook synchronization completed. OperationId={OperationId}", operation.Id);
        }
        catch (OutlookIntegrationException ex)
        {
            var retry = OutlookRetryPolicy.Classify(ex.Code, ex.IsTransient, operation.AttemptCount + 1);
            DateTimeOffset? next = retry.ShouldRetry ? DateTimeOffset.UtcNow.Add(Backoff(operation.AttemptCount + 1)) : null;
            await store.MarkFailedAsync(operation.Id, retry.Status, ex.Code, ex.Message, next, cancellationToken);
            logger.LogWarning("Outlook synchronization failed. OperationId={OperationId} ErrorCode={ErrorCode}", operation.Id, ex.Code);
        }
    }

    private static TimeSpan Backoff(int attempts) => TimeSpan.FromMinutes(Math.Min(360, Math.Pow(2, Math.Min(attempts, 8))));
}
