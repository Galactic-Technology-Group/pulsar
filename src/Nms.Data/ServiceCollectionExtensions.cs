using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Nms.Data;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddNmsData(this IServiceCollection services, string connectionString)
    {
        services.AddDbContextPool<NmsDbContext>(o =>
            o.UseNpgsql(connectionString, n => n.MigrationsHistoryTable("__ef_migrations", "nms")));
        return services;
    }
}
