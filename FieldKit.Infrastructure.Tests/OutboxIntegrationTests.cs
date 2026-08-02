using FieldKit.BuildingBlocks;
using FieldKit.Infrastructure;
using FieldKit.Infrastructure.Outbox;
using FieldKit.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace FieldKit.Infrastructure.Tests;

/// <summary>
/// The transactional outbox, on real Postgres: an aggregate's integration event is written to the
/// outbox in the same transaction as the save, then the processor delivers it to its handler
/// exactly once (at-least-once delivery + idempotent, ADR-0006).
/// </summary>
public class OutboxIntegrationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine").Build();
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 2, 9, 0, 0, TimeSpan.Zero);
    private readonly TenantId _tenant = TenantId.New();
    private ServiceProvider _provider = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        var services = new ServiceCollection();
        services.AddSingleton<IClock>(new FixedClock(FixedNow));
        services.AddScoped<ITenantContext>(_ => new FakeTenantContext(_tenant, "maria"));
        services.AddSingleton<EventRecorder>();
        services.AddScoped<IIntegrationEventHandler<WidgetCreated>, WidgetCreatedHandler>();
        services.AddModuleDbContext<TestDbContext>(_postgres.GetConnectionString(), "test");
        _provider = services.BuildServiceProvider();

        using var scope = _provider.CreateScope();
        await scope.ServiceProvider.GetRequiredService<TestDbContext>().Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    [Fact]
    public async Task Save_writes_event_to_outbox_and_processing_delivers_it_exactly_once()
    {
        Guid widgetId;
        using (var scope = _provider.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<TestDbContext>();
            var widget = new Widget { Name = "Shelf strip" };
            widget.MarkCreated(FixedNow);
            context.Widgets.Add(widget);
            await context.SaveChangesAsync();
            widgetId = widget.Id;
        }

        // Committed with the widget, not yet processed.
        Assert.Equal(1, await CountPendingAsync());

        var processor = _provider.GetRequiredService<OutboxProcessor>();
        var recorder = _provider.GetRequiredService<EventRecorder>();

        var handled = await processor.ProcessAsync<TestDbContext>();

        Assert.Equal(1, handled);
        Assert.Equal([widgetId], recorder.Handled);
        Assert.Equal(0, await CountPendingAsync());

        // Idempotent: a second run delivers nothing and doesn't re-invoke the handler.
        Assert.Equal(0, await processor.ProcessAsync<TestDbContext>());
        Assert.Single(recorder.Handled);
    }

    private async Task<int> CountPendingAsync()
    {
        using var scope = _provider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<TestDbContext>();
        return await context.Set<OutboxMessage>().CountAsync(m => m.ProcessedOnUtc == null);
    }
}
