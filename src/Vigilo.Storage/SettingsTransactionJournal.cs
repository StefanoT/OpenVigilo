using System.Security.Cryptography;
using Vigilo.Configuration;

namespace Vigilo.Storage;

internal sealed record SettingsTransactionJournal(
    int SchemaVersion,
    Guid TransactionId,
    Guid AccountId,
    string PreviousModelPreset,
    ConfigurationFileSnapshot PreviousProtectedSettings,
    ConfigurationFileSnapshot PreviousModelSettings)
{
    public const int CurrentSchemaVersion = 1;

    public static ConfigurationFileDefinition<SettingsTransactionJournal> Definition(StorageOptions options) =>
        new(
            options.SettingsTransactionJournalPath,
            CurrentSchemaVersion,
            static () => throw new InvalidDataException("A settings transaction journal cannot be created implicitly."),
            static journal => journal.SchemaVersion,
            static (_, _) => throw new InvalidDataException("Settings transaction journal migration is not supported."),
            journal => Validate(journal, options));

    private static void Validate(SettingsTransactionJournal journal, StorageOptions options)
    {
        if (journal.TransactionId == Guid.Empty || journal.AccountId == Guid.Empty)
        {
            throw new InvalidDataException("The settings transaction journal has no transaction or account identifier.");
        }

        if (string.IsNullOrWhiteSpace(journal.PreviousModelPreset))
        {
            throw new InvalidDataException("The settings transaction journal has no previous model preset.");
        }

        ValidateSnapshot(journal.PreviousProtectedSettings, options.ProtectedSettingsPath);
        ValidateSnapshot(journal.PreviousModelSettings, options.ModelSettingsPath);
    }

    private static void ValidateSnapshot(ConfigurationFileSnapshot snapshot, string expectedPath)
    {
        if (snapshot is null || snapshot.Content is null)
        {
            throw new InvalidDataException("The settings transaction journal contains an empty snapshot.");
        }

        if (!string.Equals(
                Path.GetFullPath(snapshot.Path),
                Path.GetFullPath(expectedPath),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The settings transaction journal references an unexpected file.");
        }

        if (!snapshot.Exists && snapshot.Content.Length != 0)
        {
            throw new InvalidDataException("A missing-file snapshot cannot contain data.");
        }

        var actualHash = Convert.ToHexString(SHA256.HashData(snapshot.Content));
        if (!string.Equals(snapshot.Sha256, actualHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("A settings transaction snapshot failed integrity validation.");
        }
    }
}
