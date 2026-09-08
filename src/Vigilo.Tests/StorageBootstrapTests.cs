using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Vigilo.Core;
using Vigilo.Storage;

namespace Vigilo.Tests;

public sealed class StorageBootstrapTests
{
    [Fact]
    public async Task Bootstrap_creates_current_schema_for_empty_database()
    {
        await using var connection = await OpenConnectionAsync();
        await using var db = CreateDbContext(connection);

        await BootstrapAsync(db);

        Assert.Equal(
            db.Database.GetMigrations(),
            await db.Database.GetAppliedMigrationsAsync());
        Assert.True(await TableExistsAsync(connection, "SettingsConfigurationCommits"));
        Assert.True(await IndexExistsAsync(connection, "IX_EmailMessages_AccountId_Folder_ReceivedAt"));
        Assert.True(await ForeignKeyExistsAsync(connection, "EmailMessages", "AccountId", "Accounts"));
        Assert.True(await ForeignKeyExistsAsync(connection, "TrackedItems", "EmailMessageId", "EmailMessages"));
    }

    [Fact]
    public async Task Bootstrap_rejects_tables_without_migration_history()
    {
        await using var connection = await OpenConnectionAsync();
        await using var db = CreateDbContext(connection);
        await BootstrapAsync(db);
        var graph = CreateGraph();
        AddGraph(db, graph);
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync("DELETE FROM \"__EFMigrationsHistory\";");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => BootstrapAsync(db));

        Assert.Contains("no Vigilo migration history", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await ReadMigrationHistoryAsync(connection));
        Assert.Equal(1, await db.Accounts.CountAsync());
    }

    [Fact]
    public async Task Bootstrap_rejects_unknown_newer_schema_without_changing_history()
    {
        const string futureMigration = "99991231235959_FutureRelease";
        const string futureProductVersion = "999.0.0";
        await using var connection = await OpenConnectionAsync();
        await using var db = CreateDbContext(connection);
        await BootstrapAsync(db);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO \"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") VALUES ({futureMigration}, {futureProductVersion})");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => BootstrapAsync(db));

        Assert.Contains("newer or unknown", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            db.Database.GetMigrations().Concat([futureMigration]),
            await ReadMigrationHistoryAsync(connection));
    }

    [Fact]
    public async Task Cascade_relationships_delete_complete_account_graph()
    {
        await using var connection = await OpenConnectionAsync();
        await using var db = CreateDbContext(connection);
        await BootstrapAsync(db);
        var graph = CreateGraph();
        AddGraph(db, graph);
        await db.SaveChangesAsync();

        db.Accounts.Remove(graph.Account);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        Assert.Empty(await db.Accounts.ToListAsync());
        Assert.Empty(await db.EmailMessages.ToListAsync());
        Assert.Empty(await db.ProcessedEmails.ToListAsync());
        Assert.Empty(await db.TrackedItems.ToListAsync());
        Assert.Empty(await db.NotificationRecords.ToListAsync());
        Assert.Empty(await db.SenderRules.ToListAsync());
        Assert.Empty(await db.OutlookStoreBindings.ToListAsync());
        Assert.Empty(await db.OutlookItemBindings.ToListAsync());
        Assert.Empty(await db.OutlookCategorySyncOperations.ToListAsync());
    }

    private static async Task BootstrapAsync(VigiloDbContext db) =>
        await new AppBootstrapper(db, NullLogger<AppBootstrapper>.Instance)
            .InitializeAsync(CancellationToken.None);

    private static async Task<SqliteConnection> OpenConnectionAsync()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        return connection;
    }

    private static VigiloDbContext CreateDbContext(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<VigiloDbContext>().UseSqlite(connection).Options);

    private static void AddGraph(VigiloDbContext db, DatabaseGraph graph)
    {
        db.AddRange(
            graph.Account,
            graph.EmailMessage,
            graph.ProcessedEmail,
            graph.TrackedItem,
            graph.NotificationRecord,
            graph.SenderRule,
            graph.OutlookStoreBinding,
            graph.OutlookItemBinding,
            graph.OutlookCategorySyncOperation);
    }

    private static DatabaseGraph CreateGraph()
    {
        var now = DateTimeOffset.UtcNow;
        var account = new EmailAccount
        {
            Name = "Primary mailbox",
            EmailAddress = "owner@example.com",
            Username = "owner@example.com"
        };
        var message = new EmailMessage
        {
            AccountId = account.Id,
            Folder = "Inbox",
            ProviderMessageId = "provider-42",
            MessageIdHeader = "<release-upgrade@example.com>",
            SenderName = "Sender",
            SenderEmail = "sender@example.com",
            Subject = "Release upgrade",
            ReceivedAt = now,
            Snippet = "Migration test",
            NormalizedBody = "Upgrade the released database.",
            ThreadKey = "release-upgrade",
            LastScannedAt = now
        };
        var processed = new ProcessedEmail
        {
            AccountId = account.Id,
            Folder = "Inbox",
            UidValidity = 1,
            ImapUid = 42,
            MessageIdHeader = message.MessageIdHeader,
            SubjectHash = "subject",
            BodyHash = "body",
            FlagsHash = "flags",
            ReceivedAt = now,
            FirstSeenAt = now,
            LastSeenAt = now,
            LastProcessedAt = now,
            ProcessingStatus = ProcessingStatus.Classified,
            ClassificationVersion = "test",
            ClassifierModelVersion = "test-model",
            EmailMessageId = message.Id
        };
        var trackedItem = new TrackedItem
        {
            EmailMessageId = message.Id,
            Status = TrackedItemStatus.Open,
            Deadline = now.AddDays(1),
            ThreadKey = message.ThreadKey,
            ActionSummary = "Verify migration",
            Reason = "Released schema upgrade",
            CreatedAt = now,
            UpdatedAt = now
        };

        return new DatabaseGraph(
            account,
            message,
            processed,
            trackedItem,
            new NotificationRecord
            {
                TrackedItemId = trackedItem.Id,
                Kind = NotificationKind.NewActionableItem,
                DedupeKey = "migration-test",
                SentAt = now
            },
            new SenderRule
            {
                AccountId = account.Id,
                SenderEmail = message.SenderEmail,
                NormalizedSenderEmail = message.SenderEmail,
                Kind = SenderRuleKind.Vip
            },
            new OutlookStoreBinding
            {
                VigiloMailboxId = account.Id,
                OutlookStoreId = "store-1",
                OutlookStoreDisplayName = "Primary mailbox",
                IsEnabled = true
            },
            new OutlookItemBinding
            {
                VigiloMessageId = message.Id,
                InternetMessageId = message.MessageIdHeader,
                OutlookEntryId = "entry-1",
                OutlookStoreId = "store-1",
                LastMatchedAtUtc = now,
                MatchMethod = OutlookMatchMethod.InternetMessageId
            },
            new OutlookCategorySyncOperation
            {
                VigiloMessageId = message.Id,
                DesiredCategoriesJson = "[\"Vigilo - Action Needed\"]",
                Status = OutlookSyncStatus.Pending
            });
    }

    private static async Task<IReadOnlyList<string>> ReadMigrationHistoryAsync(SqliteConnection connection)
    {
        var migrations = new List<string>();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT MigrationId FROM \"__EFMigrationsHistory\" ORDER BY MigrationId;";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            migrations.Add(reader.GetString(0));
        }

        return migrations;
    }

    private static async Task<bool> TableExistsAsync(SqliteConnection connection, string table)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name;";
        command.Parameters.AddWithValue("$name", table);
        return Convert.ToInt64(await command.ExecuteScalarAsync()) == 1;
    }

    private static async Task<bool> IndexExistsAsync(SqliteConnection connection, string index)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = $name;";
        command.Parameters.AddWithValue("$name", index);
        return Convert.ToInt64(await command.ExecuteScalarAsync()) == 1;
    }

    private static async Task<bool> ForeignKeyExistsAsync(
        SqliteConnection connection,
        string table,
        string column,
        string principalTable)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA foreign_key_list('{table}');";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            if (string.Equals(reader.GetString(2), principalTable, StringComparison.OrdinalIgnoreCase)
                && string.Equals(reader.GetString(3), column, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private sealed record DatabaseGraph(
        EmailAccount Account,
        EmailMessage EmailMessage,
        ProcessedEmail ProcessedEmail,
        TrackedItem TrackedItem,
        NotificationRecord NotificationRecord,
        SenderRule SenderRule,
        OutlookStoreBinding OutlookStoreBinding,
        OutlookItemBinding OutlookItemBinding,
        OutlookCategorySyncOperation OutlookCategorySyncOperation);
}
