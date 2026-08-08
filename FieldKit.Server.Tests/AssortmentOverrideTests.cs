using System.Net;
using System.Net.Http.Json;
using FieldKit.Modules.Outlets;
using FieldKit.Modules.Products;

namespace FieldKit.Server.Tests;

/// <summary>
/// One outlet's departures from its channel's assortment (<c>PRD-02</c>, <c>B2</c>) — W6 slice 4.
/// </summary>
/// <remarks>
/// The effective assortment is computed on read rather than stored, so these tests are mostly about
/// the merge: what the channel says, plus what the outlet adds, minus what it removes.
/// </remarks>
[Collection(ServerCollection.Name)]
public class AssortmentOverrideTests(ServerFixture fixture)
{
    private static string Unique(string label) => $"{label}-{Guid.NewGuid():N}"[..20];

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
            new CreateOutletRequest(Unique("OUT"), "Corner Shop", channelId, "Europe/Bucharest"));

        Assert.True(
            response.StatusCode == HttpStatusCode.Created,
            $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        return (await response.Content.ReadFromJsonAsync<OutletResponse>())!.Id;
    }

    private static async Task<Guid> ProductAsync(HttpClient writer, string sku)
    {
        var response = await writer.PostAsJsonAsync("/api/products", new CreateProductRequest(sku, sku));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ProductResponse>())!.Id;
    }

    private static async Task SetChannelAsync(
        HttpClient writer, Guid channelId, params AssortmentLineRequest[] lines)
    {
        var response = await writer.PutAsJsonAsync(
            $"/api/products/assortments/channels/{channelId}", new SetAssortmentRequest(lines));

        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }

    private static async Task<IReadOnlyList<OverrideResponse>> SetOverridesAsync(
        HttpClient writer, Guid outletId, params OverrideLineRequest[] lines)
    {
        var response = await writer.PutAsJsonAsync(
            $"/api/products/assortments/outlets/{outletId}/overrides", new SetOverridesRequest(lines));

        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        return (await response.Content.ReadFromJsonAsync<List<OverrideResponse>>())!;
    }

    private static async Task<IReadOnlyList<AssortmentItemResponse>> EffectiveAsync(
        HttpClient reader, Guid outletId) =>
        (await reader.GetFromJsonAsync<List<AssortmentItemResponse>>(
            $"/api/products/assortments/outlets/{outletId}"))!;

    [Fact]
    public async Task An_outlet_with_no_overrides_gets_its_channels_assortment_unchanged()
    {
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        using var writer = fixture.CreateAuthenticatedClient();

        var channelId = await ChannelAsync(admin);
        var outletId = await OutletAsync(admin, channelId);
        var productId = await ProductAsync(writer, Unique("SKU"));

        await SetChannelAsync(writer, channelId, new AssortmentLineRequest(productId, MustStock: true));

        var effective = Assert.Single(await EffectiveAsync(writer, outletId));
        Assert.Equal(productId, effective.ProductId);
        Assert.True(effective.MustStock);
    }

    [Fact]
    public async Task A_removed_product_disappears_from_that_outlet_only()
    {
        // The point of overrides: one shop refuses a line without the channel's list changing for
        // every other shop in it.
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        using var writer = fixture.CreateAuthenticatedClient();

        var channelId = await ChannelAsync(admin);
        var fussy = await OutletAsync(admin, channelId);
        var ordinary = await OutletAsync(admin, channelId);
        var productId = await ProductAsync(writer, Unique("SKU"));

        await SetChannelAsync(writer, channelId, new AssortmentLineRequest(productId));
        await SetOverridesAsync(
            writer, fussy, new OverrideLineRequest(productId, AssortmentOverrideKind.Removed));

        Assert.Empty(await EffectiveAsync(writer, fussy));
        Assert.Single(await EffectiveAsync(writer, ordinary));
    }

    [Fact]
    public async Task An_added_product_appears_for_that_outlet_only()
    {
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        using var writer = fixture.CreateAuthenticatedClient();

        var channelId = await ChannelAsync(admin);
        var special = await OutletAsync(admin, channelId);
        var ordinary = await OutletAsync(admin, channelId);
        var localLine = await ProductAsync(writer, Unique("SKU"));

        await SetChannelAsync(writer, channelId);
        await SetOverridesAsync(
            writer,
            special,
            new OverrideLineRequest(localLine, AssortmentOverrideKind.Added, MustStock: true));

        var added = Assert.Single(await EffectiveAsync(writer, special));
        Assert.Equal(localLine, added.ProductId);
        Assert.True(added.MustStock);

        Assert.Empty(await EffectiveAsync(writer, ordinary));
    }

    [Fact]
    public async Task Adding_a_product_the_channel_already_has_raises_it_to_must_stock()
    {
        // Not an error, and the reason an Added override wins rather than being refused as
        // redundant: it is how one shop treats as mandatory what its channel treats as optional.
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        using var writer = fixture.CreateAuthenticatedClient();

        var channelId = await ChannelAsync(admin);
        var outletId = await OutletAsync(admin, channelId);
        var productId = await ProductAsync(writer, Unique("SKU"));

        await SetChannelAsync(writer, channelId, new AssortmentLineRequest(productId, MustStock: false));
        await SetOverridesAsync(
            writer,
            outletId,
            new OverrideLineRequest(productId, AssortmentOverrideKind.Added, MustStock: true));

        var effective = Assert.Single(await EffectiveAsync(writer, outletId));
        Assert.Equal(productId, effective.ProductId);
        Assert.True(effective.MustStock);
    }

    [Fact]
    public async Task Removing_a_product_the_channel_does_not_have_is_inert_rather_than_an_error()
    {
        // What a shop's record looks like after the channel drops a line the shop had already
        // excluded. Refusing it would mean an override becoming invalid because something else
        // changed.
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        using var writer = fixture.CreateAuthenticatedClient();

        var channelId = await ChannelAsync(admin);
        var outletId = await OutletAsync(admin, channelId);
        var productId = await ProductAsync(writer, Unique("SKU"));

        await SetChannelAsync(writer, channelId);
        await SetOverridesAsync(
            writer, outletId, new OverrideLineRequest(productId, AssortmentOverrideKind.Removed));

        Assert.Empty(await EffectiveAsync(writer, outletId));
    }

    [Fact]
    public async Task A_channel_change_reaches_every_outlet_without_a_backfill()
    {
        // The consequence of computing the effective assortment rather than storing it. If it were
        // materialised per outlet, this would need a rewrite of every shop in the channel — and a
        // half-failed one would leave two shops disagreeing about the same channel.
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        using var writer = fixture.CreateAuthenticatedClient();

        var channelId = await ChannelAsync(admin);
        var outletId = await OutletAsync(admin, channelId);
        var first = await ProductAsync(writer, Unique("AAA"));
        var second = await ProductAsync(writer, Unique("BBB"));

        await SetChannelAsync(writer, channelId, new AssortmentLineRequest(first));
        Assert.Single(await EffectiveAsync(writer, outletId));

        await SetChannelAsync(
            writer, channelId, new AssortmentLineRequest(first), new AssortmentLineRequest(second));

        Assert.Equal(2, (await EffectiveAsync(writer, outletId)).Count);
    }

    [Fact]
    public async Task Overrides_are_replaced_rather_than_merged()
    {
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        using var writer = fixture.CreateAuthenticatedClient();

        var channelId = await ChannelAsync(admin);
        var outletId = await OutletAsync(admin, channelId);
        var first = await ProductAsync(writer, Unique("SKU"));
        var second = await ProductAsync(writer, Unique("SKU"));

        await SetOverridesAsync(
            writer, outletId, new OverrideLineRequest(first, AssortmentOverrideKind.Removed));

        var replaced = await SetOverridesAsync(
            writer, outletId, new OverrideLineRequest(second, AssortmentOverrideKind.Removed));

        Assert.Equal(second, Assert.Single(replaced).ProductId);
    }

    [Fact]
    public async Task The_same_product_added_and_removed_is_refused()
    {
        // A shop where one product is both added and removed has no answer, and every read would
        // have to pick one. The unique index would refuse it anyway; this makes the refusal
        // actionable.
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        using var writer = fixture.CreateAuthenticatedClient();

        var channelId = await ChannelAsync(admin);
        var outletId = await OutletAsync(admin, channelId);
        var productId = await ProductAsync(writer, Unique("SKU"));

        var response = await writer.PutAsJsonAsync(
            $"/api/products/assortments/outlets/{outletId}/overrides",
            new SetOverridesRequest(
            [
                new OverrideLineRequest(productId, AssortmentOverrideKind.Added),
                new OverrideLineRequest(productId, AssortmentOverrideKind.Removed),
            ]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = Assert.Single(await Refusals.ProblemsOf(response));
        Assert.Equal("product.assortment.duplicateOverride", problem.Code);
    }

    [Fact]
    public async Task An_override_cannot_name_a_product_that_does_not_exist()
    {
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        using var writer = fixture.CreateAuthenticatedClient();

        var channelId = await ChannelAsync(admin);
        var outletId = await OutletAsync(admin, channelId);

        var response = await writer.PutAsJsonAsync(
            $"/api/products/assortments/outlets/{outletId}/overrides",
            new SetOverridesRequest([new OverrideLineRequest(Guid.NewGuid(), AssortmentOverrideKind.Added)]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            "product.assortment.productMissing",
            Assert.Single(await Refusals.ProblemsOf(response)).Code);
    }

    [Fact]
    public async Task Overrides_cannot_be_set_for_an_outlet_that_does_not_exist()
    {
        using var writer = fixture.CreateAuthenticatedClient();

        var response = await writer.PutAsJsonAsync(
            $"/api/products/assortments/outlets/{Guid.NewGuid()}/overrides",
            new SetOverridesRequest([]));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task The_kind_crosses_the_wire_as_a_name_not_a_number()
    {
        // Same convention as ProductStatus and OutletStatus. Asserted on the raw body, because the
        // typed client deserializes both forms happily and would prove nothing.
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        using var writer = fixture.CreateAuthenticatedClient();

        var channelId = await ChannelAsync(admin);
        var outletId = await OutletAsync(admin, channelId);
        var productId = await ProductAsync(writer, Unique("SKU"));

        await SetOverridesAsync(
            writer, outletId, new OverrideLineRequest(productId, AssortmentOverrideKind.Removed));

        var body = await writer.GetStringAsync(
            $"/api/products/assortments/outlets/{outletId}/overrides");

        Assert.Contains("\"kind\":\"Removed\"", body);
        Assert.DoesNotContain("\"kind\":1", body);
    }

    [Fact]
    public async Task Another_tenants_outlet_has_no_overrides_to_read()
    {
        using var tenantA = fixture.CreateAuthenticatedClient();
        using var tenantB = fixture.CreateAuthenticatedClient(fixture.TenantBAccessToken);

        var channelOfB = await ChannelAsync(tenantB);
        var outletOfB = await OutletAsync(tenantB, channelOfB);

        var response = await tenantA.GetAsync(
            $"/api/products/assortments/outlets/{outletOfB}/overrides");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
