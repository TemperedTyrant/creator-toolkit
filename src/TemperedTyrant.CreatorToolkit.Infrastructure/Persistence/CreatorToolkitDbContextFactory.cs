using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace TemperedTyrant.CreatorToolkit.Infrastructure.Persistence;

public sealed class CreatorToolkitDbContextFactory
    : IDesignTimeDbContextFactory<CreatorToolkitDbContext>
{
    public CreatorToolkitDbContext CreateDbContext(string[] args)
    {
        DbContextOptions<CreatorToolkitDbContext> options =
            new DbContextOptionsBuilder<CreatorToolkitDbContext>()
                .UseSqlite("Data Source=:memory:;Foreign Keys=True")
                .Options;

        return new CreatorToolkitDbContext(options);
    }
}
