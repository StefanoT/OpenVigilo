using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Vigilo.Storage;

public sealed class VigiloDbContextDesignFactory : IDesignTimeDbContextFactory<VigiloDbContext>
{
    public VigiloDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<VigiloDbContext>()
            .UseSqlite("Data Source=vigilo-design.db")
            .Options;

        return new VigiloDbContext(options);
    }
}
