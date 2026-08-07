using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FieldKit.Infrastructure.Outbox;
using FieldKit.Modules.Outlets;
using FieldKit.Modules.Products;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FieldKit.Server.Tests;

/// <summary>
/// Where a price list applies, and the event that announces it (<c>PRD-03</c>) — W6 slice 6.
/// </summary>
[Collection(ServerCollection.Name)]
public class PriceListAssignmentTests(ServerFixture fixture)
{
    private const string Lists = "/api/products/price-lists";

    private static string Unique(string label) => $"{label}-{Guid.NewGuid():N}"[..20];

    private static DateOnly Today => new(2026, 1, 1);

    private static async Task<Guid> ChannelAsync(HttpClient admin)
    {
        var response = await admin.PostAsJsonAsync(
            "/api/outlets/channels", new ChannelRequest(Unique("Channel")));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ChannelResponse>())!.Id;
    }

    private static async Task<Guid> OutletAsync(HttpClient admin, Guid channelId)
    {
        var response = await admin.PostAsJsonAsync(
            "/api/outlets",
            new CreateOutletRequest(Unique("OUT"), "Corner Shop", channelId, null, null, "Europe/Bucharest"));

        Assert.True(
            response.StatusCode == HttpStatusCode.Created,
            $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        return (await response.Content.ReadFromJsonAsync<OutletResponse>())!.Id;
    }

    private static async Task<Guid> ListAsync(HttpClient writer)
    {
        var response = await writer.PostAsJsonAsync(
            Lists, new CreatePriceListRequest(Unique("List"), "EUR", Today, null));

        Assert.True(
            response.StatusCode == HttpStatusCode.Created,
            $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        return (await response.Content.ReadFromJsonAsync<PriceListResponse>())!.Id;
    }

    private static async Task<IReadOnlyList<AssignmentResponse>> AssignAsync(
        HttpClient writer, Guid listId, IReadOnlyList<Guid>? channels = null, IReadOnlyList<Guid>? outlets = null)
    {
        var response = await writer.PutAsJsonAsync(
            $"{Lists}/{listId}/assignments",
            new SetAssignmentsRequest(channels ?? [], outlets ?? []));

        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        return (await response.Content.ReadFromJsonAsync<List<AssignmentResponse>>())!;
    }

    /// <summary>The PriceListPublished events in the outbox for one list, oldest first.</summary>
    private async Task<IReadOnlyList<PriceListPublished>> PublishedAsync(Guid priceListId)
    {
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductsDbContext>();

        // Filtered by type in SQL and matched on the payload in memory: the content column is jsonb,
        // and a Contains against it translates to `jsonb ~~ jsonb`, which Postgres has no operator
        // for. Type holds the assembly-qualified name, so this matches on the name rather than
        // comparing to FullName.
        var payloads = await db.Set<OutboxMessage>()
            .Where(message => message.Type.Contains(nameof(PriceListPublished)))
            .OrderBy(message => message.OccurredOnUtc)
            .Select(message => message.Content)
            .ToListAsync();

        return
        [
            .. payloads
                .Select(json => JsonSerializer.Deserialize<PriceListPublished>(
                    json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
                .Where(published => published.PriceListId == priceListId),
        ];
    }

    [Fact]
    public async Task A_list_can_apply_to_a_channel_and_to_particular_outlets()
    {
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        using var writer = fixture.CreateAuthenticatedClient();

        var channelId = await ChannelAsync(admin);
        var outletId = await OutletAsync(admin, channelId);
        var listId = await ListAsync(writer);

        var assignments = await AssignAsync(writer, listId, [channelId], [outletId]);

        Assert.Equal(2, assignments.Count);
        Assert.Contains(assignments, a => a.ChannelId == channelId && a.OutletId is null);
        Assert.Contains(assignments, a => a.OutletId == outletId && a.ChannelId is null);
    }

    [Fact]
    public async Task Assigning_replaces_the_whole_scope()
    {
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        using var writer = fixture.CreateAuthenticatedClient();

        var first = await ChannelAsync(admin);
        var second = await ChannelAsync(admin);
        var listId = await ListAsync(writer);

        await AssignAsync(writer, listId, [first]);
        var replaced = await AssignAsync(writer, listId, [second]);

        Assert.Equal(second, Assert.Single(replaced).ChannelId);
    }

    [Fact]
    public async Task Assigning_the_same_scope_twice_does_not_duplicate_it()
    {
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        using var writer = fixture.CreateAuthenticatedClient();

        var channelId = await ChannelAsync(admin);
        var listId = await ListAsync(writer);

        await AssignAsync(writer, listId, [channelId]);
        Assert.Single(await AssignAsync(writer, listId, [channelId]));
    }

    [Fact]
    public async Task A_channel_that_does_not_exist_is_refused()
    {
        // Products cannot see the channel table (AT-1), so this goes through
        // IOutletClassification.ChannelExistsAsync. Without it the assignment would save cleanly and
        // price nobody.
        using var writer = fixture.CreateAuthenticatedClient();
        var listId = await ListAsync(writer);

        var response = await writer.PutAsJsonAsync(
            $"{Lists}/{listId}/assignments", new SetAssignmentsRequest([Guid.NewGuid()], []));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = Assert.Single(await Refusals.ProblemsOf(response));
        Assert.Equal("channelIds", problem.Field);
        Assert.Equal("product.priceList.channelMissing", problem.Code);
    }

    [Fact]
    public async Task An_outlet_that_does_not_exist_is_refused()
    {
        using var writer = fixture.CreateAuthenticatedClient();
        var listId = await ListAsync(writer);

        var response = await writer.PutAsJsonAsync(
            $"{Lists}/{listId}/assignments", new SetAssignmentsRequest([], [Guid.NewGuid()]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            "product.priceList.outletMissing",
            Assert.Single(await Refusals.ProblemsOf(response)).Code);
    }

    [Fact]
    public async Task Another_tenants_outlet_reads_as_missing_rather_than_forbidden()
    {
        // IOutletCatalog is tenant-filtered, so tenant B's outlet is simply absent from the result —
        // which surfaces here as "does not exist", the only answer that does not confirm it does.
        using var tenantBAdmin = fixture.CreateAuthenticatedClient(fixture.TenantBAccessToken);
        var channelOfB = await ChannelAsync(tenantBAdmin);
        var outletOfB = await OutletAsync(tenantBAdmin, channelOfB);

        using var writer = fixture.CreateAuthenticatedClient();
        var listId = await ListAsync(writer);

        var response = await writer.PutAsJsonAsync(
            $"{Lists}/{listId}/assignments", new SetAssignmentsRequest([], [outletOfB]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            "product.priceList.outletMissing",
            Assert.Single(await Refusals.ProblemsOf(response)).Code);
    }

    [Fact]
    public async Task The_database_refuses_a_row_scoped_to_both_or_to_neither()
    {
        // The check constraint, proven at the table. The endpoint can only ever build one-scoped
        // rows, so nothing reachable over HTTP exercises this — which is exactly why it is worth
        // asserting: it is the guard for whatever writes to the table without going through the
        // endpoint. Raw SQL because that is the only way to attempt the invalid row.
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductsDbContext>();

        foreach (var (channel, outlet) in new (object?, object?)[]
                 {
                     (Guid.NewGuid(), Guid.NewGuid()), // both
                     (null, null),                     // neither
                 })
        {
            var refused = await Record.ExceptionAsync(() => db.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO products.price_list_assignment
                    ("Id", "PriceListId", "channel_id", "outlet_id", "TenantId", "CreatedAtUtc")
                VALUES ({Guid.CreateVersion7()}, {Guid.NewGuid()}, {channel}, {outlet}, {Guid.NewGuid()}, now())
                """));

            Assert.NotNull(refused);
            Assert.Contains("ck_price_list_assignment_one_scope", refused.ToString());
        }
    }

    [Fact]
    public async Task Assigning_a_list_announces_it_through_the_outbox()
    {
        // The event is what Sync turns into a reference delta. Read from the outbox rather than
        // asserted at the call site, because the property that matters is that it was written in the
        // same transaction as the assignment rows (ADR-0006) — not that a method was called.
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        using var writer = fixture.CreateAuthenticatedClient();

        var channelId = await ChannelAsync(admin);
        var listId = await ListAsync(writer);

        await AssignAsync(writer, listId, [channelId]);

        var published = Assert.Single(await PublishedAsync(listId));
        Assert.Equal("EUR", published.Currency);
        Assert.Equal(Today, published.EffectiveFrom);
        Assert.Null(published.EffectiveTo);
        Assert.Equal(1, published.ChannelCount);
        Assert.Equal(0, published.OutletCount);
    }

    [Fact]
    public async Task Withdrawing_a_list_is_announced_too()
    {
        // "This list now reaches nobody" is a change a consumer needs as much as any other: it is how
        // a list is withdrawn, and a device that never hears it keeps pricing against a list that no
        // longer applies.
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        using var writer = fixture.CreateAuthenticatedClient();

        var channelId = await ChannelAsync(admin);
        var listId = await ListAsync(writer);

        await AssignAsync(writer, listId, [channelId]);
        await AssignAsync(writer, listId);

        var published = await PublishedAsync(listId);
        Assert.Equal(2, published.Count);
        Assert.Equal(0, published[^1].ChannelCount);
        Assert.Equal(0, published[^1].OutletCount);
    }

    [Fact]
    public async Task Assignments_cannot_be_read_or_set_for_a_list_that_does_not_exist()
    {
        using var writer = fixture.CreateAuthenticatedClient();

        Assert.Equal(
            HttpStatusCode.NotFound,
            (await writer.GetAsync($"{Lists}/{Guid.NewGuid()}/assignments")).StatusCode);

        var write = await writer.PutAsJsonAsync(
            $"{Lists}/{Guid.NewGuid()}/assignments", new SetAssignmentsRequest([], []));

        Assert.Equal(HttpStatusCode.NotFound, write.StatusCode);
    }

    [Fact]
    public async Task Reading_a_scope_and_setting_it_are_different_capabilities()
    {
        using var viewer = fixture.CreateAuthenticatedClient(fixture.ReadOnlyAccessToken);
        using var writer = fixture.CreateAuthenticatedClient();
        var listId = await ListAsync(writer);

        Assert.Equal(
            HttpStatusCode.OK, (await viewer.GetAsync($"{Lists}/{listId}/assignments")).StatusCode);

        var write = await viewer.PutAsJsonAsync(
            $"{Lists}/{listId}/assignments", new SetAssignmentsRequest([], []));

        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
    }
}
