using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using Vigilo.Configuration;
using Vigilo.Core;
using Vigilo.Storage;

namespace Vigilo.Tests;

public sealed class AccountSettingsServiceTests
{
    [Fact]
    public async Task Repeated_reads_create_a_fresh_database_context_per_operation()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var dbOptions = new DbContextOptionsBuilder<VigiloDbContext>().UseSqlite(connection).Options;
        await using (var bootstrapDb = new VigiloDbContext(dbOptions))
        {
            await bootstrapDb.Database.EnsureCreatedAsync();
        }

        var factory = new TestDbContextFactory(dbOptions);
        var service = new AccountSettingsService(
            factory,
            new StorageOptions
            {
                ProtectedSettingsPath = Path.Combine(Path.GetTempPath(), "Vigilo.Tests", Guid.NewGuid().ToString("N"), "protected-settings.json")
            },
            new AtomicConfigurationRepository());

        await service.GetAccountsAsync(CancellationToken.None);
        await service.GetAccountsAsync(CancellationToken.None);

        Assert.Equal(2, factory.CreatedCount);
    }

    [Fact]
    public async Task SaveAccountAsync_inserts_new_account_into_current_schema()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var dbOptions = new DbContextOptionsBuilder<VigiloDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new VigiloDbContext(dbOptions);
        await db.Database.EnsureCreatedAsync();
        var service = CreateService(dbOptions);
        var account = new EmailAccount
        {
            Name = "New account",
            EmailAddress = "new@example.com",
            Username = "new@example.com"
        };

        await service.SaveAccountAsync(account, CancellationToken.None);

        var savedAccount = await db.Accounts.AsNoTracking().SingleAsync();
        Assert.Equal(account.Id, savedAccount.Id);
        Assert.Equal("New account", savedAccount.Name);
        Assert.Equal("new@example.com", savedAccount.EmailAddress);
        Assert.True(savedAccount.StoreMessageBodies);
    }

    [Fact]
    public async Task SaveAccountAsync_inserts_second_account_into_legacy_schema()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var dbOptions = new DbContextOptionsBuilder<VigiloDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new VigiloDbContext(dbOptions);
        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE "Accounts" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_Accounts" PRIMARY KEY,
                "Name" TEXT NOT NULL,
                "EmailAddress" TEXT NOT NULL,
                "ImapHost" TEXT NOT NULL,
                "ImapPort" INTEGER NOT NULL,
                "UseSsl" INTEGER NOT NULL,
                "Username" TEXT NOT NULL,
                "FoldersToMonitor" TEXT NOT NULL,
                "SentFoldersToMonitor" TEXT NOT NULL,
                "PollingFallbackMinutes" INTEGER NOT NULL,
                "UseImapIdle" INTEGER NOT NULL,
                "NotificationsEnabled" INTEGER NOT NULL,
                "DigestTime" TEXT NOT NULL,
                "DataRetentionDays" INTEGER NOT NULL,
                "StoreMessageBodies" INTEGER NOT NULL,
                "CreatedAt" TEXT NOT NULL,
                "UpdatedAt" TEXT NOT NULL
            );
            """);

        var firstAccount = new EmailAccount
        {
            Name = "Existing account",
            EmailAddress = "first@example.com",
            Username = "first@example.com"
        };
        db.Accounts.Add(firstAccount);
        await db.SaveChangesAsync();

        var service = CreateService(dbOptions);
        await service.SaveAccountAsync(
            new EmailAccount
            {
                EmailAddress = "second@example.com",
                Username = "second@example.com"
            },
            CancellationToken.None);

        Assert.Equal(2, await db.Accounts.CountAsync());
        Assert.All(await db.Accounts.ToListAsync(), account => Assert.True(account.StoreMessageBodies));
        var preservedAccount = await db.Accounts.SingleAsync(account => account.Id == firstAccount.Id);
        Assert.Equal("Existing account", preservedAccount.Name);
        Assert.Equal("first@example.com", preservedAccount.EmailAddress);
    }

    [Fact]
    public async Task Protected_settings_migrate_legacy_shape_and_preserve_concurrent_password_updates()
    {
        var root = Path.Combine(Path.GetTempPath(), "Vigilo.Tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "protected-settings.json");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(path, """{"Passwords":{}}""");
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var dbOptions = new DbContextOptionsBuilder<VigiloDbContext>().UseSqlite(connection).Options;
        await using var db = new VigiloDbContext(dbOptions);
        var service = new AccountSettingsService(
            new TestDbContextFactory(dbOptions),
            new StorageOptions { ProtectedSettingsPath = path },
            new AtomicConfigurationRepository());
        var passwords = Enumerable.Range(0, 16)
            .Select(index => (AccountId: Guid.NewGuid(), Password: $"secret-{index}"))
            .ToArray();

        await Task.WhenAll(passwords.Select(entry =>
            service.SavePasswordAsync(entry.AccountId, entry.Password, CancellationToken.None)));

        foreach (var entry in passwords)
        {
            Assert.Equal(entry.Password, await service.GetPasswordAsync(entry.AccountId, CancellationToken.None));
        }

        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        Assert.Equal(1, json.RootElement.GetProperty("SchemaVersion").GetInt32());
        Assert.Equal(passwords.Length, json.RootElement.GetProperty("Passwords").EnumerateObject().Count());
        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public async Task Protected_settings_reject_base64_that_is_not_valid_current_user_ciphertext()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), "Vigilo.Tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "protected-settings.json");
        Directory.CreateDirectory(root);
        var accountId = Guid.NewGuid();
        await File.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(new
            {
                SchemaVersion = 1,
                Passwords = new Dictionary<Guid, string> { [accountId] = "AQID" }
            }));
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var dbOptions = new DbContextOptionsBuilder<VigiloDbContext>().UseSqlite(connection).Options;
        await using var db = new VigiloDbContext(dbOptions);
        var service = new AccountSettingsService(
            new TestDbContextFactory(dbOptions),
            new StorageOptions { ProtectedSettingsPath = path },
            new AtomicConfigurationRepository());

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.HasPasswordAsync(accountId, CancellationToken.None));

        Directory.Delete(root, recursive: true);
    }

    private static AccountSettingsService CreateService(DbContextOptions<VigiloDbContext> dbOptions) => new(
        new TestDbContextFactory(dbOptions),
        new StorageOptions
        {
            ProtectedSettingsPath = Path.Combine(
                Path.GetTempPath(),
                "Vigilo.Tests",
                Guid.NewGuid().ToString("N"),
                "protected-settings.json")
        },
        new AtomicConfigurationRepository());
}
