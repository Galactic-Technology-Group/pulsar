using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Nms.Data;

/// <summary>Used only by `dotnet ef` tooling.</summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<NmsDbContext>
{
    public NmsDbContext CreateDbContext(string[] args)
    {
        var cs = Environment.GetEnvironmentVariable("NMS_DB") ?? "Host=localhost;Database=nms;Username=nms;Password=nms";
        var opts = new DbContextOptionsBuilder<NmsDbContext>()
            .UseNpgsql(cs, o => o.MigrationsHistoryTable("__ef_migrations", "nms"))
            .Options;
        return new NmsDbContext(opts);
    }
}
