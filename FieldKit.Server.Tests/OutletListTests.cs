using System.Net.Http.Json;
using FieldKit.Modules.Outlets;
using FieldKit.Web;

namespace FieldKit.Server.Tests;

/// <summary>
/// Paging, search and sort over the outlet base (<c>OUT-01</c>).
/// </summary>
/// <remarks>
/// Every test here scopes itself to outlets it created, by searching for a prefix nothing else uses.
/// The fixture's database is shared and accumulates rows from every other suite — assertions about
/// "the first page" would otherwise be assertions about what ran before them.
/// </remarks>
[Collection(ServerCollection.Name)]
public class OutletListTests(ServerFixture fixture)
{
    private const string Zone = "Europe/Bucharest";

    private static string Unique(string label) => $"{label}-{Guid.NewGuid():N}"[..18];

    private async Task<Guid> ChannelAsync(HttpClient client, string? name = null)
    {
        var response = await client.PostAsJsonAsync(
            "/api/outlets/channels", new ChannelRequest(name ?? Unique("Channel")));

        return (await response.Content.ReadFromJsonAsync<ChannelResponse>())!.Id;
    }

    private static async Task<PagedList<OutletResponse>> ListAsync(HttpClient client, string query) =>
        (await client.GetFromJsonAsync<PagedList<OutletResponse>>($"/api/outlets?{query}"))!;

    /// <summary>Creates <paramref name="count"/> outlets whose codes share a searchable prefix.</summary>
    private async Task<(string Prefix, Guid ChannelId)> SeedAsync(
        HttpClient client, int count, OutletStatus status = OutletStatus.Active)
    {
        // NewGuid, not CreateVersion7. A v7 GUID leads with a timestamp, so truncating one to ten
        // characters hands two tests seeded in the same millisecond range the *same* prefix — and
        // each then searches up the other's outlets. Found by these tests passing alone and failing
        // together, which is the only way that class of bug ever shows itself.
        var prefix = $"P{Guid.NewGuid():N}"[..10].ToUpperInvariant();
        var channelId = await ChannelAsync(client);

        for (var index = 0; index < count; index++)
        {
            var created = await client.PostAsJsonAsync(
                "/api/outlets",
                new CreateOutletRequest(
                    $"{prefix}-{index:D3}", $"Shop {index:D3}", channelId, Zone));

            if (status != OutletStatus.Active)
            {
                var outlet = (await created.Content.ReadFromJsonAsync<OutletResponse>())!;

                await client.PostAsJsonAsync(
                    $"/api/outlets/{outlet.Id}/status",
                    new OutletStatusRequest(status, "seeded for a filter test"));
            }
        }

        return (prefix, channelId);
    }

    [Fact]
    public async Task A_page_carries_the_total_so_a_pager_can_be_drawn()
    {
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        var (prefix, _) = await SeedAsync(client, 7);

        var page = await ListAsync(client, $"search={prefix}&pageSize=3");

        Assert.Equal(3, page.Items.Count);
        Assert.Equal(7, page.Total);
        Assert.Equal(1, page.Page);
        Assert.Equal(3, page.PageSize);
    }

    [Fact]
    public async Task Every_row_appears_on_exactly_one_page()
    {
        // The assertion offset paging lives or dies on. Rows with equal sort keys have no defined
        // order in SQL, so without a unique tiebreak Postgres may return them differently between
        // the query for page 1 and the query for page 2 — and an outlet then shows up on both while
        // another shows up on neither. Sorting by status, where every seeded row shares one value,
        // is the case that exposes it.
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        var (prefix, _) = await SeedAsync(client, 9);

        var seen = new List<string>();

        for (var page = 1; page <= 3; page++)
        {
            var slice = await ListAsync(client, $"search={prefix}&sort=Status&page={page}&pageSize=3");
            seen.AddRange(slice.Items.Select(outlet => outlet.Code));
        }

        Assert.Equal(9, seen.Count);
        Assert.Equal(9, seen.Distinct().Count());
    }

    [Fact]
    public async Task Search_matches_a_code_or_a_name_and_ignores_case()
    {
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        var channelId = await ChannelAsync(client);
        var code = Unique("SRCH");

        await client.PostAsJsonAsync(
            "/api/outlets",
            new CreateOutletRequest(code, "Alimentara Voievozilor", channelId, Zone));

        Assert.Single((await ListAsync(client, $"search={code.ToLowerInvariant()}")).Items);
        Assert.Contains(
            (await ListAsync(client, "search=voievozilor")).Items,
            outlet => outlet.Code == code);
    }

    [Fact]
    public async Task A_search_for_a_wildcard_searches_for_that_character()
    {
        // Unescaped, `%` is a LIKE wildcard: a search for it would match the entire table while
        // looking like a search that simply found a lot. The failure is invisible in a demo and
        // wrong in every tenant.
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        var channelId = await ChannelAsync(client);
        var code = Unique("PCT");

        await client.PostAsJsonAsync(
            "/api/outlets",
            new CreateOutletRequest(code, "50% Off Store", channelId, Zone));

        var literal = await ListAsync(client, "search=50%25");

        Assert.Contains(literal.Items, outlet => outlet.Code == code);
        Assert.All(literal.Items, outlet => Assert.Contains("50%", outlet.Name));
    }

    [Fact]
    public async Task Sorting_orders_the_whole_list_rather_than_the_page()
    {
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        var (prefix, _) = await SeedAsync(client, 5);

        var ascending = await ListAsync(client, $"search={prefix}&sort=Name&pageSize=2");
        var descending = await ListAsync(client, $"search={prefix}&sort=Name&descending=true&pageSize=2");

        // The first page descending must hold the *last* names overall, not the first page reversed.
        // `Project` used to impose `OrderBy(Code)` on the source, which silently overrode whatever
        // the caller asked for — a sort that appeared to work because Code and Name happened to
        // agree in every earlier test.
        Assert.Equal(["Shop 000", "Shop 001"], ascending.Items.Select(outlet => outlet.Name));
        Assert.Equal(["Shop 004", "Shop 003"], descending.Items.Select(outlet => outlet.Name));
    }

    [Fact]
    public async Task Filters_and_search_narrow_the_total_together()
    {
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        var (prefix, channelId) = await SeedAsync(client, 4);
        var otherChannel = await ChannelAsync(client);

        await client.PostAsJsonAsync(
            "/api/outlets",
            new CreateOutletRequest($"{prefix}-900", "Elsewhere", otherChannel, Zone));

        var all = await ListAsync(client, $"search={prefix}");
        var narrowed = await ListAsync(client, $"search={prefix}&channelId={channelId}");

        Assert.Equal(5, all.Total);

        // The total tracks the filter. A total counted before filtering would say 5 while showing 4,
        // and the pager would offer a page that is always empty.
        Assert.Equal(4, narrowed.Total);
        Assert.Equal(4, narrowed.Items.Count);
    }

    [Fact]
    public async Task A_page_size_nobody_should_ask_for_is_clamped_rather_than_served()
    {
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var huge = await ListAsync(client, "pageSize=100000");
        Assert.Equal(Paging.MaxSize, huge.PageSize);

        // Clamped, not refused: the response says which size was used, so a caller can see what
        // happened without parsing an error. Nonsense page numbers land on the first page for the
        // same reason — nobody types `?page=-2` on purpose.
        var negative = await ListAsync(client, "page=-2");
        Assert.Equal(1, negative.Page);
    }

    [Fact]
    public async Task A_page_past_the_end_is_empty_rather_than_an_error()
    {
        // What a pager does when someone deletes rows while a tab is open on page 40.
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        var (prefix, _) = await SeedAsync(client, 2);

        var beyond = await ListAsync(client, $"search={prefix}&page=50&pageSize=10");

        Assert.Empty(beyond.Items);
        Assert.Equal(2, beyond.Total);
    }

    [Fact]
    public async Task Status_filters_and_search_survive_a_lifecycle_change()
    {
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        var (prefix, _) = await SeedAsync(client, 3, OutletStatus.Inactive);

        Assert.Equal(3, (await ListAsync(client, $"search={prefix}&status=Inactive")).Total);
        Assert.Equal(0, (await ListAsync(client, $"search={prefix}&status=Active")).Total);
    }
}
