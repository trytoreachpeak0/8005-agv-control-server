using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ControlServer.Infrastructure.Persistence;

public sealed class ControlServerDesignTimeDbContextFactory : IDesignTimeDbContextFactory<ControlServerDbContext>
{
    public ControlServerDbContext CreateDbContext(string[] args)
    {
        _ = args;
        DbContextOptions<ControlServerDbContext> options = new DbContextOptionsBuilder<ControlServerDbContext>()
            .UseSqlite("Data Source=controlserver.design.db")
            .Options;
        return new ControlServerDbContext(options);
    }
}
