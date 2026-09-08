using System.Data;
using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Vigilo.Core;

namespace Vigilo.Storage;

public static class VigiloDatabaseSchema
{
    public const string CurrentMigration = "20260908104704_Baseline";
}

public sealed class AppBootstrapper(VigiloDbContext dbContext, ILogger<AppBootstrapper> logger) : IAppBootstrapper
{
    private static readonly string[] RequiredTables =
    [
        "Accounts",
        "EmailMessages",
        "LiveReports",
        "NotificationRecords",
        "OutlookCategorySyncOperations",
        "OutlookItemBindings",
        "OutlookStoreBindings",
        "ProcessedEmails",
        "SenderRules",
        "SettingsConfigurationCommits",
        "TrackedItems"
    ];

    private static readonly string[] RequiredIndexes =
    [
        "IX_EmailMessages_AccountId_Folder_ReceivedAt",
        "IX_EmailMessages_AccountId_ProviderMessageId",
        "IX_EmailMessages_MessageIdHeader",
        "IX_EmailMessages_ThreadKey",
        "IX_NotificationRecords_TrackedItemId_Kind_DedupeKey",
        "IX_OutlookCategorySyncOperations_NextAttemptAtUtc",
        "IX_OutlookCategorySyncOperations_Status_NextAttemptAtUtc",
        "IX_OutlookCategorySyncOperations_VigiloMessageId_Status",
        "IX_OutlookStoreBindings_VigiloMailboxId",
        "IX_ProcessedEmails_AccountId_Folder_LastSeenAt",
        "IX_ProcessedEmails_AccountId_Folder_UidValidity_ImapUid",
        "IX_ProcessedEmails_EmailMessageId",
        "IX_ProcessedEmails_MessageIdHeader",
        "IX_ProcessedEmails_ProcessingStatus_NextClassificationAttemptAt",
        "IX_SenderRules_AccountId_NormalizedSenderEmail",
        "IX_TrackedItems_EmailMessageId",
        "IX_TrackedItems_MessageType_Status",
        "IX_TrackedItems_Status_Deadline",
        "IX_TrackedItems_ThreadKey_Status"
    ];

    private static readonly (
        string DependentTable,
        string DependentColumn,
        string PrincipalTable,
        string PrincipalColumn,
        string OnDelete)[] RequiredForeignKeys =
    [
        ("EmailMessages", "AccountId", "Accounts", "Id", "CASCADE"),
        ("NotificationRecords", "TrackedItemId", "TrackedItems", "Id", "CASCADE"),
        ("OutlookCategorySyncOperations", "VigiloMessageId", "EmailMessages", "Id", "CASCADE"),
        ("OutlookItemBindings", "VigiloMessageId", "EmailMessages", "Id", "CASCADE"),
        ("OutlookStoreBindings", "VigiloMailboxId", "Accounts", "Id", "CASCADE"),
        ("ProcessedEmails", "AccountId", "Accounts", "Id", "CASCADE"),
        ("ProcessedEmails", "EmailMessageId", "EmailMessages", "Id", "CASCADE"),
        ("SenderRules", "AccountId", "Accounts", "Id", "CASCADE"),
        ("TrackedItems", "EmailMessageId", "EmailMessages", "Id", "CASCADE")
    ];

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "Storage migration started. ExpectedMigration={ExpectedMigration}",
            VigiloDatabaseSchema.CurrentMigration);

        var knownMigrations = dbContext.Database.GetMigrations().ToArray();
        ValidateCompiledMigrations(knownMigrations);

        var appliedMigrations = (await dbContext.Database.GetAppliedMigrationsAsync(cancellationToken)).ToArray();
        ValidateAppliedMigrations(knownMigrations, appliedMigrations);

        if (appliedMigrations.Length == 0)
        {
            var existingTables = await GetTableNamesAsync(cancellationToken);
            if (existingTables.Count > 0)
            {
                throw new InvalidOperationException(
                    "The database contains tables but has no Vigilo migration history. Restore it from a backup or remove it; no schema changes were applied.");
            }

            await dbContext.Database.MigrateAsync(cancellationToken);
        }

        await ValidateCurrentSchemaAsync(knownMigrations, cancellationToken);
        logger.LogInformation(
            "Storage migration completed. CurrentMigration={CurrentMigration}",
            VigiloDatabaseSchema.CurrentMigration);
    }

    private static void ValidateCompiledMigrations(IReadOnlyList<string> knownMigrations)
    {
        if (knownMigrations.Count != 1
            || !string.Equals(knownMigrations[0], VigiloDatabaseSchema.CurrentMigration, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The compiled migration set does not match the declared Vigilo schema version {VigiloDatabaseSchema.CurrentMigration}.");
        }
    }

    private static void ValidateAppliedMigrations(
        IReadOnlyList<string> knownMigrations,
        IReadOnlyList<string> appliedMigrations)
    {
        var unknown = appliedMigrations
            .Except(knownMigrations, StringComparer.Ordinal)
            .ToArray();
        if (unknown.Length > 0)
        {
            throw new InvalidOperationException(
                $"The database was created by a newer or unknown Vigilo schema ({string.Join(", ", unknown)}). "
                + "Install a compatible application version; no schema changes were applied.");
        }

        var expectedPrefix = knownMigrations.Take(appliedMigrations.Count);
        if (!expectedPrefix.SequenceEqual(appliedMigrations, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                "The database migration history is incomplete or out of order. No schema changes were applied.");
        }
    }

    private async Task ValidateCurrentSchemaAsync(
        IReadOnlyList<string> knownMigrations,
        CancellationToken cancellationToken)
    {
        var appliedMigrations = (await dbContext.Database.GetAppliedMigrationsAsync(cancellationToken)).ToArray();
        ValidateAppliedMigrations(knownMigrations, appliedMigrations);
        if (!appliedMigrations.SequenceEqual(knownMigrations, StringComparer.Ordinal)
            || !string.Equals(appliedMigrations[^1], VigiloDatabaseSchema.CurrentMigration, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Database migration did not reach expected schema {VigiloDatabaseSchema.CurrentMigration}.");
        }

        await ValidateRequiredTablesAsync(cancellationToken);

        var indexes = await GetIndexNamesAsync(cancellationToken);
        var missingIndexes = RequiredIndexes.Where(index => !indexes.Contains(index)).ToArray();
        if (missingIndexes.Length > 0)
        {
            throw new InvalidOperationException(
                $"Database schema {VigiloDatabaseSchema.CurrentMigration} is missing indexes: {string.Join(", ", missingIndexes)}.");
        }

        var foreignKeys = await GetForeignKeysAsync(cancellationToken);
        var missingForeignKeys = RequiredForeignKeys
            .Where(required => !foreignKeys.Contains(required))
            .Select(required =>
                $"{required.DependentTable}.{required.DependentColumn}->{required.PrincipalTable}.{required.PrincipalColumn} ON DELETE {required.OnDelete}")
            .ToArray();
        if (missingForeignKeys.Length > 0)
        {
            throw new InvalidOperationException(
                $"Database schema {VigiloDatabaseSchema.CurrentMigration} is missing foreign keys: {string.Join(", ", missingForeignKeys)}.");
        }

        await ValidateForeignKeyIntegrityAsync(cancellationToken);
    }

    private async Task ValidateRequiredTablesAsync(CancellationToken cancellationToken)
    {
        var tables = await GetTableNamesAsync(cancellationToken);
        var missingTables = RequiredTables.Where(table => !tables.Contains(table)).ToArray();
        if (missingTables.Length > 0)
        {
            throw new InvalidOperationException(
                $"The Vigilo database is missing required tables: {string.Join(", ", missingTables)}.");
        }
    }

    private async Task ValidateForeignKeyIntegrityAsync(CancellationToken cancellationToken)
    {
        await WithOpenConnectionAsync(
            async connection =>
            {
                await using var command = CreateCommand(connection, "PRAGMA foreign_key_check;");
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken))
                {
                    throw new InvalidOperationException(
                        $"Foreign-key integrity check failed for table {reader.GetString(0)} at row {reader.GetValue(1)}.");
                }
            },
            cancellationToken);
    }

    private async Task<HashSet<string>> GetTableNamesAsync(CancellationToken cancellationToken) =>
        await ReadNameSetAsync(
            "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' AND name <> '__EFMigrationsHistory';",
            cancellationToken);

    private async Task<HashSet<string>> GetIndexNamesAsync(CancellationToken cancellationToken) =>
        await ReadNameSetAsync(
            "SELECT name FROM sqlite_master WHERE type = 'index' AND name NOT LIKE 'sqlite_%';",
            cancellationToken);

    private async Task<HashSet<string>> ReadNameSetAsync(string sql, CancellationToken cancellationToken)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await WithOpenConnectionAsync(
            async connection =>
            {
                await using var command = CreateCommand(connection, sql);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    names.Add(reader.GetString(0));
                }
            },
            cancellationToken);
        return names;
    }

    private async Task<HashSet<(
        string DependentTable,
        string DependentColumn,
        string PrincipalTable,
        string PrincipalColumn,
        string OnDelete)>> GetForeignKeysAsync(
        CancellationToken cancellationToken)
    {
        var foreignKeys = new HashSet<(
            string DependentTable,
            string DependentColumn,
            string PrincipalTable,
            string PrincipalColumn,
            string OnDelete)>();
        await WithOpenConnectionAsync(
            async connection =>
            {
                foreach (var table in RequiredTables.Where(table => table != "SettingsConfigurationCommits"))
                {
                    await using var command = CreateCommand(connection, $"PRAGMA foreign_key_list(\"{table}\");");
                    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                    while (await reader.ReadAsync(cancellationToken))
                    {
                        foreignKeys.Add((
                            table,
                            reader.GetString(3),
                            reader.GetString(2),
                            reader.GetString(4),
                            reader.GetString(6)));
                    }
                }
            },
            cancellationToken);
        return foreignKeys;
    }

    private async Task<long> ExecuteScalarLongAsync(string sql, CancellationToken cancellationToken)
    {
        long result = 0;
        await WithOpenConnectionAsync(
            async connection =>
            {
                await using var command = CreateCommand(connection, sql);
                result = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
            },
            cancellationToken);
        return result;
    }

    private async Task WithOpenConnectionAsync(
        Func<DbConnection, Task> action,
        CancellationToken cancellationToken)
    {
        var connection = dbContext.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;
        if (shouldClose)
        {
            await dbContext.Database.OpenConnectionAsync(cancellationToken);
        }

        try
        {
            await action(connection);
        }
        finally
        {
            if (shouldClose)
            {
                await dbContext.Database.CloseConnectionAsync();
            }
        }
    }

    private DbCommand CreateCommand(DbConnection connection, string commandText)
    {
        var command = connection.CreateCommand();
        command.CommandText = commandText;
        command.Transaction = dbContext.Database.CurrentTransaction?.GetDbTransaction();
        return command;
    }
}
