using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace FieldKit.Infrastructure;

/// <summary>
/// Applies a module's EF Core migrations on startup (<c>Database.MigrateAsync</c>). Each module owns
/// its migrations and its own <c>__EFMigrationsHistory</c> table in its own schema (ADR-0005), so
/// contexts sharing the database never collide — unlike <c>EnsureCreated</c>, which is all-or-nothing.
/// </summary>
public sealed class ModuleMigrator<TContext>(IServiceProvider services) : IHostedService
    where TContext : ModuleDbContext
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<TContext>();
        await context.Database.MigrateAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
