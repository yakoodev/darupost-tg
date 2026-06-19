using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace TgAutoposter.Infrastructure.Persistence;

/// <summary>
/// Used only by the EF Core tooling (`dotnet ef migrations`/`database update`) so migrations can be
/// generated without spinning up the API host. The connection string here never has to be reachable —
/// migrations are emitted from the model, not the database.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres")
            ?? "Host=localhost;Port=5432;Database=tg_autoposter;Username=tg_autoposter;Password=tg_autoposter";

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new AppDbContext(options);
    }
}
