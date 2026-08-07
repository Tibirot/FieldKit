using System.Net;
using System.Net.Http.Json;
using FieldKit.Modules.Outlets;
using FieldKit.Modules.Products;

namespace FieldKit.Server.Tests;

/// <summary>
/// Resolving the promotion for one line over HTTP (<c>PRD-06</c>) — W6 slice 12.
/// </summary>
/// <remarks>
/// The selection rules are pinned by <see cref="PromotionResolutionVectorTests"/> against the shared
/// vector file, not here. What these cover is everything the vectors cannot reach: that the right
/// candidates are gathered — by reach, by target, and up the category tree — that tenants stay apart,
/// and that a line says what it is. Restating a selection case here would give it two homes and let
/// them drift.
/// </remarks>
[Collection(ServerCollection.Name)]
public class PromotionResolutionTests(ServerFixture fixture)
{
    private const string Promotions = "/api/products/promotions";

    private static string Unique(string label) => $"{label}-{Guid.NewGuid():N}"[..20];

    private static DateOnly Opens => new(2026, 6, 1);

    private static string Url(Guid outletId, Guid productId, int quantity, string on = "2026-06-15") =>
        $"/api/products/outlets/{outletId}/promotions?on={on}&productId={productId}&quantity={quantity}";

    private static async Task<Guid> ChannelAsync(HttpClient admin)
    {
        var response = await admin.PostAsJsonAsync(
            "/api/outlets/channels", new { name = Unique("Channel") });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ChannelResponse>())!.Id;
    }

    private static async Task<Guid> OutletAsync(HttpClient admin, Guid channelId)
    {
        var response = await admin.PostAsJsonAsync(
            "/api/outlets",
            new CreateOutletRequest(
                Unique("OUT"), "Corner Shop", channelId, null, null, "Europe/Bucharest"));

        Assert.True(
            response.StatusCode == HttpStatusCode.Created,
            $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        return (await response.Content.ReadFromJsonAsync<OutletResponse>())!.Id;
    }

    private static async Task<Guid> CategoryAsync(HttpClient writer, Guid? parentId = null)
    {
        var response = await writer.PostAsJsonAsync(
            "/api/products/categories", new { name = Unique("Cat"), parentId });

        Assert.True(
            response.StatusCode == HttpStatusCode.Created,
            $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        return (await response.Content.ReadFromJsonAsync<CategoryResponse>())!.Id;
    }

    private static async Task<Guid> ProductAsync(HttpClient writer, Guid? categoryId = null)
    {
        var response = await writer.PostAsJsonAsync(
            "/api/products", new { sku = Unique("SKU"), name = "Cola 500ml", categoryId });

        Assert.True(
            response.StatusCode == HttpStatusCode.Created,
            $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        return (await response.Content.ReadFromJsonAsync<ProductResponse>())!.Id;
    }

    /// <summary>Authors a promotion, points it at something, and gives it a scope.</summary>
    private static async Task<Guid> PromotionAsync(
        HttpClient writer,
        int priority = 0,
        Guid? productId = null,
        Guid? categoryId = null,
        Guid? channelId = null,
        Guid? outletId = null,
        string value = "15",
        DateOnly? from = null,
        DateOnly? to = null)
    {
        var created = await writer.PostAsJsonAsync(
            Promotions,
            new CreatePromotionRequest(
                Name: Unique("Promo"),
                Type: PromotionType.PercentOff,
                ValidFrom: from ?? Opens,
                Value: value,
                ValidTo: to,
                Priority: priority));

        Assert.True(
            created.StatusCode == HttpStatusCode.Created,
            $"{created.StatusCode}: {await created.Content.ReadAsStringAsync()}");

        var id = (await created.Content.ReadFromJsonAsync<PromotionResponse>())!.Id;

        var targeted = await writer.PutAsJsonAsync(
            $"{Promotions}/{id}/targets",
            new SetPromotionTargetsRequest(
                productId is { } p ? [p] : [], categoryId is { } c ? [c] : []));

        Assert.True(
            targeted.StatusCode == HttpStatusCode.OK,
            $"{targeted.StatusCode}: {await targeted.Content.ReadAsStringAsync()}");

        var assigned = await writer.PutAsJsonAsync(
            $"{Promotions}/{id}/assignments",
            new SetPromotionScopeRequest(
                channelId is { } ch ? [ch] : [], outletId is { } o ? [o] : []));

        Assert.True(
            assigned.StatusCode == HttpStatusCode.OK,
            $"{assigned.StatusCode}: {await assigned.Content.ReadAsStringAsync()}");

        return id;
    }

    private static async Task<ResolvedPromotionResponse?> ResolveAsync(
        HttpClient client, Guid outletId, Guid productId, int quantity = 1)
    {
        var response = await client.GetAsync(Url(outletId, productId, quantity));

        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        return (await response.Content.ReadFromJsonAsync<PromotionResolutionResponse>())!.Promotion;
    }

    [Fact]
    public async Task A_promotion_reaching_the_channel_applies_to_its_outlets()
    {
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        using var writer = fixture.CreateAuthenticatedClient();

        var channelId = await ChannelAsync(admin);
        var outletId = await OutletAsync(admin, channelId);
        var productId = await ProductAsync(writer);

        var promotionId = await PromotionAsync(
            writer, productId: productId, channelId: channelId, value: "15");

        var resolved = await ResolveAsync(writer, outletId, productId);

        Assert.NotNull(resolved);
        Assert.Equal(promotionId, resolved.PromotionId);
        Assert.Equal(PromotionType.PercentOff, resolved.Type);
        Assert.Equal("15.00", resolved.PercentOff);
        Assert.Null(resolved.Bundle);
    }

    [Fact]
    public async Task A_promotion_reaching_only_one_shop_applies_there()
    {
        // Both scopes have to reach the resolver. One that loaded only channel assignments would pass
        // every vector and still miss this.
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        using var writer = fixture.CreateAuthenticatedClient();

        var channelId = await ChannelAsync(admin);
        var outletId = await OutletAsync(admin, channelId);
        var elsewhere = await OutletAsync(admin, channelId);
        var productId = await ProductAsync(writer);

        var promotionId = await PromotionAsync(
            writer, productId: productId, outletId: outletId);

        Assert.Equal(promotionId, (await ResolveAsync(writer, outletId, productId))!.PromotionId);
        Assert.Null(await ResolveAsync(writer, elsewhere, productId));
    }

    [Fact]
    public async Task A_promotion_reaching_a_different_channel_does_not_apply()
    {
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        using var writer = fixture.CreateAuthenticatedClient();

        var channelId = await ChannelAsync(admin);
        var otherChannel = await ChannelAsync(admin);
        var outletId = await OutletAsync(admin, channelId);
        var productId = await ProductAsync(writer);

        await PromotionAsync(writer, productId: productId, channelId: otherChannel);

        Assert.Null(await ResolveAsync(writer, outletId, productId));
    }

    [Fact]
    public async Task A_promotion_on_a_category_covers_a_product_filed_beneath_it()
    {
        // The reason authoring stores the category rather than its members: matching walks up from
        // the product, so a deal on Beverages covers Beverages / Water / Still without anything
        // being expanded at authoring time.
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        using var writer = fixture.CreateAuthenticatedClient();

        var channelId = await ChannelAsync(admin);
        var outletId = await OutletAsync(admin, channelId);

        var beverages = await CategoryAsync(writer);
        var water = await CategoryAsync(writer, beverages);
        var still = await CategoryAsync(writer, water);
        var productId = await ProductAsync(writer, still);

        var promotionId = await PromotionAsync(
            writer, categoryId: beverages, channelId: channelId);

        Assert.Equal(promotionId, (await ResolveAsync(writer, outletId, productId))!.PromotionId);
    }

    [Fact]
    public async Task A_promotion_on_a_sibling_category_does_not_cover_the_product()
    {
        // Up, not sideways or down. A deal on Sparkling must not reach a Still product.
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        using var writer = fixture.CreateAuthenticatedClient();

        var channelId = await ChannelAsync(admin);
        var outletId = await OutletAsync(admin, channelId);

        var beverages = await CategoryAsync(writer);
        var still = await CategoryAsync(writer, beverages);
        var sparkling = await CategoryAsync(writer, beverages);
        var productId = await ProductAsync(writer, still);

        await PromotionAsync(writer, categoryId: sparkling, channelId: channelId);

        Assert.Null(await ResolveAsync(writer, outletId, productId));
    }

    [Fact]
    public async Task A_promotion_on_a_child_category_does_not_cover_a_product_filed_above_it()
    {
        // The other direction of the same rule. A deal on Still does not reach a product filed
        // directly under Beverages — it is not in Still.
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        using var writer = fixture.CreateAuthenticatedClient();

        var channelId = await ChannelAsync(admin);
        var outletId = await OutletAsync(admin, channelId);

        var beverages = await CategoryAsync(writer);
        var still = await CategoryAsync(writer, beverages);
        var productId = await ProductAsync(writer, beverages);

        await PromotionAsync(writer, categoryId: still, channelId: channelId);

        Assert.Null(await ResolveAsync(writer, outletId, productId));
    }

    [Fact]
    public async Task A_product_with_no_category_still_matches_a_product_target()
    {
        // Categories are optional on a product (PRD-01). The category walk returning nothing must not
        // stop a promotion that names the product outright.
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        using var writer = fixture.CreateAuthenticatedClient();

        var channelId = await ChannelAsync(admin);
        var outletId = await OutletAsync(admin, channelId);
        var productId = await ProductAsync(writer);

        var promotionId = await PromotionAsync(
            writer, productId: productId, channelId: channelId);

        Assert.Equal(promotionId, (await ResolveAsync(writer, outletId, productId))!.PromotionId);
    }

    [Fact]
    public async Task A_promotion_targeting_a_different_product_does_not_apply()
    {
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        using var writer = fixture.CreateAuthenticatedClient();

        var channelId = await ChannelAsync(admin);
        var outletId = await OutletAsync(admin, channelId);
        var productId = await ProductAsync(writer);
        var other = await ProductAsync(writer);

        await PromotionAsync(writer, productId: other, channelId: channelId);

        Assert.Null(await ResolveAsync(writer, outletId, productId));
    }

    [Fact]
    public async Task A_withdrawn_promotion_stops_applying()
    {
        // Emptying the scope is how a promotion is pulled — the symmetry settled in #110. This is
        // the end-to-end proof that resolution honours it.
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        using var writer = fixture.CreateAuthenticatedClient();

        var channelId = await ChannelAsync(admin);
        var outletId = await OutletAsync(admin, channelId);
        var productId = await ProductAsync(writer);

        var promotionId = await PromotionAsync(
            writer, productId: productId, channelId: channelId);

        Assert.NotNull(await ResolveAsync(writer, outletId, productId));

        await writer.PutAsJsonAsync(
            $"{Promotions}/{promotionId}/assignments", new SetPromotionScopeRequest([], []));

        Assert.Null(await ResolveAsync(writer, outletId, productId));
    }

    [Fact]
    public async Task The_highest_priority_of_several_in_scope_wins()
    {
        // The end-to-end shape of BR-PRD-3, once. The rule itself is pinned by the vectors; what this
        // proves is that several candidates actually reach the resolver together.
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        using var writer = fixture.CreateAuthenticatedClient();

        var channelId = await ChannelAsync(admin);
        var outletId = await OutletAsync(admin, channelId);
        var productId = await ProductAsync(writer);

        await PromotionAsync(
            writer, priority: 10, productId: productId, channelId: channelId, value: "40");

        var winner = await PromotionAsync(
            writer, priority: 100, productId: productId, outletId: outletId, value: "5");

        var resolved = await ResolveAsync(writer, outletId, productId);

        Assert.Equal(winner, resolved!.PromotionId);
        Assert.Equal("5.00", resolved.PercentOff);
    }

    [Fact]
    public async Task A_tiered_promotion_resolves_to_the_tier_the_quantity_reaches()
    {
        // End-to-end, because the tier rows have to be loaded and grouped correctly to reach the
        // resolver at all — the vectors assume they arrived.
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        using var writer = fixture.CreateAuthenticatedClient();

        var channelId = await ChannelAsync(admin);
        var outletId = await OutletAsync(admin, channelId);
        var productId = await ProductAsync(writer);

        var created = await writer.PostAsJsonAsync(
            Promotions,
            new CreatePromotionRequest(
                Name: Unique("Promo"), Type: PromotionType.VolumeTiered, ValidFrom: Opens));

        var promotionId = (await created.Content.ReadFromJsonAsync<PromotionResponse>())!.Id;

        await writer.PutAsJsonAsync(
            $"{Promotions}/{promotionId}/tiers",
            new SetPromotionTiersRequest(
            [
                new PromotionTierRequest(6, "2.5"),
                new PromotionTierRequest(24, "10"),
                new PromotionTierRequest(12, "5"),
            ]));

        await writer.PutAsJsonAsync(
            $"{Promotions}/{promotionId}/targets",
            new SetPromotionTargetsRequest([productId], []));

        await writer.PutAsJsonAsync(
            $"{Promotions}/{promotionId}/assignments",
            new SetPromotionScopeRequest([channelId], []));

        Assert.Equal("10.00", (await ResolveAsync(writer, outletId, productId, 30))!.PercentOff);
        Assert.Equal("5.00", (await ResolveAsync(writer, outletId, productId, 12))!.PercentOff);
        Assert.Equal("2.50", (await ResolveAsync(writer, outletId, productId, 6))!.PercentOff);

        // Below the lowest threshold the promotion does not apply at all.
        Assert.Null(await ResolveAsync(writer, outletId, productId, 3));
    }

    [Fact]
    public async Task A_bundle_crosses_the_wire_nested_and_only_once_earned()
    {
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        using var writer = fixture.CreateAuthenticatedClient();

        var channelId = await ChannelAsync(admin);
        var outletId = await OutletAsync(admin, channelId);
        var productId = await ProductAsync(writer);

        var created = await writer.PostAsJsonAsync(
            Promotions,
            new CreatePromotionRequest(
                Name: Unique("Promo"),
                Type: PromotionType.BuyXGetY,
                ValidFrom: Opens,
                Bundle: new BundleRequest(2, 1, "100")));

        var promotionId = (await created.Content.ReadFromJsonAsync<PromotionResponse>())!.Id;

        await writer.PutAsJsonAsync(
            $"{Promotions}/{promotionId}/targets",
            new SetPromotionTargetsRequest([productId], []));

        await writer.PutAsJsonAsync(
            $"{Promotions}/{promotionId}/assignments",
            new SetPromotionScopeRequest([channelId], []));

        Assert.Null(await ResolveAsync(writer, outletId, productId, 1));

        var resolved = await ResolveAsync(writer, outletId, productId, 2);
        Assert.Equal(PromotionType.BuyXGetY, resolved!.Type);
        Assert.Equal(2, resolved.Bundle!.BuyQuantity);
        Assert.Equal("100.00", resolved.Bundle.GetPercentOff);
        Assert.Null(resolved.PercentOff);

        var body = await (await writer.GetAsync(Url(outletId, productId, 2))).Content
            .ReadAsStringAsync();

        Assert.Contains("\"type\":\"BuyXGetY\"", body);
        Assert.Contains("\"bundle\":{", body);
    }

    [Fact]
    public async Task No_promotion_is_a_stated_answer_rather_than_an_empty_body()
    {
        // "This line has no promotion today" is an answer, not a missing resource — and not silence.
        //
        // Asserted on the raw body because the first draft returned the promotion directly and null
        // when there was none, which ASP.NET Core turns into an empty body: it short-circuits on a
        // null value and writes nothing, for Results.Ok and Results.Json alike. Every test that
        // expected "no promotion" failed on a JSON parse error rather than an assertion, which is
        // what pointed at it. This assertion is what stops it coming back.
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        using var writer = fixture.CreateAuthenticatedClient();

        var outletId = await OutletAsync(admin, await ChannelAsync(admin));
        var productId = await ProductAsync(writer);

        var response = await writer.GetAsync(Url(outletId, productId, 1));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("{\"promotion\":null}", (await response.Content.ReadAsStringAsync()).Trim());
    }

    [Fact]
    public async Task A_line_that_does_not_say_what_it_is_gets_refused()
    {
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        using var writer = fixture.CreateAuthenticatedClient();

        var outletId = await OutletAsync(admin, await ChannelAsync(admin));
        var productId = await ProductAsync(writer);
        var url = $"/api/products/outlets/{outletId}/promotions";

        var bare = await writer.GetAsync($"{url}?on=2026-06-15");
        Assert.Equal(HttpStatusCode.BadRequest, bare.StatusCode);
        var codes = (await Refusals.ProblemsOf(bare)).Select(problem => problem.Code).ToList();
        Assert.Contains("product.promotion.productRequired", codes);
        Assert.Contains("product.promotion.quantityRequired", codes);

        // Zero is refused rather than silently resolving to nothing: a line of zero reaches no tier
        // and earns no bundle, which reads as "this shop has no promotions" instead of as a mistake.
        var zero = await writer.GetAsync($"{url}?on=2026-06-15&productId={productId}&quantity=0");
        Assert.Equal(HttpStatusCode.BadRequest, zero.StatusCode);
        Assert.Equal(
            "product.promotion.quantityTooSmall",
            Assert.Single(await Refusals.ProblemsOf(zero)).Code);
    }

    [Fact]
    public async Task The_date_is_required_and_must_be_a_date()
    {
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        using var writer = fixture.CreateAuthenticatedClient();

        var outletId = await OutletAsync(admin, await ChannelAsync(admin));
        var productId = await ProductAsync(writer);
        var url = $"/api/products/outlets/{outletId}/promotions?productId={productId}&quantity=1";

        var missing = await writer.GetAsync(url);
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal(
            "product.promotion.dateRequired",
            Assert.Single(await Refusals.ProblemsOf(missing)).Code);

        var malformed = await writer.GetAsync($"{url}&on=15-06-2026");
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);

        var problem = Assert.Single(await Refusals.ProblemsOf(malformed));
        Assert.Equal("on", problem.Field);
        Assert.Equal("product.promotion.dateMalformed", problem.Code);
    }

    [Fact]
    public async Task An_outlet_that_does_not_exist_or_belongs_elsewhere_is_not_found()
    {
        using var tenantBAdmin = fixture.CreateAuthenticatedClient(fixture.TenantBAccessToken);
        var outletOfB = await OutletAsync(tenantBAdmin, await ChannelAsync(tenantBAdmin));

        using var writer = fixture.CreateAuthenticatedClient();
        var productId = await ProductAsync(writer);

        Assert.Equal(
            HttpStatusCode.NotFound,
            (await writer.GetAsync(Url(Guid.NewGuid(), productId, 1))).StatusCode);

        // Tenant B's outlet is absent from a tenant-filtered contract, which surfaces as 404 — the
        // only answer that does not confirm it exists elsewhere.
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await writer.GetAsync(Url(outletOfB, productId, 1))).StatusCode);
    }

    [Fact]
    public async Task Resolving_a_promotion_needs_only_read_permission()
    {
        // A rep prices an order; they do not author deals.
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        using var writer = fixture.CreateAuthenticatedClient();
        using var viewer = fixture.CreateAuthenticatedClient(fixture.ReadOnlyAccessToken);

        var outletId = await OutletAsync(admin, await ChannelAsync(admin));
        var productId = await ProductAsync(writer);

        Assert.Equal(
            HttpStatusCode.OK, (await viewer.GetAsync(Url(outletId, productId, 1))).StatusCode);
    }
}
