using FieldKit.Infrastructure;
using FieldKit.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace FieldKit.Infrastructure.Tests;

/// <summary>
/// The persistence base, verified on a real PostgreSQL (Testcontainers) — never in-memory
/// (testing strategy §3): schema-per-module, tenant stamping + auditing, and tenant query-filter
/// isolation all behave as they will in production.
/// </summary>
public class PersistenceIntegrationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine")
        .Build();

    private static readonly DateTimeOffset FixedNow = new(2026, 8, 2, 9, 0, 0, TimeSpan.Zero);
    private readonly TenantId _tenantA = TenantId.New();
    private readonly TenantId _tenantB = TenantId.New();

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        await using var ctx = NewContext(_tenantA);
        await ctx.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync() => await _postgres.DisposeAsync();

    private TestDbContext NewContext(TenantId tenant, string user = "user-a")
    {
        var tenantContext = new FakeTenantContext(tenant, user);
        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseNpgsql(_postgres.GetConnectionString())
            .AddInterceptors(new EntityStampingInterceptor(new FixedClock(FixedNow), tenantContext))
            .Options;
        return new TestDbContext(options, tenantContext);
    }

    [Fact]
    public async Task Saving_stamps_tenant_and_audit_fields()
    {
        Guid id;
        await using (var ctx = NewContext(_tenantA, "maria"))
        {
            var widget = new Widget { Name = "Shelf strip" };
            ctx.Widgets.Add(widget);
            await ctx.SaveChangesAsync();
            id = widget.Id;
        }

        await using var read = NewContext(_tenantA);
        var saved = await read.Widgets.SingleAsync(w => w.Id == id);

        Assert.Equal(_tenantA, saved.TenantId);
        Assert.Equal(FixedNow, saved.CreatedAtUtc);
        Assert.Equal("maria", saved.CreatedBy);
        Assert.Null(saved.ModifiedAtUtc);
    }

    [Fact]
    public async Task Tenant_query_filter_isolates_rows()
    {
        await using (var a = NewContext(_tenantA))
        {
            a.Widgets.Add(new Widget { Name = "A-only" });
            await a.SaveChangesAsync();
        }

        await using var tenantBReader = NewContext(_tenantB);
        Assert.Empty(await tenantBReader.Widgets.ToListAsync()); // B never sees A's rows

        await using var tenantAReader = NewContext(_tenantA);
        Assert.NotEmpty(await tenantAReader.Widgets.ToListAsync()); // A sees its own
    }

    [Fact]
    public async Task Table_lives_in_the_modules_own_schema()
    {
        await using var ctx = NewContext(_tenantA);

        var schema = await ctx.Database
            .SqlQuery<string>(
                $"""select table_schema AS "Value" from information_schema.tables where lower(table_name) = 'widgets'""")
            .SingleAsync();

        Assert.Equal("test", schema);
    }
}
