using Microsoft.EntityFrameworkCore;
using Vigilo.Storage;

namespace Vigilo.Tests;

internal sealed class TestDbContextFactory(DbContextOptions<VigiloDbContext> options)
    : IDbContextFactory<VigiloDbContext>
{
    private int createdCount;

    public int CreatedCount => Volatile.Read(ref createdCount);

    public VigiloDbContext CreateDbContext()
    {
        Interlocked.Increment(ref createdCount);
        return new(options);
    }

    public Task<VigiloDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(CreateDbContext());
    }
}
