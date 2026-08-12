using System.Net;
using System.Net.Http.Json;
using FieldKit.Modules.Outlets;
using FieldKit.Modules.Products;

namespace FieldKit.Server.Tests;

/// <summary>
/// The smallest order a shop may place (<c>ORD-06</c>, <c>BR-ORD-5</c>) — W11 slice 8b-i.
/// </summary>
/// <remarks>
/// <para>
/// <b>Authoring and resolution, not enforcement.</b> The refusal a rep meets is 8b-ii's, on the
/// device — "must be met to submit" has to be answered at a counter with no signal. What this slice
/// gives <c>BR-ORD-5</c> is something to read.
/// </para>
/// <para>
/// <b>The scope came from the ledger, not from this slice.</b> <c>B1</c> says "optional minimum order
/// value per channel/outlet", which is the third rule in this module to take that shape. The tests
/// worth writing are therefore about the two things it does *not* share with the other two: that a
/// minimum carries a currency, and that a mismatched one is refused rather than compared.
/// </para>
/// </remarks>
[Collection(ServerCollection.Name)]
public class OrderMinimumTests(ServerFixture fixture)
{
    private static string Unique(string label) => $"{label}-{Guid.NewGuid():N}"[..18];

    /// <summary>Channels and outlets belong to Outlets, whose writes the `rep` fixture lacks.</summary>
    private HttpClient Admin() => fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

    private static async Task<Guid> ChannelAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync(
            "/api/outlets/channels", new ChannelRequest(Unique("Modern")));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<ChannelResponse>())!.Id;
    }

    private static async Task<Guid> OutletAsync(HttpClient client, Guid channelId)
    {
        var response = await client.PostAsJsonAsync(
            "/api/outlets",
            new CreateOutletRequest(Unique("OUT"), "Corner Shop", channelId, "Europe/Bucharest"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<OutletResponse>())!.Id;
    }

    private static async Task<HttpResponseMessage> SetAsync(
        HttpClient client, params OrderMinimumRequest[] minimums) =>
        await client.PutAsJsonAsync(
            "/api/products/order-minimums", new SetOrderMinimumsRequest(minimums));

    private static async Task<ResolvedOrderMinimumResponse?> ResolveAsync(
        HttpClient client, Guid outletId)
    {
        var response = await client.GetAsync($"/api/products/outlets/{outletId}/order-minimum");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<OrderMinimumResolutionResponse>())!.Minimum;
    }

    [Fact]
    public async Task An_outlet_with_nothing_configured_has_no_minimum_rather_than_one_of_zero()
    {
        /*
         * The ordinary case, and the one `BR-ORD-5`'s "if configured" is about. Most tenants will
         * never set a minimum, so *absent* has to be a first-class answer — and it has to mean
         * "every order passes" rather than "no order does", which is what a zero would read as to
         * anybody skimming the response.
         */
        using var client = fixture.CreateAuthenticatedClient();
        using var admin = Admin();

        var channelId = await ChannelAsync(admin);
        var outletId = await OutletAsync(admin, channelId);

        Assert.Null(await ResolveAsync(client, outletId));
    }

    [Fact]
    public async Task A_channel_minimum_applies_to_every_outlet_in_it()
    {
        using var client = fixture.CreateAuthenticatedClient();
        using var admin = Admin();

        var channelId = await ChannelAsync(admin);
        var outletId = await OutletAsync(admin, channelId);

        Assert.Equal(
            HttpStatusCode.OK,
            (await SetAsync(client, new OrderMinimumRequest(channelId, null, "150.00", "RON"))).StatusCode);

        var resolved = await ResolveAsync(client, outletId);

        Assert.Equal("150.00", resolved!.Amount);
        Assert.Equal("RON", resolved.CurrencyCode);
        Assert.Equal("Channel", resolved.Scope);
    }

    [Fact]
    public async Task An_outlet_minimum_beats_its_channels()
    {
        /*
         * `B1`'s precedence, and the same one `BR-PRD-2` gives a price list. Asserted with a *lower*
         * outlet figure than the channel's so the answer cannot come from picking the larger number
         * — which is a rule nobody wrote and one a careless implementation might land on.
         */
        using var client = fixture.CreateAuthenticatedClient();
        using var admin = Admin();

        var channelId = await ChannelAsync(admin);
        var outletId = await OutletAsync(admin, channelId);

        await SetAsync(
            client,
            new OrderMinimumRequest(channelId, null, "150.00", "RON"),
            new OrderMinimumRequest(null, outletId, "50.00", "RON"));

        var resolved = await ResolveAsync(client, outletId);

        Assert.Equal("50.00", resolved!.Amount);
        Assert.Equal("Outlet", resolved.Scope);
    }

    [Fact]
    public async Task Withdrawing_every_minimum_is_an_empty_set_rather_than_a_deletion()
    {
        // The same shape a promotion's targets and a class's rates use. A tenant that stops applying
        // a minimum says so by replacing the set with nothing, and every order is submittable again.
        using var client = fixture.CreateAuthenticatedClient();
        using var admin = Admin();

        var channelId = await ChannelAsync(admin);
        var outletId = await OutletAsync(admin, channelId);

        await SetAsync(client, new OrderMinimumRequest(channelId, null, "150.00", "RON"));

        Assert.NotNull(await ResolveAsync(client, outletId));

        Assert.Equal(HttpStatusCode.OK, (await SetAsync(client)).StatusCode);

        Assert.Null(await ResolveAsync(client, outletId));
    }

    [Fact]
    public async Task A_minimum_carries_its_currency_so_it_can_be_compared_to_an_order()
    {
        /*
         * The one thing this rule has that a price list assignment does not, and the reason it is
         * stored rather than assumed: an order's currency comes from the list that priced it
         * (`BR-ORD-7`), and comparing 50 EUR to 200 RON by their numbers alone would refuse orders
         * comfortably over the threshold while looking like the rule working.
         */
        using var client = fixture.CreateAuthenticatedClient();
        using var admin = Admin();

        var channelId = await ChannelAsync(admin);
        var outletId = await OutletAsync(admin, channelId);

        await SetAsync(client, new OrderMinimumRequest(channelId, null, "40.00", "eur"));

        var resolved = await ResolveAsync(client, outletId);

        // Upper-cased on the way in, like every other currency in this module.
        Assert.Equal("EUR", resolved!.CurrencyCode);
    }

    [Theory]
    [InlineData("0", "product.orderMinimum.amountNotPositive")]
    [InlineData("-10.00", "product.orderMinimum.amountNotPositive")]
    [InlineData("1,500", "product.orderMinimum.amountNotANumber")]
    [InlineData("lots", "product.orderMinimum.amountNotANumber")]
    public async Task An_amount_that_is_not_a_minimum_is_refused_by_name(string amount, string code)
    {
        /*
         * Zero is refused rather than stored, which is the opposite call to a tax rate's — and
         * deliberately. A 0.00 tax means zero-rated goods, a real commercial fact; a minimum of zero
         * means *no minimum*, which is already expressible by not having a row. Two ways to say one
         * thing is how a screen ends up showing "minimum: 0.00" at a rep who then wonders what it is
         * for.
         *
         * "1,500" is here because `NumberStyles.Number` would read it as fifteen hundred under
         * invariant culture — a tenant who meant one and a half getting a threshold ten times too
         * high, stored without complaint.
         */
        using var client = fixture.CreateAuthenticatedClient();
        using var admin = Admin();

        var channelId = await ChannelAsync(admin);

        var response = await SetAsync(client, new OrderMinimumRequest(channelId, null, amount, "RON"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        Assert.Contains(await Refusals.ProblemsOf(response), error => error.Code == code);
    }

    [Fact]
    public async Task A_minimum_that_applies_to_both_scopes_or_neither_is_refused_by_name()
    {
        // Refused here as well as by the check constraint: a constraint violation surfaces as a 500
        // and names a database object at an author who typed into a form.
        using var client = fixture.CreateAuthenticatedClient();
        using var admin = Admin();

        var channelId = await ChannelAsync(admin);
        var outletId = await OutletAsync(admin, channelId);

        var both = await SetAsync(client, new OrderMinimumRequest(channelId, outletId, "10.00", "RON"));
        var neither = await SetAsync(client, new OrderMinimumRequest(null, null, "10.00", "RON"));

        Assert.Equal(HttpStatusCode.BadRequest, both.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, neither.StatusCode);

        Assert.Contains(
            await Refusals.ProblemsOf(both),
            error => error.Code == "product.orderMinimum.oneScope");
    }

    [Fact]
    public async Task A_scope_this_tenant_does_not_have_is_refused_rather_than_stored()
    {
        /*
         * Neither id has a foreign key — both point into Outlets, and a constraint across a module
         * boundary is what schema-per-module exists to prevent (ADR-0005). So the check is a question
         * asked through the contracts, and without it a minimum saves against a channel nobody has
         * and silently applies to nothing: the rule reads as switched off rather than as a typo.
         */
        using var client = fixture.CreateAuthenticatedClient();

        var channel = await SetAsync(
            client, new OrderMinimumRequest(Guid.CreateVersion7(), null, "10.00", "RON"));

        var outlet = await SetAsync(
            client, new OrderMinimumRequest(null, Guid.CreateVersion7(), "10.00", "RON"));

        Assert.Equal(HttpStatusCode.BadRequest, channel.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, outlet.StatusCode);
    }

    [Fact]
    public async Task One_scope_cannot_be_given_two_minimums()
    {
        // The unique index would refuse it anyway; saying so here names the field and the count
        // rather than surfacing a constraint violation.
        using var client = fixture.CreateAuthenticatedClient();
        using var admin = Admin();

        var channelId = await ChannelAsync(admin);

        var response = await SetAsync(
            client,
            new OrderMinimumRequest(channelId, null, "10.00", "RON"),
            new OrderMinimumRequest(channelId, null, "20.00", "RON"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        Assert.Contains(
            await Refusals.ProblemsOf(response),
            error => error.Code == "product.orderMinimum.scopeDuplicated");
    }

    [Fact]
    public async Task Another_tenants_minimums_are_invisible()
    {
        // Through the DbContext's filter, as everywhere — this endpoint writes no tenant predicate.
        using var client = fixture.CreateAuthenticatedClient();
        using var admin = Admin();
        using var other = fixture.CreateAuthenticatedClient(fixture.TenantBAccessToken);

        var channelId = await ChannelAsync(admin);
        var outletId = await OutletAsync(admin, channelId);

        await SetAsync(client, new OrderMinimumRequest(channelId, null, "150.00", "RON"));

        var theirs = await other.GetAsync("/api/products/order-minimums");

        Assert.Equal(HttpStatusCode.OK, theirs.StatusCode);

        Assert.DoesNotContain(
            (await theirs.Content.ReadFromJsonAsync<List<OrderMinimumResponse>>())!,
            row => row.ChannelId == channelId);

        // …and the resolution endpoint agrees, rather than the list being the only thing scoped.
        Assert.NotNull(await ResolveAsync(client, outletId));
    }

    [Fact]
    public async Task Setting_a_minimum_needs_the_write_permission()
    {
        using var readOnly = fixture.CreateAuthenticatedClient(fixture.ReadOnlyAccessToken);

        var response = await SetAsync(readOnly);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
