using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;

namespace Vigilo.Configuration;

public sealed record ConfigurationFileDefinition<T>(
    string Path,
    int CurrentVersion,
    Func<T> CreateDefault,
    Func<T, int> GetVersion,
    Func<T, int, T> Migrate,
    Action<T> Validate)
{
    public string FullPath { get; } = System.IO.Path.GetFullPath(
        string.IsNullOrWhiteSpace(Path)
            ? throw new ArgumentException("A configuration file path is required.", nameof(Path))
            : Path);
}

public interface IAtomicConfigurationRepository
{
    Task<T> ReadAsync<T>(ConfigurationFileDefinition<T> definition, CancellationToken cancellationToken);

    Task WriteAsync<T>(
        ConfigurationFileDefinition<T> definition,
        T document,
        CancellationToken cancellationToken);

    Task<T> UpdateAsync<T>(
        ConfigurationFileDefinition<T> definition,
        Func<T, T> update,
        CancellationToken cancellationToken);

    Task<ConfigurationFileSnapshot> CaptureAsync(string path, CancellationToken cancellationToken);

    Task RestoreAsync(ConfigurationFileSnapshot snapshot, CancellationToken cancellationToken);

    Task DeleteAsync(string path, CancellationToken cancellationToken);
}

public sealed record ConfigurationFileSnapshot(
    string Path,
    bool Exists,
    byte[] Content,
    string Sha256);

public sealed class AtomicConfigurationRepository : IAtomicConfigurationRepository
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> FileGates =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    public async Task<T> ReadAsync<T>(
        ConfigurationFileDefinition<T> definition,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var gate = GateFor(definition.FullPath);
        await gate.WaitAsync(cancellationToken);
        try
        {
            return await ReadCoreAsync(definition, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task WriteAsync<T>(
        ConfigurationFileDefinition<T> definition,
        T document,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(document);
        var gate = GateFor(definition.FullPath);
        await gate.WaitAsync(cancellationToken);
        try
        {
            await WriteCoreAsync(definition, document, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<T> UpdateAsync<T>(
        ConfigurationFileDefinition<T> definition,
        Func<T, T> update,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(update);
        var gate = GateFor(definition.FullPath);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var current = await ReadCoreAsync(definition, cancellationToken);
            var updated = update(current)
                ?? throw new InvalidDataException("The configuration update returned no document.");
            await WriteCoreAsync(definition, updated, cancellationToken);
            return updated;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<ConfigurationFileSnapshot> CaptureAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var fullPath = NormalizePath(path);
        var gate = GateFor(fullPath);
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(fullPath))
            {
                return new ConfigurationFileSnapshot(fullPath, false, [], ComputeHash([]));
            }

            var content = await File.ReadAllBytesAsync(fullPath, cancellationToken);
            return new ConfigurationFileSnapshot(fullPath, true, content, ComputeHash(content));
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task RestoreAsync(
        ConfigurationFileSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ValidateSnapshot(snapshot);
        var fullPath = NormalizePath(snapshot.Path);
        var gate = GateFor(fullPath);
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (snapshot.Exists)
            {
                await WriteBytesCoreAsync(fullPath, snapshot.Content, snapshot.Sha256, cancellationToken);
            }
            else if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task DeleteAsync(string path, CancellationToken cancellationToken)
    {
        var fullPath = NormalizePath(path);
        var gate = GateFor(fullPath);
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private static SemaphoreSlim GateFor(string path) =>
        FileGates.GetOrAdd(path, static _ => new SemaphoreSlim(1, 1));

    private static async Task<T> ReadCoreAsync<T>(
        ConfigurationFileDefinition<T> definition,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(definition.FullPath))
        {
            var created = definition.CreateDefault()
                ?? throw new InvalidDataException("The configuration default factory returned no document.");
            EnsureCurrentAndValid(definition, created);
            return created;
        }

        var document = await DeserializeAsync<T>(definition.FullPath, cancellationToken);
        var version = definition.GetVersion(document);
        if (version > definition.CurrentVersion)
        {
            throw new InvalidDataException(
                $"Configuration '{definition.FullPath}' uses unsupported schema version {version}; " +
                $"this build supports up to version {definition.CurrentVersion}.");
        }

        if (version < definition.CurrentVersion)
        {
            document = definition.Migrate(document, definition.CurrentVersion)
                ?? throw new InvalidDataException("The configuration migration returned no document.");
            EnsureCurrentAndValid(definition, document);
            await WriteCoreAsync(definition, document, cancellationToken);
            return document;
        }

        EnsureCurrentAndValid(definition, document);
        return document;
    }

    private static async Task WriteCoreAsync<T>(
        ConfigurationFileDefinition<T> definition,
        T document,
        CancellationToken cancellationToken)
    {
        EnsureCurrentAndValid(definition, document);

        await using var buffer = new MemoryStream();
        await JsonSerializer.SerializeAsync(buffer, document, SerializerOptions, cancellationToken);
        var content = buffer.ToArray();
        var persisted = JsonSerializer.Deserialize<T>(content, SerializerOptions)
            ?? throw new InvalidDataException("The serialized configuration contains an empty document.");
        EnsureCurrentAndValid(definition, persisted);
        await WriteBytesCoreAsync(definition.FullPath, content, ComputeHash(content), cancellationToken);
    }

    private static async Task WriteBytesCoreAsync(
        string fullPath,
        byte[] content,
        string expectedHash,
        CancellationToken cancellationToken)
    {
        var directory = System.IO.Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("The configuration path has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = System.IO.Path.Combine(
            directory,
            $".{System.IO.Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 16 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(content, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            var persistedHash = ComputeHash(await File.ReadAllBytesAsync(temporaryPath, cancellationToken));
            if (!string.Equals(expectedHash, persistedHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Temporary configuration '{temporaryPath}' failed integrity validation.");
            }

            if (File.Exists(fullPath))
            {
                File.Replace(temporaryPath, fullPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporaryPath, fullPath);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static async Task<T> DeserializeAsync<T>(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            return await JsonSerializer.DeserializeAsync<T>(stream, SerializerOptions, cancellationToken)
                ?? throw new InvalidDataException($"Configuration '{path}' contains an empty document.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Configuration '{path}' is not valid JSON.", ex);
        }
    }

    private static void EnsureCurrentAndValid<T>(ConfigurationFileDefinition<T> definition, T document)
    {
        var version = definition.GetVersion(document);
        if (version != definition.CurrentVersion)
        {
            throw new InvalidDataException(
                $"Configuration '{definition.FullPath}' must use schema version {definition.CurrentVersion}, not {version}.");
        }

        definition.Validate(document);
    }

    private static string NormalizePath(string path) => System.IO.Path.GetFullPath(
        string.IsNullOrWhiteSpace(path)
            ? throw new ArgumentException("A configuration file path is required.", nameof(path))
            : path);

    private static void ValidateSnapshot(ConfigurationFileSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot.Content);
        if (!snapshot.Exists && snapshot.Content.Length != 0)
        {
            throw new InvalidDataException("A snapshot for a missing file cannot contain data.");
        }

        var actualHash = ComputeHash(snapshot.Content);
        if (!string.Equals(snapshot.Sha256, actualHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The configuration snapshot failed integrity validation.");
        }
    }

    private static string ComputeHash(byte[] content) => Convert.ToHexString(SHA256.HashData(content));
}
