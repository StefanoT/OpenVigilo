using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Vigilo.Configuration;
using Vigilo.Core;
using Vigilo.LocalAi;
using Vigilo.Storage;

namespace Vigilo.Tests;

public sealed class SettingsCoordinatorTests
{
    [Fact]
    public async Task Save_commits_database_password_and_model_documents_as_one_coordinated_operation()
    {
        using var directory = new TemporaryDirectory();
        await using var fixture = await DatabaseFixture.CreateAsync();
        var repository = new AtomicConfigurationRepository();
        var storageOptions = Options(directory.Path);
        var accountSettings = new AccountSettingsService(
            fixture.DbContextFactory,
            storageOptions,
            repository);
        var modelOptions = new ModelOptions { ModelBaseDirectory = Path.Combine(directory.Path, "Models") };
        using var modelSelection = new ModelSelectionService(
            modelOptions,
            new ModelSettingsStore(Path.Combine(directory.Path, "model-settings.json")),
            NullLogger<ModelSelectionService>.Instance,
            repository);
        var coordinator = new SettingsCoordinator(
            fixture.DbContextFactory,
            storageOptions,
            repository,
            Recovery(fixture.DbContextFactory, storageOptions, repository, modelSelection),
            accountSettings,
            modelSelection,
            NullLogger<SettingsCoordinator>.Instance);
        var account = ValidAccount();

        await coordinator.SaveAsync(
            new SettingsSaveRequest(
                account,
                OutlookBinding: null,
                ModelProfileCatalog.Gemma4E2BLiteRtPreset,
                NewPassword: "app-secret"),
            CancellationToken.None);

        Assert.Equal(account.Id, (await fixture.Db.Accounts.AsNoTracking().SingleAsync()).Id);
        Assert.Equal("app-secret", await accountSettings.GetPasswordAsync(account.Id, CancellationToken.None));
        Assert.Equal(ModelProfileCatalog.Gemma4E2BLiteRtPreset, modelSelection.GetCurrentProfile().Preset);
        Assert.Contains("\"SchemaVersion\": 1", await File.ReadAllTextAsync(Path.Combine(directory.Path, "protected-settings.json")));
        Assert.Contains("\"SchemaVersion\": 1", await File.ReadAllTextAsync(Path.Combine(directory.Path, "model-settings.json")));
        Assert.False(File.Exists(storageOptions.SettingsTransactionJournalPath));
        Assert.Equal(
            0,
            await fixture.Db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM SettingsConfigurationCommits").SingleAsync());
    }

    [Fact]
    public async Task File_failure_rolls_back_database_and_restores_the_previous_model_selection()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        using var directory = new TemporaryDirectory();
        var repository = new AtomicConfigurationRepository();
        var storageOptions = Options(directory.Path);
        var accountSettings = new FailingPasswordAccountSettings(fixture.Db);
        var modelSelection = new RecordingModelSelectionService();
        var coordinator = new SettingsCoordinator(
            fixture.DbContextFactory,
            storageOptions,
            repository,
            Recovery(fixture.DbContextFactory, storageOptions, repository, modelSelection),
            accountSettings,
            modelSelection,
            NullLogger<SettingsCoordinator>.Instance);

        await Assert.ThrowsAsync<IOException>(() => coordinator.SaveAsync(
            new SettingsSaveRequest(
                ValidAccount(),
                OutlookBinding: null,
                ModelProfileCatalog.Gemma4E2BLiteRtPreset,
                NewPassword: "will-fail"),
            CancellationToken.None));

        Assert.Empty(await fixture.Db.Accounts.AsNoTracking().ToListAsync());
        Assert.Equal(ModelProfileCatalog.Phi4MiniPreset, modelSelection.GetCurrentProfile().Preset);
        Assert.Equal(
            [ModelProfileCatalog.Gemma4E2BLiteRtPreset, ModelProfileCatalog.Phi4MiniPreset],
            modelSelection.Selections);
    }

    [Fact]
    public async Task Validation_runs_before_any_database_or_file_change()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        using var directory = new TemporaryDirectory();
        var repository = new AtomicConfigurationRepository();
        var storageOptions = Options(directory.Path);
        var accountSettings = new FailingPasswordAccountSettings(fixture.Db);
        var modelSelection = new RecordingModelSelectionService();
        var coordinator = new SettingsCoordinator(
            fixture.DbContextFactory,
            storageOptions,
            repository,
            Recovery(fixture.DbContextFactory, storageOptions, repository, modelSelection),
            accountSettings,
            modelSelection,
            NullLogger<SettingsCoordinator>.Instance);
        var invalid = ValidAccount();
        invalid.ImapPort = 70_000;

        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.SaveAsync(
            new SettingsSaveRequest(invalid, null, ModelProfileCatalog.Phi4MiniPreset, null),
            CancellationToken.None));

        Assert.Empty(await fixture.Db.Accounts.AsNoTracking().ToListAsync());
        Assert.Empty(modelSelection.Selections);
    }

    [Fact]
    public async Task Recovery_restores_snapshots_when_database_commit_marker_is_absent()
    {
        using var directory = new TemporaryDirectory();
        await using var fixture = await DatabaseFixture.CreateAsync();
        var repository = new AtomicConfigurationRepository();
        var options = Options(directory.Path);
        var modelSelection = new RecordingModelSelectionService();
        await File.WriteAllTextAsync(options.ProtectedSettingsPath, "old-protected");
        await File.WriteAllTextAsync(options.ModelSettingsPath, "old-model");
        var journal = new SettingsTransactionJournal(
            SettingsTransactionJournal.CurrentSchemaVersion,
            Guid.NewGuid(),
            Guid.NewGuid(),
            ModelProfileCatalog.Phi4MiniPreset,
            await repository.CaptureAsync(options.ProtectedSettingsPath, CancellationToken.None),
            await repository.CaptureAsync(options.ModelSettingsPath, CancellationToken.None));
        await repository.WriteAsync(
            SettingsTransactionJournal.Definition(options),
            journal,
            CancellationToken.None);
        await File.WriteAllTextAsync(options.ProtectedSettingsPath, "new-protected");
        await File.WriteAllTextAsync(options.ModelSettingsPath, "new-model");

        await Recovery(fixture.DbContextFactory, options, repository, modelSelection)
            .RecoverPendingAsync(CancellationToken.None);

        Assert.Equal("old-protected", await File.ReadAllTextAsync(options.ProtectedSettingsPath));
        Assert.Equal("old-model", await File.ReadAllTextAsync(options.ModelSettingsPath));
        Assert.False(File.Exists(options.SettingsTransactionJournalPath));
        Assert.Equal([ModelProfileCatalog.Phi4MiniPreset], modelSelection.Selections);
    }

    [Fact]
    public async Task Recovery_keeps_new_files_when_database_commit_marker_is_visible()
    {
        using var directory = new TemporaryDirectory();
        await using var fixture = await DatabaseFixture.CreateAsync();
        var repository = new AtomicConfigurationRepository();
        var options = Options(directory.Path);
        var modelSelection = new RecordingModelSelectionService();
        var account = ValidAccount();
        account.UpdatedAt = DateTimeOffset.UtcNow;
        fixture.Db.Accounts.Add(account);
        await fixture.Db.SaveChangesAsync();
        await File.WriteAllTextAsync(options.ProtectedSettingsPath, "old-protected");
        await File.WriteAllTextAsync(options.ModelSettingsPath, "old-model");
        var transactionId = Guid.NewGuid();
        var journal = new SettingsTransactionJournal(
            SettingsTransactionJournal.CurrentSchemaVersion,
            transactionId,
            account.Id,
            ModelProfileCatalog.Phi4MiniPreset,
            await repository.CaptureAsync(options.ProtectedSettingsPath, CancellationToken.None),
            await repository.CaptureAsync(options.ModelSettingsPath, CancellationToken.None));
        await repository.WriteAsync(
            SettingsTransactionJournal.Definition(options),
            journal,
            CancellationToken.None);
        await fixture.Db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO SettingsConfigurationCommits (TransactionId, CreatedAtUtc) VALUES ({transactionId.ToString()}, {DateTimeOffset.UtcNow})");
        await File.WriteAllTextAsync(options.ProtectedSettingsPath, "new-protected");
        await File.WriteAllTextAsync(options.ModelSettingsPath, "new-model");

        await Recovery(fixture.DbContextFactory, options, repository, modelSelection)
            .RecoverPendingAsync(CancellationToken.None);

        Assert.Equal("new-protected", await File.ReadAllTextAsync(options.ProtectedSettingsPath));
        Assert.Equal("new-model", await File.ReadAllTextAsync(options.ModelSettingsPath));
        Assert.False(File.Exists(options.SettingsTransactionJournalPath));
        Assert.Empty(modelSelection.Selections);
    }

    private static StorageOptions Options(string directory) => new()
    {
        DatabasePath = Path.Combine(directory, "vigilo.db"),
        ProtectedSettingsPath = Path.Combine(directory, "protected-settings.json"),
        ModelSettingsPath = Path.Combine(directory, "model-settings.json"),
        SettingsTransactionJournalPath = Path.Combine(directory, "settings-transaction.json")
    };

    private static SettingsTransactionRecoveryService Recovery(
        IDbContextFactory<VigiloDbContext> dbContextFactory,
        StorageOptions options,
        IAtomicConfigurationRepository repository,
        IModelSelectionService modelSelection) =>
        new(dbContextFactory, options, repository, modelSelection, NullLogger<SettingsTransactionRecoveryService>.Instance);

    private static EmailAccount ValidAccount() => new()
    {
        Name = "Primary",
        EmailAddress = "owner@example.com",
        Username = "owner@example.com",
        ImapHost = "imap.example.com",
        ImapPort = 993,
        FoldersToMonitor = "Inbox",
        PollingFallbackMinutes = 15,
        DataRetentionDays = 180
    };

    private sealed class FailingPasswordAccountSettings(VigiloDbContext db) : IAccountSettingsService
    {
        public Task<IReadOnlyList<EmailAccount>> GetAccountsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<EmailAccount>>([]);

        public Task<EmailAccount?> GetAccountAsync(Guid accountId, CancellationToken cancellationToken) =>
            Task.FromResult<EmailAccount?>(null);

        public Task<EmailAccount> GetOrCreateDefaultAccountAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public async Task SaveAccountAsync(EmailAccount account, CancellationToken cancellationToken)
        {
            db.Accounts.Add(account);
            await db.SaveChangesAsync(cancellationToken);
        }

        public Task DeleteAccountAsync(Guid accountId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> HasPasswordAsync(Guid accountId, CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public Task<string?> GetPasswordAsync(Guid accountId, CancellationToken cancellationToken) =>
            Task.FromResult<string?>(null);

        public Task SavePasswordAsync(Guid accountId, string password, CancellationToken cancellationToken) =>
            throw new IOException("Injected password-store failure.");

        public Task DeletePasswordAsync(Guid accountId, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class RecordingModelSelectionService : IModelSelectionService
    {
        private string preset = ModelProfileCatalog.Phi4MiniPreset;

        public List<string> Selections { get; } = [];

        public IReadOnlyList<AiModelProfile> GetProfiles() => ModelProfileCatalog.Profiles;

        public AiModelProfile GetCurrentProfile() => ModelProfileCatalog.GetProfile(preset);

        public Task SelectProfileAsync(string selectedPreset, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            preset = ModelProfileCatalog.GetProfile(selectedPreset).Preset;
            Selections.Add(preset);
            return Task.CompletedTask;
        }
    }

    private sealed class DatabaseFixture(
        SqliteConnection connection,
        VigiloDbContext db,
        TestDbContextFactory dbContextFactory) : IAsyncDisposable
    {
        public VigiloDbContext Db { get; } = db;
        public TestDbContextFactory DbContextFactory { get; } = dbContextFactory;

        public static async Task<DatabaseFixture> CreateAsync()
        {
            var connection = new SqliteConnection("DataSource=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<VigiloDbContext>().UseSqlite(connection).Options;
            var db = new VigiloDbContext(options);
            await db.Database.EnsureCreatedAsync();
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TABLE IF NOT EXISTS SettingsConfigurationCommits (
                    TransactionId TEXT NOT NULL PRIMARY KEY,
                    CreatedAtUtc TEXT NOT NULL);
                """);
            return new DatabaseFixture(connection, db, new TestDbContextFactory(options));
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Vigilo.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
