using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Nms.Data;

public static class DbInitializer
{
    /// <summary>Applies EF migrations, then the Timescale setup. Safe to call on every start.</summary>
    public static async Task InitializeAsync(IServiceProvider services, CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NmsDbContext>();
        var log = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("DbInitializer");

        await db.Database.MigrateAsync(ct);

        try
        {
            await db.Database.ExecuteSqlRawAsync(TimescaleSql.Setup, ct);
            log.LogInformation("Timescale setup applied");
        }
        catch (Exception ex)
        {
            // Timescale may be missing in local dev; history still works as a plain table.
            log.LogWarning(ex, "Timescale setup failed; check_results will be a plain table");
        }
    }
}
