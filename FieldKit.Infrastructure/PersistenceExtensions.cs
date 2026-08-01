using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FieldKit.Infrastructure;

public static class PersistenceExtensions
{
    /// <summary>
    /// Registers a module's <see cref="ModuleDbContext"/> with the FieldKit conventions: Npgsql and
    /// the entity-stamping interceptor (tenant + audit). Each module calls this for its own context.
    /// </summary>
    public static IServiceCollection AddModuleDbContext<TContext>(
        this IServiceCollection services,
        string connectionString)
        where TContext : ModuleDbContext
    {
        services.AddScoped<EntityStampingInterceptor>();

        services.AddDbContext<TContext>((serviceProvider, options) =>
            options
                .UseNpgsql(connectionString)
                .AddInterceptors(serviceProvider.GetRequiredService<EntityStampingInterceptor>()));

        return services;
    }
}
