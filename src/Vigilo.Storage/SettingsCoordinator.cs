using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Runtime.ExceptionServices;
using Vigilo.Configuration;
using Vigilo.Core;

namespace Vigilo.Storage;

public sealed class SettingsCoordinator(
    IDbContextFactory<VigiloDbContext> dbContextFactory,
    StorageOptions options,
    IAtomicConfigurationRepository configurationRepository,
    SettingsTransactionRecoveryService recoveryService,
    IAccountSettingsService accountSettings,
    IModelSelectionService modelSelectionService,
    ILogger<SettingsCoordinator> logger) : ISettingsCoordinator
{
    private static readonly SemaphoreSlim SaveGate = new(1, 1);

    public async Task SaveAsync(SettingsSaveRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        Validate(request);

        await SaveGate.WaitAsync(cancellationToken);
        try
        {
            await recoveryService.RecoverPendingAsync(cancellationToken);
            await SaveCoreAsync(request, cancellationToken);
        }
        finally
        {
            SaveGate.Release();
        }
    }

    private async Task SaveCoreAsync(SettingsSaveRequest request, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var previousPreset = modelSelectionService.GetCurrentProfile().Preset;
        var modelChanged = !string.Equals(previousPreset, request.ModelPreset, StringComparison.OrdinalIgnoreCase);
        var passwordChanged = request.NewPassword is not null;
        var journal = new SettingsTransactionJournal(
            SettingsTransactionJournal.CurrentSchemaVersion,
            Guid.NewGuid(),
            request.Account.Id,
            previousPreset,
            await configurationRepository.CaptureAsync(options.ProtectedSettingsPath, cancellationToken),
            await configurationRepository.CaptureAsync(options.ModelSettingsPath, cancellationToken));
        var journalDefinition = SettingsTransactionJournal.Definition(options);
        await configurationRepository.WriteAsync(journalDefinition, journal, cancellationToken);

        Exception? commitFailure = null;
        var commitCompleted = false;
        var recoveryFailures = new List<Exception>();
        try
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
            try
            {
                await AccountSettingsService.SaveAccountAsync(dbContext, request.Account, cancellationToken);
                if (request.OutlookBinding is not null)
                {
                    await OutlookSyncStore.SaveBindingAsync(dbContext, request.OutlookBinding, cancellationToken);
                    await dbContext.SaveChangesAsync(cancellationToken);
                }

                await dbContext.Database.ExecuteSqlInterpolatedAsync(
                    $"INSERT INTO SettingsConfigurationCommits (TransactionId, CreatedAtUtc) VALUES ({journal.TransactionId.ToString()}, {DateTimeOffset.UtcNow})",
                    cancellationToken);

                if (modelChanged)
                {
                    await modelSelectionService.SelectProfileAsync(request.ModelPreset, cancellationToken);
                }

                if (passwordChanged)
                {
                    await accountSettings.SavePasswordAsync(request.Account.Id, request.NewPassword!, cancellationToken);
                }

                await transaction.CommitAsync(cancellationToken);
                commitCompleted = true;
            }
            catch (Exception ex)
            {
                commitFailure = ex;
                try
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                }
                catch (Exception rollbackFailure)
                {
                    recoveryFailures.Add(rollbackFailure);
                }
            }
        }
        catch (Exception transactionFailure)
        {
            if (commitCompleted)
            {
                logger.LogWarning(
                    transactionFailure,
                    "Settings were committed, but disposing the database transaction failed.");
            }
            else
            {
                commitFailure ??= transactionFailure;
                if (!ReferenceEquals(commitFailure, transactionFailure))
                {
                    recoveryFailures.Add(transactionFailure);
                }
            }
        }

        if (commitFailure is not null)
        {
            dbContext.ChangeTracker.Clear();
            await TryRecoverAsync(
                () => recoveryService.RecoverPendingAsync(CancellationToken.None),
                recoveryFailures);
            logger.LogError(
                commitFailure,
                "Application settings were not committed and durable recovery was attempted. AccountId={AccountId} RecoveryFailureCount={RecoveryFailureCount}",
                request.Account.Id,
                recoveryFailures.Count);

            if (recoveryFailures.Count == 0)
            {
                ExceptionDispatchInfo.Capture(commitFailure).Throw();
            }

            throw new AggregateException(
                "Application settings failed and one or more recovery operations also failed. The recovery journal was retained.",
                [commitFailure, .. recoveryFailures]);
        }

        try
        {
            await recoveryService.RecoverPendingAsync(cancellationToken);
        }
        catch (Exception cleanupFailure)
        {
            logger.LogWarning(
                cleanupFailure,
                "Settings committed, but the completed transaction journal could not be removed. Startup recovery will retry cleanup.");
        }

        logger.LogInformation(
            "Application settings committed. AccountId={AccountId} OutlookBindingChanged={OutlookBindingChanged} ModelChanged={ModelChanged} PasswordChanged={PasswordChanged}",
            request.Account.Id,
            request.OutlookBinding is not null,
            modelChanged,
            passwordChanged);
    }

    private void Validate(SettingsSaveRequest request)
    {
        SettingsValidator.ValidateAccount(request.Account);
        if (request.OutlookBinding is not null)
        {
            SettingsValidator.ValidateOutlookBinding(request.OutlookBinding);
        }

        if (!modelSelectionService.GetProfiles().Any(profile =>
                string.Equals(profile.Preset, request.ModelPreset, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"Unknown local AI model preset '{request.ModelPreset}'.");
        }

        if (request.NewPassword is not null && string.IsNullOrWhiteSpace(request.NewPassword))
        {
            throw new InvalidOperationException("An app password cannot be empty.");
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
