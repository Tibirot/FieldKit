using System.Net;
using System.Net.Http.Json;
using FieldKit.Modules.Outlets;
using FieldKit.Modules.Products;

namespace FieldKit.Server.Tests;

/// <summary>
/// Channel assortments and the must-stock list (<c>PRD-02</c>) — W6 slice 3.
/// </summary>
/// <remarks>
/// These also stand in for <c>IOutletClassification</c>'s behaviour tests. That contract cannot be
/// exercised from a DI scope — tenant resolution is request-scoped by design (ADR-0008), so
/// resolving it outside a request throws before any query runs. Driving it through the endpoints
/// that consume it is the only way to observe it, and is closer to how it is actually used.
/// </remarks>
[Collection(ServerCollection.Name)]
public class AssortmentTests(ServerFixture fixture)
{
    private static string Unique(string label) => $"{label}-{Guid.NewGuid():N}"[..20];

    private static async Task<Guid> ChannelAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync(
            "/api/outlets/channels", new ChannelRequest(Unique("Channel")));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ChannelResponse>())!.Id;
    }

    private static async Task<Guid> OutletAsync(HttpClient client, Guid channelId)
    {
        var response = await client.PostAsJsonAsync(
            "/api/outlets",
            new CreateOutletRequest(Unique("OUT"), "Corner Shop", channelId, null, null, "Europe/Bucharest"));

        Assert.True(
            response.StatusCode == HttpStatusCode.Created,
            $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        return (await response.Content.ReadFromJsonAsync<OutletResponse>())!.Id;
    }

    private static async Task<Guid> ProductAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync(
            "/api/products", new CreateProductRequest(Unique("SKU"), "Veridian Still"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ProductResponse>())!.Id;
    }

    private static async Task<IReadOnlyList<AssortmentItemResponse>> SetAsync(
        HttpClient client, Guid channelId, params AssortmentLineRequest[] lines)
    {
        var response = await client.PutAsJsonAsync(
            $"/api/products/assortments/channels/{channelId}", new SetAssortmentRequest(lines));

        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        return (await response.Content.ReadFromJsonAsync<List<AssortmentItemResponse>>())!;
    }

    [Fact]
    public async Task A_channel_assortment_is_set_and_read_back_must_stock_first()
    {
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        using var writer = fixture.CreateAuthenticatedClient();
        var channelId = await ChannelAsync(client);
        var ordinary = await ProductAsync(writer);
        var mustStock = await ProductAsync(writer);

        var set = await SetAsync(
            writer,
            channelId,
            new AssortmentLineRequest(ordinary),
            new AssortmentLineRequest(mustStock, MustStock: true));

        // Must-stock first, so a suggested-order list does not have to sort it again.
        Assert.Equal(2, set.Count);
        Assert.Equal(mustStock, set[0].ProductId);
        Assert.True(set[0].MustStock);
        Assert.False(set[1].MustStock);
    }

    [Fact]
    public async Task Setting_an_assortment_replaces_it_rather_than_adding_to_it()
    {
        // A set has no obvious partial update: an absent product could mean "leave it" or "remove
        // it". Replace is the semantics every other PUT here uses, and the screen that edits this
        // renders the whole list.
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        using var writer = fixture.CreateAuthenticatedClient();
        var channelId = await ChannelAsync(client);
        var first = await ProductAsync(writer);
        var second = await ProductAsync(writer);

        await SetAsync(writer, channelId, new AssortmentLineRequest(first));
        var replaced = await SetAsync(writer, channelId, new AssortmentLineRequest(second));

        Assert.Equal(second, Assert.Single(replaced).ProductId);
    }

    [Fact]
    public async Task Setting_it_twice_is_idempotent_and_does_not_duplicate_rows()
    {
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        using var writer = fixture.CreateAuthenticatedClient();
        var channelId = await ChannelAsync(client);
        var productId = await ProductAsync(writer);

        await SetAsync(writer, channelId, new AssortmentLineRequest(productId));
        var again = await SetAsync(writer, channelId, new AssortmentLineRequest(productId));

        Assert.Single(again);
    }

    [Fact]
    public async Task A_must_stock_flag_can_be_toggled_without_removing_the_product()
    {
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        using var writer = fixture.CreateAuthenticatedClient();
        var channelId = await ChannelAsync(client);
        var productId = await ProductAsync(writer);

        await SetAsync(writer, channelId, new AssortmentLineRequest(productId, MustStock: true));
        var toggled = await SetAsync(writer, channelId, new AssortmentLineRequest(productId, MustStock: false));

        Assert.False(Assert.Single(toggled).MustStock);
    }

    [Fact]
    public async Task An_assortment_cannot_name_a_channel_that_does_not_exist()
    {
        // The reason Products consumes IOutletClassification.ChannelExistsAsync. Without it this
        // would save cleanly and simply never apply to anybody — an assortment for a channel no
        // outlet trades in.
        using var writer = fixture.CreateAuthenticatedClient();
        var productId = await ProductAsync(writer);

        var response = await writer.PutAsJsonAsync(
            $"/api/products/assortments/channels/{Guid.NewGuid()}",
            new SetAssortmentRequest([new AssortmentLineRequest(productId)]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = Assert.Single(await Refusals.ProblemsOf(response));
        Assert.Equal("channelId", problem.Field);
        Assert.Equal("product.assortment.channelMissing", problem.Code);
    }

    [Fact]
    public async Task An_assortment_cannot_name_a_product_that_does_not_exist()
    {
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        using var writer = fixture.CreateAuthenticatedClient();
        var channelId = await ChannelAsync(client);

        var response = await writer.PutAsJsonAsync(
            $"/api/products/assortments/channels/{channelId}",
            new SetAssortmentRequest([new AssortmentLineRequest(Guid.NewGuid())]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            "product.assortment.productMissing",
            Assert.Single(await Refusals.ProblemsOf(response)).Code);
    }

    [Fact]
    public async Task The_same_product_twice_is_refused_rather_than_deduplicated()
    {
        // Two lines disagreeing about must-stock is a request with no single meaning. Picking one
        // silently would make the answer depend on ordering.
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        using var writer = fixture.CreateAuthenticatedClient();
        var channelId = await ChannelAsync(client);
        var productId = await ProductAsync(writer);

        var response = await writer.PutAsJsonAsync(
            $"/api/products/assortments/channels/{channelId}",
            new SetAssortmentRequest(
                [new AssortmentLineRequest(productId, true), new AssortmentLineRequest(productId, false)]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = Assert.Single(await Refusals.ProblemsOf(response));
        Assert.Equal("product.assortment.duplicateProduct", problem.Code);
        Assert.Equal("1", problem.Args?["count"]);
    }

    [Fact]
    public async Task An_outlet_reads_the_assortment_of_the_channel_it_trades_in()
    {
        // The join Products cannot make alone, and the whole reason IOutletClassification exists:
        // Products knows which channel an assortment is for, only Outlets knows which channel this
        // shop is.
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        using var writer = fixture.CreateAuthenticatedClient();
        var channelId = await ChannelAsync(client);
        var outletId = await OutletAsync(client, channelId);
        var productId = await ProductAsync(writer);

        await SetAsync(writer, channelId, new AssortmentLineRequest(productId, MustStock: true));

        var forOutlet = await writer.GetFromJsonAsync<List<AssortmentItemResponse>>(
            $"/api/products/assortments/outlets/{outletId}");

        var item = Assert.Single(forOutlet!);
        Assert.Equal(productId, item.ProductId);
        Assert.True(item.MustStock);
    }

    [Fact]
    public async Task An_outlet_in_another_channel_sees_a_different_assortment()
    {
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        using var writer = fixture.CreateAuthenticatedClient();
        var stocked = await ChannelAsync(client);
        var bare = await ChannelAsync(client);
        var outletInBare = await OutletAsync(client, bare);

        await SetAsync(writer, stocked, new AssortmentLineRequest(await ProductAsync(writer)));

        var forOutlet = await writer.GetFromJsonAsync<List<AssortmentItemResponse>>(
            $"/api/products/assortments/outlets/{outletInBare}");

        Assert.Empty(forOutlet!);
    }

    [Fact]
    public async Task An_unknown_outlet_is_not_found_rather_than_empty()
    {
        // Two different answers: "no such shop" and "nothing is sold there". Collapsing them would
        // let a typo in an outlet id read as an empty assortment.
        using var writer = fixture.CreateAuthenticatedClient();

        var response = await writer.GetAsync($"/api/products/assortments/outlets/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Another_tenants_outlet_is_not_found_either()
    {
        // IOutletClassification is tenant-filtered like everything else, so tenant B's outlet is
        // simply absent — which is what turns into a 404 here rather than leaking that it exists.
        //
        // Tenant A reads as `rep`, not `admin`: Tenant Admin deliberately holds no product
        // permissions at all (SystemRoleTemplates — "administering who may sell is a different
        // capability from selling"), so reading as admin would be a 403 and would prove nothing
        // about isolation.
        using var tenantA = fixture.CreateAuthenticatedClient();
        using var tenantB = fixture.CreateAuthenticatedClient(fixture.TenantBAccessToken);

        var channelOfB = await ChannelAsync(tenantB);
        var outletOfB = await OutletAsync(tenantB, channelOfB);

        var response = await tenantA.GetAsync($"/api/products/assortments/outlets/{outletOfB}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Reading_an_assortment_and_authoring_it_are_different_capabilities()
    {
        using var viewer = fixture.CreateAuthenticatedClient(fixture.ReadOnlyAccessToken);
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        var channelId = await ChannelAsync(admin);

        Assert.Equal(
            HttpStatusCode.OK,
            (await viewer.GetAsync($"/api/products/assortments/channels/{channelId}")).StatusCode);

        var write = await viewer.PutAsJsonAsync(
            $"/api/products/assortments/channels/{channelId}", new SetAssortmentRequest([]));

        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
    }
}
