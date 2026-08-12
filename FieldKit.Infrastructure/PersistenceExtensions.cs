using FieldKit.Infrastructure.Outbox;
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
        string connectionString,
        string schema)
        where TContext : ModuleDbContext
    {
        services.AddScoped<EntityStampingInterceptor>();
        services.AddScoped<RowVersionStampingInterceptor>();
        services.AddSingleton<ConvertIntegrationEventsToOutboxInterceptor>();
        services.AddSingleton<ClientGeneratedKeyGuard>();
        services.AddSingleton<OutboxProcessor>();

        services.AddDbContext<TContext>((serviceProvider, options) =>
            options
                // Keep each module's migrations history in its own schema, so contexts sharing the
                // database never collide on __EFMigrationsHistory (schema-per-module, ADR-0005).
                .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", schema))
                .AddInterceptors(
                    serviceProvider.GetRequiredService<EntityStampingInterceptor>(),
                    serviceProvider.GetRequiredService<RowVersionStampingInterceptor>(),
                    serviceProvider.GetRequiredService<ConvertIntegrationEventsToOutboxInterceptor>(),
                    serviceProvider.GetRequiredService<ClientGeneratedKeyGuard>()));

        return services;
    }
}
