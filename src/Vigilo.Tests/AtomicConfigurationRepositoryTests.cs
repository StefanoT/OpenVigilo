using System.Text.Json;
using Vigilo.Configuration;

namespace Vigilo.Tests;

public sealed class AtomicConfigurationRepositoryTests
{
    [Fact]
    public async Task Concurrent_updates_are_serialized_without_lost_values_or_temporary_files()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "settings.json");
        var definition = Definition(path);
        var repository = new AtomicConfigurationRepository();

        await Task.WhenAll(Enumerable.Range(0, 64).Select(_ =>
            repository.UpdateAsync(
                definition,
                document => document with { Value = document.Value + 1 },
                CancellationToken.None)));

        var stored = await repository.ReadAsync(definition, CancellationToken.None);
        Assert.Equal(64, stored.Value);
        Assert.Empty(Directory.EnumerateFiles(directory.Path, "*.tmp"));
    }

    [Fact]
    public async Task Invalid_replacement_leaves_the_previous_document_unchanged()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "settings.json");
        var definition = Definition(path);
        var repository = new AtomicConfigurationRepository();
        await repository.WriteAsync(definition, new CounterDocument(1, 7), CancellationToken.None);
        var before = await File.ReadAllTextAsync(path);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            repository.WriteAsync(definition, new CounterDocument(1, -1), CancellationToken.None));

        Assert.Equal(before, await File.ReadAllTextAsync(path));
        Assert.Equal(7, (await repository.ReadAsync(definition, CancellationToken.None)).Value);
    }

    [Fact]
    public async Task Legacy_document_is_migrated_and_future_version_is_rejected()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "settings.json");
        await File.WriteAllTextAsync(path, """{"SchemaVersion":0,"Value":3}""");
        var definition = Definition(path);
        var repository = new AtomicConfigurationRepository();

        var migrated = await repository.ReadAsync(definition, CancellationToken.None);
        Assert.Equal(1, migrated.SchemaVersion);
        Assert.Equal(3, migrated.Value);
        using (var json = JsonDocument.Parse(await File.ReadAllTextAsync(path)))
        {
            Assert.Equal(1, json.RootElement.GetProperty("SchemaVersion").GetInt32());
        }

        await File.WriteAllTextAsync(path, """{"SchemaVersion":2,"Value":3}""");
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            repository.ReadAsync(definition, CancellationToken.None));
    }

    [Fact]
    public async Task Snapshot_restore_replaces_existing_content_and_removes_new_files()
    {
        using var directory = new TemporaryDirectory();
        var existingPath = Path.Combine(directory.Path, "existing.json");
        var absentPath = Path.Combine(directory.Path, "absent.json");
        var repository = new AtomicConfigurationRepository();
        await File.WriteAllTextAsync(existingPath, "before");
        var existing = await repository.CaptureAsync(existingPath, CancellationToken.None);
        var absent = await repository.CaptureAsync(absentPath, CancellationToken.None);
        await File.WriteAllTextAsync(existingPath, "after");
        await File.WriteAllTextAsync(absentPath, "created");

        await repository.RestoreAsync(existing, CancellationToken.None);
        await repository.RestoreAsync(absent, CancellationToken.None);

        Assert.Equal("before", await File.ReadAllTextAsync(existingPath));
        Assert.False(File.Exists(absentPath));
    }

    private static ConfigurationFileDefinition<CounterDocument> Definition(string path) => new(
        path,
        CurrentVersion: 1,
        CreateDefault: static () => new CounterDocument(1, 0),
        GetVersion: static document => document.SchemaVersion,
        Migrate: static (document, targetVersion) => document.SchemaVersion == 0
            ? document with { SchemaVersion = targetVersion }
            : throw new InvalidDataException("Unsupported legacy document."),
        Validate: static document =>
        {
            if (document.Value < 0)
            {
                throw new InvalidDataException("The value cannot be negative.");
            }
        });

    private sealed record CounterDocument(int SchemaVersion, int Value);

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "Vigilo.Tests",
                Guid.NewGuid().ToString("N"));
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
