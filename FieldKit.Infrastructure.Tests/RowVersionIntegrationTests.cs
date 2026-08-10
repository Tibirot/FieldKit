using FieldKit.BuildingBlocks;
using FieldKit.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace FieldKit.Infrastructure.Tests;

/// <summary>
/// The row version, on real Postgres (ADR-0013).
/// </summary>
/// <remarks>
/// These run against a container rather than in memory because the property under test is about
/// what the database does — a counter guarded by a concurrency token, in the same transaction as
/// the rows it numbers. An in-memory provider would agree with any implementation, including the
/// sequence-based one ADR-0013 rejects.
/// </remarks>
public class RowVersionIntegrationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine").Build();
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 10, 9, 0, 0, TimeSpan.Zero);
    private readonly TenantId _tenant = TenantId.New();
    private readonly TenantId _otherTenant = TenantId.New();
    private ServiceProvider _provider = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        var services = new ServiceCollection();
        services.AddSingleton<IClock>(new FixedClock(FixedNow));
        services.AddScoped<ITenantContext>(_ => new FakeTenantContext(_tenant, "maria"));
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
    public async Task Versions_start_at_one_so_a_cursor_of_zero_is_below_every_change()
    {
        // A device's first pull carries cursor 0. If the first change were also 0, `rowVersion >
        // cursor` would hide it and the device would start life missing a row.
        var version = await SaveWidgetAsync("Shelf strip");

        Assert.Equal(1, version);
    }

    [Fact]
    public async Task Each_save_takes_the_next_version()
    {
        var first = await SaveWidgetAsync("Shelf strip");
        var second = await SaveWidgetAsync("End cap");
        var third = await SaveWidgetAsync("Gondola");

        Assert.Equal([1, 2, 3], new[] { first, second, third });
    }

    [Fact]
    public async Task Everything_saved_together_shares_one_version()
    {
        // The counter counts change *sets*. A fifty-visit push must not burn fifty numbers, and a
        // device applying that batch sees one coherent step rather than fifty.
        using var scope = _provider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<TestDbContext>();

        var widgets = new[] { new Widget { Name = "A" }, new Widget { Name = "B" }, new Widget { Name = "C" } };
        context.Widgets.AddRange(widgets);
        await context.SaveChangesAsync();

        Assert.Single(widgets.Select(widget => widget.RowVersion).Distinct());
        Assert.Equal(1, widgets[0].RowVersion);
    }

    [Fact]
    public async Task An_update_takes_a_new_version_so_the_change_is_visible_to_a_delta()
    {
        var widgetId = await SaveWidgetReturningIdAsync("Shelf strip");

        using var scope = _provider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<TestDbContext>();
        var widget = await context.Widgets.SingleAsync(candidate => candidate.Id == widgetId);
        widget.Name = "Shelf strip (renamed)";
        await context.SaveChangesAsync();

        Assert.Equal(2, widget.RowVersion);
    }

    [Fact]
    public async Task A_save_that_changes_nothing_syncable_does_not_consume_a_version()
    {
        await SaveWidgetAsync("Shelf strip");

        using (var scope = _provider.CreateScope())
        {
            // No tracked entity is dirty, so there is no change for a version to name.
            var context = scope.ServiceProvider.GetRequiredService<TestDbContext>();
            await context.SaveChangesAsync();
        }

        var next = await SaveWidgetAsync("End cap");

        Assert.Equal(2, next);
    }

    [Fact]
    public async Task Tenants_are_numbered_independently()
    {
        await SaveWidgetAsync("Shelf strip");
        await SaveWidgetAsync("End cap");

        // A second tenant's first change is *its* first change. Sharing a counter would leak the
        // first tenant's write volume into the second's watermarks — and tell it how busy they are.
        var otherServices = new ServiceCollection();
        otherServices.AddSingleton<IClock>(new FixedClock(FixedNow));
        otherServices.AddScoped<ITenantContext>(_ => new FakeTenantContext(_otherTenant, "ana"));
        otherServices.AddModuleDbContext<TestDbContext>(_postgres.GetConnectionString(), "test");
        await using var otherProvider = otherServices.BuildServiceProvider();

        using var scope = otherProvider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<TestDbContext>();
        var widget = new Widget { Name = "Other tenant's shelf" };
        context.Widgets.Add(widget);
        await context.SaveChangesAsync();

        Assert.Equal(1, widget.RowVersion);
    }

    [Fact]
    public async Task Concurrent_saves_cannot_both_take_the_same_version()
    {
        // The property the whole design exists for. Two transactions reach the counter holding the
        // same original value; the token means one UPDATE matches no row and fails. Without it both
        // would commit the same number, and a device that stored it would skip whichever it did not
        // happen to read.
        await SaveWidgetAsync("Seed");

        using var first = _provider.CreateScope();
        using var second = _provider.CreateScope();

        var firstContext = first.ServiceProvider.GetRequiredService<TestDbContext>();
        var secondContext = second.ServiceProvider.GetRequiredService<TestDbContext>();

        // Both must hold the counter at the *same* original value before either commits — that is
        // what a race is. Loading it here is the difference between this test and the version I
        // wrote first, which added to both contexts and then saved them in order: the second read
        // the counter fresh during its own save, found the new value, and passed while proving
        // nothing.
        await firstContext.Set<ChangeSequence>().ToListAsync();
        await secondContext.Set<ChangeSequence>().ToListAsync();

        firstContext.Widgets.Add(new Widget { Name = "First" });
        secondContext.Widgets.Add(new Widget { Name = "Second" });

        await firstContext.SaveChangesAsync();

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => secondContext.SaveChangesAsync());
    }

    [Fact]
    public async Task A_rolled_back_save_returns_its_version_rather_than_burning_it()
    {
        // Gaplessness is not required by the protocol, but it is what makes a feed readable when a
        // device claims to be at 8830 and someone has to find out what 8831 was. A sequence cannot
        // do this: `nextval` is not transactional.
        var existingId = await SaveWidgetReturningIdAsync("Shelf strip");

        using (var scope = _provider.CreateScope())
        {
            // A primary-key collision, because it is a constraint this schema definitely has. The
            // first attempt at this test used an over-long name, which the test context does not
            // constrain — so nothing threw and the assertion below passed for the wrong reason.
            var context = scope.ServiceProvider.GetRequiredService<TestDbContext>();
            context.Widgets.Add(new Widget { Id = existingId, Name = "Duplicate" });
            await Assert.ThrowsAnyAsync<DbUpdateException>(() => context.SaveChangesAsync());
        }

        var next = await SaveWidgetAsync("End cap");

        Assert.Equal(2, next);
    }

    private async Task<long> SaveWidgetAsync(string name)
    {
        using var scope = _provider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<TestDbContext>();
        var widget = new Widget { Name = name };
        context.Widgets.Add(widget);
        await context.SaveChangesAsync();
        return widget.RowVersion;
    }

    private async Task<Guid> SaveWidgetReturningIdAsync(string name)
    {
        using var scope = _provider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<TestDbContext>();
        var widget = new Widget { Name = name };
        context.Widgets.Add(widget);
        await context.SaveChangesAsync();
        return widget.Id;
    }
}
