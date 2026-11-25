using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Relay.Infrastructure.Persistence;

/// <summary>Used by `dotnet ef` only. No database connection is opened to add a migration.</summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<RelayDbContext>
{
    public RelayDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<RelayDbContext>()
            .UseNpgsql("Host=localhost;Database=relay_design")
            .UseSnakeCaseNamingConvention()
            .Options);
}
