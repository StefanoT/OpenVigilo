using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Vigilo.Configuration;
using Vigilo.Core;

namespace Vigilo.Storage;

public sealed class SettingsTransactionRecoveryService(
    IDbContextFactory<VigiloDbContext> dbContextFactory,
    StorageOptions options,
    IAtomicConfigurationRepository configurationRepository,
    IModelSelectionService modelSelectionService,
    ILogger<SettingsTransactionRecoveryService> logger)
{
    public async Task RecoverPendingAsync(CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        if (!File.Exists(Path.GetFullPath(options.SettingsTransactionJournalPath)))
        {
            await DeleteCommitMarkersAsync(dbContext, cancellationToken);
            return;
        }

        var definition = SettingsTransactionJournal.Definition(options);
        var journal = await configurationRepository.ReadAsync(definition, cancellationToken);
        var committed = await IsDatabaseCommitVisibleAsync(dbContext, journal, cancellationToken);
        if (committed)
        {
            logger.LogInformation(
                "Completed settings transaction journal found for account {AccountId}; keeping committed files.",
                journal.AccountId);
        }
        else
        {
            await RestoreSnapshotsAsync(journal, cancellationToken);
            dbContext.ChangeTracker.Clear();
            logger.LogWarning(
                "Recovered an interrupted settings transaction for account {AccountId}.",
                journal.AccountId);
        }

        await configurationRepository.DeleteAsync(options.SettingsTransactionJournalPath, cancellationToken);
        await DeleteCommitMarkersAsync(dbContext, cancellationToken);
    }

    private async Task<bool> IsDatabaseCommitVisibleAsync(
        VigiloDbContext dbContext,
        SettingsTransactionJournal journal,
        CancellationToken cancellationToken)
    {
        return await dbContext.Database
            .SqlQuery<int>($"SELECT COUNT(*) AS Value FROM SettingsConfigurationCommits WHERE TransactionId = {journal.TransactionId.ToString()}")
            .SingleAsync(cancellationToken) != 0;
    }

    private static Task DeleteCommitMarkersAsync(
        VigiloDbContext dbContext,
        CancellationToken cancellationToken) =>
        dbContext.Database.ExecuteSqlRawAsync(
            "DELETE FROM SettingsConfigurationCommits",
            cancellationToken);

    private async Task RestoreSnapshotsAsync(
        SettingsTransactionJournal journal,
        CancellationToken cancellationToken)
    {
        var failures = new List<Exception>();
        await TryRecoverAsync(
            () => modelSelectionService.SelectProfileAsync(journal.PreviousModelPreset, cancellationToken),
            failures);
        await TryRecoverAsync(
            () => configurationRepository.RestoreAsync(journal.PreviousModelSettings, cancellationToken),
            failures);
        await TryRecoverAsync(
            () => configurationRepository.RestoreAsync(journal.PreviousProtectedSettings, cancellationToken),
            failures);

        if (failures.Count != 0)
        {
            throw new AggregateException(
                "One or more configuration snapshots could not be restored. The recovery journal was retained.",
                failures);
        }
    }

    private static async Task TryRecoverAsync(Func<Task> recovery, ICollection<Exception> failures)
    {
        try
        {
            await recovery();
        }
        catch (Exception ex)
        {
            failures.Add(ex);
        }
    }
}
