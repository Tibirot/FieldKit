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
        // The meter factory the outbox signals are created through. Registered here rather than left
        // to the host: a module that has an outbox needs one, and "the caller remembers" is the same
        // shape of mistake as the dispatcher list below. Idempotent — the web host already calls it.
        services.AddMetrics();

        services.AddSingleton<OutboxProcessor>();
        services.AddSingleton<OutboxMetrics>();

        /*
         * The dispatcher ADR-0006 describes, registered where the context is (W13 slice 3).
         *
         * Here rather than in a host's startup list, because a list is a thing a module can be left
         * off — and a module left off would go on committing events that were never delivered, which
         * is exactly the failure this slice exists to end. Registering it beside the outbox table
         * means a module cannot have one without the other.
         *
         * A hosted service registered in a plain `ServiceCollection` does not run: only a host runs
         * one. So `Infrastructure.Tests`, which builds a provider by hand to drive `ProcessAsync`
         * directly, is unaffected.
         */
        services.AddHostedService<OutboxDispatcher<TContext>>();

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
