using FieldKit.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace FieldKit.Infrastructure.Tests;

/// <summary>
/// A child added to an aggregate that is <b>already tracked</b> is an insert, not an update — the
/// defect <see cref="ClientGeneratedKeyConvention"/> ends.
/// </summary>
/// <remarks>
/// <para>
/// This had five occurrences before it was fixed once: a workflow's replaced steps, a survey's
/// replaced questions, a weight set's replaced weights, and a rep's unplanned call on both the HTTP
/// and the <c>/sync/push</c> path. Each was patched locally with a
/// <c>db.Set&lt;TChild&gt;().AddRange(parent.Children)</c>, and each patch had to be remembered by the
/// next person writing the same shape. Two of them were found in production paths as a 500.
/// </para>
/// <para>
/// It is asserted here, against a real PostgreSQL, rather than in whichever module hit it last —
/// because the fix is in <c>ModuleDbContext</c> and the sixth occurrence will be in a module nobody
/// has written yet.
/// </para>
/// </remarks>
public class ClientGeneratedKeyTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine")
        .Build();

    private static readonly DateTimeOffset FixedNow = new(2026, 8, 12, 9, 0, 0, TimeSpan.Zero);
    private readonly TenantId _tenant = TenantId.New();

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        await using var ctx = NewContext();
        await ctx.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync() => await _postgres.DisposeAsync();

    private TestDbContext NewContext()
    {
        var tenantContext = new FakeTenantContext(_tenant, "maria");
        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseNpgsql(_postgres.GetConnectionString())
            .AddInterceptors(
                new EntityStampingInterceptor(new FixedClock(FixedNow), tenantContext),
                new ClientGeneratedKeyGuard())
            .Options;
        return new TestDbContext(options, tenantContext);
    }

    private async Task<Guid> StoredWidgetAsync()
    {
        await using var ctx = NewContext();
        var widget = new Widget { Name = "Shelf strip" };
        ctx.Widgets.Add(widget);
        await ctx.SaveChangesAsync();
        return widget.Id;
    }

    [Fact]
    public async Task A_child_added_to_a_tracked_parent_is_inserted()
    {
        /*
         * The regression itself. Before the convention, EF read the part's client-set key as proof
         * the row existed, marked it `Modified`, and issued an UPDATE that matched nothing — which
         * Npgsql reports as a concurrency failure, because "zero rows affected" is indistinguishable
         * from "someone else got there first".
         *
         * Note what is *not* here: no `db.Set<WidgetPart>().Add(part)`. That line was the workaround,
         * and its absence is what this test is for.
         */
        var widgetId = await StoredWidgetAsync();

        await using (var ctx = NewContext())
        {
            var widget = await ctx.Widgets
                .Include(candidate => candidate.Parts)
                .SingleAsync(candidate => candidate.Id == widgetId);

            widget.AddPart("left bracket");

            await ctx.SaveChangesAsync();
        }

        await using var read = NewContext();
        var stored = await read.Widgets
            .Include(candidate => candidate.Parts)
            .SingleAsync(candidate => candidate.Id == widgetId);

        Assert.Equal("left bracket", Assert.Single(stored.Parts).Label);
    }

    [Fact]
    public async Task Replacing_a_tracked_parents_children_stores_the_new_set()
    {
        // The other half, and the shape three of the five occurrences had: the collection is cleared
        // and refilled, so every survivor is an insert and every departure is orphan removal. EF has
        // always handled the removals; it was the inserts it got wrong.
        var widgetId = await StoredWidgetAsync();

        await using (var first = NewContext())
        {
            var widget = await first.Widgets
                .Include(candidate => candidate.Parts)
                .SingleAsync(candidate => candidate.Id == widgetId);

            widget.AddPart("original");
            await first.SaveChangesAsync();
        }

        await using (var second = NewContext())
        {
            var widget = await second.Widgets
                .Include(candidate => candidate.Parts)
                .SingleAsync(candidate => candidate.Id == widgetId);

            widget.Parts.ToList().ForEach(part => second.Remove(part));
            widget.AddPart("replacement");

            await second.SaveChangesAsync();
        }

        await using var read = NewContext();
        var stored = await read.Widgets
            .Include(candidate => candidate.Parts)
            .SingleAsync(candidate => candidate.Id == widgetId);

        Assert.Equal("replacement", Assert.Single(stored.Parts).Label);
    }

    [Fact]
    public async Task A_row_that_never_named_itself_is_refused_rather_than_stored_as_zero()
    {
        /*
         * The counterpart to the convention. Withdrawing `ValueGenerated.OnAdd` also withdraws EF's
         * offer to invent a key, and the failure that leaves is the quiet kind: the first such row
         * stores an all-zero Guid and succeeds, and only the *second* trips the primary key — in
         * another request, blaming a row that did nothing wrong.
         *
         * So the guard makes it loud at the moment of the mistake, and names the property.
         */
        await using var ctx = NewContext();

        ctx.Widgets.Add(new Widget { Id = Guid.Empty, Name = "nameless" });

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => ctx.SaveChangesAsync());

        Assert.Contains("Widget.Id", thrown.Message);
    }
}
