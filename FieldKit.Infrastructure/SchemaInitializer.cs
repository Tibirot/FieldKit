using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace FieldKit.Infrastructure;

/// <summary>
/// Dev-time schema bootstrap: ensures a module's schema + tables exist on startup.
/// <para>
/// TEMPORARY. <c>EnsureCreated</c> is all-or-nothing per database, so it stops working once a second
/// module context shares the database — it is replaced by per-module EF migrations
/// (<c>Database.MigrateAsync</c>) in the next slice.
/// </para>
/// </summary>
public sealed class SchemaInitializer<TContext>(IServiceProvider services) : IHostedService
    where TContext : ModuleDbContext
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<TContext>();
        await context.Database.EnsureCreatedAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
