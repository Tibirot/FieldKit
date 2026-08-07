using System.Net;
using System.Net.Http.Json;
using FieldKit.Modules.Products;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FieldKit.Server.Tests;

/// <summary>
/// Authoring promotions (<c>PRD-05</c>) — W6 slice 8.
/// </summary>
/// <remarks>
/// Percentage-off and fixed-amount-off only. Volume/tiered and BOGO are the second promotion PR.
/// Nothing here asserts that a discount is *applied* — selection by priority within the window is
/// <c>PRD-06</c>, and where a promotion reaches is the next slice.
/// </remarks>
[Collection(ServerCollection.Name)]
public class PromotionTests(ServerFixture fixture)
{
    private const string Promotions = "/api/products/promotions";

    private static string Unique(string label) => $"{label}-{Guid.NewGuid():N}"[..20];

    private static DateOnly Opens => new(2026, 1, 1);

    private static async Task<Guid> ProductAsync(HttpClient writer)
    {
        var response = await writer.PostAsJsonAsync(
            "/api/products", new { sku = Unique("SKU"), name = "Cola 500ml" });

        Assert.True(
            response.StatusCode == HttpStatusCode.Created,
            $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        return (await response.Content.ReadFromJsonAsync<ProductResponse>())!.Id;
    }

    private static async Task<Guid> CategoryAsync(HttpClient writer)
    {
        var response = await writer.PostAsJsonAsync(
            "/api/products/categories", new { name = Unique("Cat") });

        Assert.True(
            response.StatusCode == HttpStatusCode.Created,
            $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        return (await response.Content.ReadFromJsonAsync<CategoryResponse>())!.Id;
    }

    private static async Task<HttpResponseMessage> CreateAsync(
        HttpClient writer,
        PromotionType type = PromotionType.PercentOff,
        string value = "15",
        string? currency = null,
        DateOnly? from = null,
        DateOnly? to = null,
        int priority = 0,
        string? name = null) =>
        await writer.PostAsJsonAsync(
            Promotions,
            new CreatePromotionRequest(
                name ?? Unique("Promo"), type, value, from ?? Opens, to, priority, currency));

    private static async Task<PromotionResponse> PromotionAsync(
        HttpClient writer,
        PromotionType type = PromotionType.PercentOff,
        string value = "15",
        string? currency = null,
        DateOnly? from = null,
        DateOnly? to = null,
        int priority = 0)
    {
        var response = await CreateAsync(writer, type, value, currency, from, to, priority);

        Assert.True(
            response.StatusCode == HttpStatusCode.Created,
            $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        return (await response.Content.ReadFromJsonAsync<PromotionResponse>())!;
    }

    private static async Task<IReadOnlyList<PromotionTargetResponse>> SetTargetsAsync(
        HttpClient writer,
        Guid promotionId,
        IReadOnlyList<Guid>? products = null,
        IReadOnlyList<Guid>? categories = null)
    {
        var response = await writer.PutAsJsonAsync(
            $"{Promotions}/{promotionId}/targets",
            new SetPromotionTargetsRequest(products ?? [], categories ?? []));

        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        return (await response.Content.ReadFromJsonAsync<List<PromotionTargetResponse>>())!;
    }

    [Fact]
    public async Task A_percentage_promotion_keeps_its_percentage_and_has_no_currency()
    {
        using var writer = fixture.CreateAuthenticatedClient();

        var promotion = await PromotionAsync(writer, PromotionType.PercentOff, "12.5");

        Assert.Equal(PromotionType.PercentOff, promotion.Type);
        Assert.Equal("12.50", promotion.Value);
        Assert.Null(promotion.Currency);
    }

    [Fact]
    public async Task A_fixed_amount_promotion_carries_its_own_currency()
    {
        // Its own, not the price list's: a promotion is authored once and may reach outlets priced in
        // more than one currency. Refusing to discount a EUR line by an RON amount is BR-PRD-1
        // holding, and PRD-06 can only make that check because the currency is stored here.
        using var writer = fixture.CreateAuthenticatedClient();

        var promotion = await PromotionAsync(
            writer, PromotionType.FixedAmountOff, "2.50", currency: "eur");

        Assert.Equal(PromotionType.FixedAmountOff, promotion.Type);
        Assert.Equal("2.50", promotion.Value);
        Assert.Equal("EUR", promotion.Currency);
    }

    [Fact]
    public async Task The_type_and_value_cross_the_wire_by_name_and_as_a_string()
    {
        // The value is a string for the same reason money is (BR-PRD-8) — "12.5% off" losing its
        // last digit to a float is the same class of bug as a price doing so.
        using var writer = fixture.CreateAuthenticatedClient();
        var promotion = await PromotionAsync(writer, PromotionType.PercentOff, "12.5");

        var body = await (await writer.GetAsync($"{Promotions}/{promotion.Id}")).Content
            .ReadAsStringAsync();

        Assert.Contains("\"type\":\"PercentOff\"", body);
        Assert.Contains("\"value\":\"12.50\"", body);
    }

    [Theory]
    [InlineData(PromotionType.PercentOff, "12.5", null)]
    [InlineData(PromotionType.PercentOff, "100", null)]
    [InlineData(PromotionType.FixedAmountOff, "2.5", "EUR")]
    [InlineData(PromotionType.FixedAmountOff, "3", "EUR")]
    public async Task Creating_and_reading_spell_the_value_the_same_way(
        PromotionType type, string value, string? currency)
    {
        // The bug this pins: `decimal` keeps its scale, so "12.5" parsed at creation renders as
        // "12.5", while the same value read back from numeric(5,2) renders as "12.50". One promotion,
        // two spellings, differing only by which request you made — and a client diffing them sees a
        // change that never happened. Both paths now format the way MoneyJsonConverter does.
        using var writer = fixture.CreateAuthenticatedClient();

        var created = await PromotionAsync(writer, type, value, currency);
        var read = await writer.GetFromJsonAsync<PromotionResponse>($"{Promotions}/{created.Id}");

        Assert.Equal(created.Value, read!.Value);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("100.01")]
    public async Task A_percentage_outside_zero_to_a_hundred_is_refused(string value)
    {
        // Zero is refused with the rest: a 0% promotion is not a rule that does nothing, it is a rule
        // that will win a priority contest against a real discount and then take nothing off.
        using var writer = fixture.CreateAuthenticatedClient();

        var response = await CreateAsync(writer, PromotionType.PercentOff, value);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = Assert.Single(await Refusals.ProblemsOf(response));
        Assert.Equal("value", problem.Field);
        Assert.Equal("product.promotion.percentOutOfRange", problem.Code);
    }

    [Fact]
    public async Task A_hundred_percent_off_is_allowed()
    {
        // The boundary the rule above stops at. A free case is a real trade deal, not a typo.
        using var writer = fixture.CreateAuthenticatedClient();

        Assert.Equal("100.00", (await PromotionAsync(writer, PromotionType.PercentOff, "100")).Value);
    }

    [Fact]
    public async Task A_fixed_amount_of_zero_or_less_is_refused()
    {
        using var writer = fixture.CreateAuthenticatedClient();

        var response = await CreateAsync(writer, PromotionType.FixedAmountOff, "0", currency: "EUR");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            "product.promotion.amountNotPositive",
            Assert.Single(await Refusals.ProblemsOf(response)).Code);
    }

    [Fact]
    public async Task A_fixed_amount_larger_than_any_price_is_allowed()
    {
        // Deliberately not refused. Whether that floors a line at zero or disqualifies the promotion
        // is a resolution question (PRD-06), and it cannot be answered here — the same promotion
        // meets a different price at every outlet it reaches.
        using var writer = fixture.CreateAuthenticatedClient();

        var promotion = await PromotionAsync(
            writer, PromotionType.FixedAmountOff, "9999.00", currency: "EUR");

        Assert.Equal("9999.00", promotion.Value);
    }

    [Fact]
    public async Task A_comma_decimal_is_refused_rather_than_read_as_thousands()
    {
        // "12,50" would parse to 1250 if thousands separators were allowed — a hundredfold discount
        // that reads as a plausible one. Same refusal as a price line.
        using var writer = fixture.CreateAuthenticatedClient();

        var response = await CreateAsync(writer, PromotionType.PercentOff, "12,50");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            "product.promotion.valueNotANumber",
            Assert.Single(await Refusals.ProblemsOf(response)).Code);
    }

    [Fact]
    public async Task A_fixed_amount_without_a_currency_is_refused()
    {
        using var writer = fixture.CreateAuthenticatedClient();

        var response = await CreateAsync(writer, PromotionType.FixedAmountOff, "2.50");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = Assert.Single(await Refusals.ProblemsOf(response));
        Assert.Equal("currency", problem.Field);
        Assert.Equal("product.promotion.currencyInvalid", problem.Code);
    }

    [Fact]
    public async Task A_percentage_with_a_currency_is_refused_rather_than_ignored()
    {
        // Dropping it silently means the author finds out when a report disagrees with what they
        // thought they wrote.
        using var writer = fixture.CreateAuthenticatedClient();

        var response = await CreateAsync(writer, PromotionType.PercentOff, "15", currency: "EUR");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            "product.promotion.currencyNotApplicable",
            Assert.Single(await Refusals.ProblemsOf(response)).Code);
    }

    [Fact]
    public async Task A_window_that_ends_before_it_starts_is_refused()
    {
        using var writer = fixture.CreateAuthenticatedClient();

        // Equal dates too: half-open makes that an empty window — a promotion never live.
        var response = await CreateAsync(writer, from: Opens, to: Opens);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = Assert.Single(await Refusals.ProblemsOf(response));
        Assert.Equal("validTo", problem.Field);
        Assert.Equal("product.promotion.windowInverted", problem.Code);
    }

    [Fact]
    public async Task Two_promotions_cannot_share_a_name()
    {
        using var writer = fixture.CreateAuthenticatedClient();
        var name = Unique("Promo");

        Assert.Equal(HttpStatusCode.Created, (await CreateAsync(writer, name: name)).StatusCode);

        var again = await CreateAsync(writer, name: name);
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
        Assert.Equal(
            "product.promotion.nameTaken",
            Assert.Single(await Refusals.ProblemsOf(again)).Code);
    }

    [Fact]
    public async Task Everything_wrong_at_once_comes_back_at_once()
    {
        // A form with four bad fields should be fixable in one pass, not four round trips.
        using var writer = fixture.CreateAuthenticatedClient();

        var response = await writer.PostAsJsonAsync(
            Promotions,
            new CreatePromotionRequest(
                "  ", PromotionType.PercentOff, "150", Opens, Opens, 0, "EUR"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var codes = (await Refusals.ProblemsOf(response)).Select(p => p.Code).ToList();

        Assert.Contains("product.promotion.nameRequired", codes);
        Assert.Contains("product.promotion.percentOutOfRange", codes);
        Assert.Contains("product.promotion.currencyNotApplicable", codes);
        Assert.Contains("product.promotion.windowInverted", codes);
    }

    [Fact]
    public async Task A_promotion_can_be_revalued_redated_and_reprioritised()
    {
        using var writer = fixture.CreateAuthenticatedClient();
        var promotion = await PromotionAsync(writer, PromotionType.PercentOff, "10", priority: 5);

        var response = await writer.PutAsJsonAsync(
            $"{Promotions}/{promotion.Id}",
            new UpdatePromotionRequest(
                promotion.Name, "20", Opens, new DateOnly(2026, 4, 1), 50));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = (await response.Content.ReadFromJsonAsync<PromotionResponse>())!;

        Assert.Equal("20.00", updated.Value);
        Assert.Equal(new DateOnly(2026, 4, 1), updated.ValidTo);
        Assert.Equal(50, updated.Priority);
        Assert.Equal(PromotionType.PercentOff, updated.Type);
    }

    [Fact]
    public async Task Updating_cannot_change_the_type_or_the_currency()
    {
        // Neither is on UpdatePromotionRequest, so this asserts what a caller *cannot say* rather
        // than a refusal. Re-typing would reinterpret the value — 15 meaning "15% off" becoming 15
        // meaning "€15 off" — and every order already priced against it would be explained by a rule
        // that no longer exists.
        Assert.DoesNotContain(
            typeof(UpdatePromotionRequest).GetProperties(),
            property => property.Name is "Type" or "Currency");

        using var writer = fixture.CreateAuthenticatedClient();
        var promotion = await PromotionAsync(
            writer, PromotionType.FixedAmountOff, "2.50", currency: "EUR");

        var response = await writer.PutAsJsonAsync(
            $"{Promotions}/{promotion.Id}",
            new UpdatePromotionRequest(promotion.Name, "3.00", Opens));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = (await response.Content.ReadFromJsonAsync<PromotionResponse>())!;

        Assert.Equal(PromotionType.FixedAmountOff, updated.Type);
        Assert.Equal("EUR", updated.Currency);
        Assert.Equal("3.00", updated.Value);
    }

    [Fact]
    public async Task Promotions_are_listed_best_priority_first()
    {
        using var writer = fixture.CreateAuthenticatedClient();

        var low = await PromotionAsync(writer, priority: 1);
        var high = await PromotionAsync(writer, priority: 9_000);

        var listed = (await writer.GetFromJsonAsync<List<PromotionResponse>>(Promotions))!
            .Where(promotion => promotion.Id == low.Id || promotion.Id == high.Id)
            .ToList();

        // Higher wins (BR-PRD-3, as decided on Promotion.Priority) — a promotion that must beat
        // everything already authored is a bigger number, not a renumbering of everything else.
        Assert.Equal([high.Id, low.Id], listed.Select(promotion => promotion.Id));
    }

    [Fact]
    public async Task A_promotion_targets_products_and_categories_together()
    {
        using var writer = fixture.CreateAuthenticatedClient();

        var promotion = await PromotionAsync(writer);
        var productId = await ProductAsync(writer);
        var categoryId = await CategoryAsync(writer);

        var targets = await SetTargetsAsync(writer, promotion.Id, [productId], [categoryId]);

        Assert.Equal(2, targets.Count);
        Assert.Contains(targets, t => t.ProductId == productId && t.CategoryId is null);
        Assert.Contains(targets, t => t.CategoryId == categoryId && t.ProductId is null);
    }

    [Fact]
    public async Task Setting_targets_replaces_the_whole_set()
    {
        using var writer = fixture.CreateAuthenticatedClient();

        var promotion = await PromotionAsync(writer);
        var first = await ProductAsync(writer);
        var second = await ProductAsync(writer);

        await SetTargetsAsync(writer, promotion.Id, [first]);
        var replaced = await SetTargetsAsync(writer, promotion.Id, [second]);

        Assert.Equal(second, Assert.Single(replaced).ProductId);
    }

    [Fact]
    public async Task A_promotion_with_no_target_at_all_is_refused()
    {
        // Unlike a price list's scope, where emptying it is how a list is withdrawn. A promotion with
        // no target is not withdrawn — it is a discount with no subject, and the resolver would have
        // to guess whether that means everything or nothing. Withdrawing is what the window is for.
        using var writer = fixture.CreateAuthenticatedClient();
        var promotion = await PromotionAsync(writer);

        var response = await writer.PutAsJsonAsync(
            $"{Promotions}/{promotion.Id}/targets", new SetPromotionTargetsRequest([], []));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            "product.promotion.targetRequired",
            Assert.Single(await Refusals.ProblemsOf(response)).Code);
    }

    [Fact]
    public async Task A_product_or_category_that_does_not_exist_is_refused()
    {
        using var writer = fixture.CreateAuthenticatedClient();
        var promotion = await PromotionAsync(writer);

        var badProduct = await writer.PutAsJsonAsync(
            $"{Promotions}/{promotion.Id}/targets",
            new SetPromotionTargetsRequest([Guid.NewGuid()], []));

        Assert.Equal(HttpStatusCode.BadRequest, badProduct.StatusCode);
        Assert.Equal(
            "product.promotion.productMissing",
            Assert.Single(await Refusals.ProblemsOf(badProduct)).Code);

        var badCategory = await writer.PutAsJsonAsync(
            $"{Promotions}/{promotion.Id}/targets",
            new SetPromotionTargetsRequest([], [Guid.NewGuid()]));

        Assert.Equal(HttpStatusCode.BadRequest, badCategory.StatusCode);
        Assert.Equal(
            "product.promotion.categoryMissing",
            Assert.Single(await Refusals.ProblemsOf(badCategory)).Code);
    }

    [Fact]
    public async Task Another_tenants_product_reads_as_missing_rather_than_forbidden()
    {
        using var tenantB = fixture.CreateAuthenticatedClient(fixture.TenantBAccessToken);
        var productOfB = await ProductAsync(tenantB);

        using var writer = fixture.CreateAuthenticatedClient();
        var promotion = await PromotionAsync(writer);

        var response = await writer.PutAsJsonAsync(
            $"{Promotions}/{promotion.Id}/targets",
            new SetPromotionTargetsRequest([productOfB], []));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            "product.promotion.productMissing",
            Assert.Single(await Refusals.ProblemsOf(response)).Code);
    }

    [Fact]
    public async Task A_promotion_that_does_not_exist_is_not_found()
    {
        using var writer = fixture.CreateAuthenticatedClient();
        var absent = Guid.NewGuid();

        Assert.Equal(
            HttpStatusCode.NotFound, (await writer.GetAsync($"{Promotions}/{absent}")).StatusCode);

        Assert.Equal(
            HttpStatusCode.NotFound,
            (await writer.GetAsync($"{Promotions}/{absent}/targets")).StatusCode);

        var write = await writer.PutAsJsonAsync(
            $"{Promotions}/{absent}/targets", new SetPromotionTargetsRequest([], []));

        Assert.Equal(HttpStatusCode.NotFound, write.StatusCode);
    }

    [Fact]
    public async Task Reading_a_promotion_and_authoring_one_are_different_capabilities()
    {
        using var viewer = fixture.CreateAuthenticatedClient(fixture.ReadOnlyAccessToken);

        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync(Promotions)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await CreateAsync(viewer)).StatusCode);
    }

    [Fact]
    public async Task The_database_refuses_a_value_that_does_not_match_its_type()
    {
        // The check constraint, proven at the table. The endpoint can only ever build matching rows,
        // which is exactly why this is worth asserting: it is the guard for whatever writes without
        // going through the endpoint. Raw SQL because that is the only way to attempt the bad row.
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductsDbContext>();

        foreach (var (type, percent, amount, currency) in new (string, object?, object?, object?)[]
                 {
                     ("PercentOff", null, 5m, "EUR"),      // a percentage carrying money
                     ("PercentOff", 10m, null, "EUR"),     // a percentage with a currency
                     ("FixedAmountOff", null, 5m, null),   // money with no units
                     ("FixedAmountOff", 10m, null, "EUR"), // a fixed amount carrying a percentage
                 })
        {
            var refused = await Record.ExceptionAsync(() => db.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO products.promotion
                    ("Id", "Name", "type", "percent_off", "amount_off", "currency",
                     "ValidFrom", "ValidTo", "Priority", "TenantId", "CreatedAtUtc")
                VALUES ({Guid.CreateVersion7()}, {Guid.NewGuid().ToString()}, {type},
                        {percent}, {amount}, {currency},
                        {Opens}, NULL, 0, {Guid.NewGuid()}, now())
                """));

            Assert.NotNull(refused);
            Assert.Contains("ck_promotion_value_matches_type", refused.ToString());
        }
    }

    [Fact]
    public async Task The_database_refuses_a_target_of_both_or_of_neither()
    {
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductsDbContext>();

        foreach (var (product, category) in new (object?, object?)[]
                 {
                     (Guid.NewGuid(), Guid.NewGuid()), // both
                     (null, null),                     // neither
                 })
        {
            var refused = await Record.ExceptionAsync(() => db.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO products.promotion_target
                    ("Id", "PromotionId", "product_id", "category_id", "TenantId", "CreatedAtUtc")
                VALUES ({Guid.CreateVersion7()}, {Guid.NewGuid()}, {product}, {category},
                        {Guid.NewGuid()}, now())
                """));

            Assert.NotNull(refused);
            Assert.Contains("ck_promotion_target_one_subject", refused.ToString());
        }
    }
}
