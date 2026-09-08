using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Vigilo.Configuration;
using Vigilo.Core;

namespace Vigilo.Storage;

public sealed class AccountSettingsService : IAccountSettingsService
{
    private const int ProtectedSettingsSchemaVersion = 1;

    private readonly IDbContextFactory<VigiloDbContext> dbContextFactory;
    private readonly IAtomicConfigurationRepository configurationRepository;
    private readonly ConfigurationFileDefinition<ProtectedSettings> protectedSettingsDefinition;

    public AccountSettingsService(
        IDbContextFactory<VigiloDbContext> dbContextFactory,
        StorageOptions options,
        IAtomicConfigurationRepository configurationRepository)
    {
        this.dbContextFactory = dbContextFactory;
        this.configurationRepository = configurationRepository;
        protectedSettingsDefinition = new(
            options.ProtectedSettingsPath,
            ProtectedSettingsSchemaVersion,
            static () => new ProtectedSettings { SchemaVersion = ProtectedSettingsSchemaVersion },
            static settings => settings.SchemaVersion,
            static (settings, targetVersion) => MigrateProtectedSettings(settings, targetVersion),
            ValidateProtectedSettings);
    }

    public async Task<IReadOnlyList<EmailAccount>> GetAccountsAsync(CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var accounts = await dbContext.Accounts.AsNoTracking().ToListAsync(cancellationToken);
        return accounts
            .OrderBy(x => x.CreatedAt)
            .ThenBy(x => x.EmailAddress)
            .ToList();
    }

    public async Task<EmailAccount?> GetAccountAsync(Guid accountId, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await dbContext.Accounts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == accountId, cancellationToken);
    }

    public async Task<EmailAccount> GetOrCreateDefaultAccountAsync(CancellationToken cancellationToken)
    {
        var account = (await GetAccountsAsync(cancellationToken)).FirstOrDefault();
        if (account is not null)
        {
            return account;
        }

        account = new EmailAccount();
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        dbContext.Accounts.Add(account);
        await dbContext.SaveChangesAsync(cancellationToken);
        return account;
    }

    public async Task SaveAccountAsync(EmailAccount account, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await SaveAccountAsync(dbContext, account, cancellationToken);
    }

    internal static async Task SaveAccountAsync(
        VigiloDbContext dbContext,
        EmailAccount account,
        CancellationToken cancellationToken)
    {
        SettingsValidator.ValidateAccount(account);
        account.UpdatedAt = DateTimeOffset.UtcNow;
        var exists = await dbContext.Accounts.AnyAsync(x => x.Id == account.Id, cancellationToken);
        if (exists)
        {
            dbContext.Accounts.Update(account);
        }
        else
        {
            dbContext.Accounts.Add(account);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAccountAsync(Guid accountId, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var account = await dbContext.Accounts.FirstOrDefaultAsync(x => x.Id == accountId, cancellationToken);
        if (account is not null)
        {
            var messages = await dbContext.EmailMessages
                .Where(x => x.AccountId == accountId)
                .ToListAsync(cancellationToken);
            var messageIds = messages.Select(x => x.Id).ToHashSet();
            var trackedItems = await dbContext.TrackedItems
                .Where(x => messageIds.Contains(x.EmailMessageId))
                .ToListAsync(cancellationToken);
            var trackedItemIds = trackedItems.Select(x => x.Id).ToHashSet();
            var notificationRecords = await dbContext.NotificationRecords
                .Where(x => trackedItemIds.Contains(x.TrackedItemId))
                .ToListAsync(cancellationToken);
            var processed = await dbContext.ProcessedEmails
                .Where(x => x.AccountId == accountId)
                .ToListAsync(cancellationToken);
            var outlookOperations = await dbContext.OutlookCategorySyncOperations
                .Where(x => messageIds.Contains(x.VigiloMessageId)).ToListAsync(cancellationToken);
            var outlookItems = await dbContext.OutlookItemBindings
                .Where(x => messageIds.Contains(x.VigiloMessageId)).ToListAsync(cancellationToken);
            var outlookBinding = await dbContext.OutlookStoreBindings
                .FirstOrDefaultAsync(x => x.VigiloMailboxId == accountId, cancellationToken);
            var senderRules = await dbContext.SenderRules
                .Where(x => x.AccountId == accountId)
                .ToListAsync(cancellationToken);

            dbContext.NotificationRecords.RemoveRange(notificationRecords);
            dbContext.OutlookCategorySyncOperations.RemoveRange(outlookOperations);
            dbContext.OutlookItemBindings.RemoveRange(outlookItems);
            if (outlookBinding is not null) dbContext.OutlookStoreBindings.Remove(outlookBinding);
            dbContext.SenderRules.RemoveRange(senderRules);
            dbContext.TrackedItems.RemoveRange(trackedItems);
            dbContext.ProcessedEmails.RemoveRange(processed);
            dbContext.EmailMessages.RemoveRange(messages);
            dbContext.Accounts.Remove(account);
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        await DeletePasswordAsync(accountId, cancellationToken);
    }

    public async Task<string?> GetPasswordAsync(Guid accountId, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var settings = await LoadProtectedSettingsAsync(cancellationToken);
        if (!settings.Passwords.TryGetValue(accountId, out var protectedValue))
        {
            return null;
        }

        try
        {
            var bytes = Convert.FromBase64String(protectedValue);
            var unprotected = ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(unprotected);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    public async Task<bool> HasPasswordAsync(Guid accountId, CancellationToken cancellationToken)
    {
        var settings = await LoadProtectedSettingsAsync(cancellationToken);
        return settings.Passwords.ContainsKey(accountId);
    }

    public async Task SavePasswordAsync(Guid accountId, string password, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Protected application passwords require Windows DPAPI.");
        }

        if (accountId == Guid.Empty)
        {
            throw new ArgumentException("An account identifier is required.", nameof(accountId));
        }

        if (string.IsNullOrWhiteSpace(password))
        {
            throw new ArgumentException("An app password cannot be empty.", nameof(password));
        }

        var protectedBytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(password), null, DataProtectionScope.CurrentUser);
        var protectedValue = Convert.ToBase64String(protectedBytes);
        await configurationRepository.UpdateAsync(
            protectedSettingsDefinition,
            settings =>
            {
                settings.Passwords[accountId] = protectedValue;
                return settings;
            },
            cancellationToken);
    }

    public async Task DeletePasswordAsync(Guid accountId, CancellationToken cancellationToken)
    {
        if (accountId == Guid.Empty)
        {
            return;
        }

        await configurationRepository.UpdateAsync(
            protectedSettingsDefinition,
            settings =>
            {
                settings.Passwords.Remove(accountId);
                return settings;
            },
            cancellationToken);
    }

    private Task<ProtectedSettings> LoadProtectedSettingsAsync(CancellationToken cancellationToken) =>
        configurationRepository.ReadAsync(protectedSettingsDefinition, cancellationToken);

    private static ProtectedSettings MigrateProtectedSettings(ProtectedSettings settings, int targetVersion)
    {
        if (settings.SchemaVersion is not 0)
        {
            throw new InvalidDataException(
                $"Protected settings schema version {settings.SchemaVersion} cannot be migrated.");
        }

        settings.SchemaVersion = targetVersion;
        settings.Passwords ??= [];
        return settings;
    }

    private static void ValidateProtectedSettings(ProtectedSettings settings)
    {
        if (settings.Passwords is null)
        {
            throw new InvalidDataException("Protected settings must contain a password map.");
        }

        foreach (var (accountId, protectedValue) in settings.Passwords)
        {
            if (accountId == Guid.Empty || string.IsNullOrWhiteSpace(protectedValue))
            {
                throw new InvalidDataException("Protected settings contain an invalid account password entry.");
            }

            try
            {
                var protectedBytes = Convert.FromBase64String(protectedValue);
                if (OperatingSystem.IsWindows())
                {
                    var unprotected = ProtectedData.Unprotect(
                        protectedBytes,
                        optionalEntropy: null,
                        DataProtectionScope.CurrentUser);
                    CryptographicOperations.ZeroMemory(unprotected);
                }
            }
            catch (FormatException ex)
            {
                throw new InvalidDataException(
                    $"Protected settings contain an invalid encrypted value for account {accountId}.",
                    ex);
            }
            catch (CryptographicException ex)
            {
                throw new InvalidDataException(
                    $"Protected settings contain ciphertext that cannot be decrypted for account {accountId}.",
                    ex);
            }
        }
    }

    private sealed class ProtectedSettings
    {
        public int SchemaVersion { get; set; }
        public Dictionary<Guid, string> Passwords { get; set; } = [];
    }
}
