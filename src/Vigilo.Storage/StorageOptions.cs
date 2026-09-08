namespace Vigilo.Storage;

public sealed class StorageOptions
{
    public string DatabasePath { get; set; } = "";
    public string ProtectedSettingsPath { get; set; } = "";
    public string ModelSettingsPath { get; set; } = "";
    public string SettingsTransactionJournalPath { get; set; } = "";

    /// <summary>
    /// Path of the classification model-response cache file. Local-data maintenance deletes it
    /// together with purged messages so derived model responses never outlive their sources.
    /// Must match the path passed to the classification registration.
    /// </summary>
    public string ClassificationResponseCachePath { get; set; } = "";
}
