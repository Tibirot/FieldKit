using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using FieldKit.Modules.Order.Contracts;
using FieldKit.Modules.Outlets;
using FieldKit.Modules.Visit;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace FieldKit.Server.Tests;

/// <summary>
/// Applying an order a device captured offline (<c>ORD-07</c>, <c>OFF-04</c>) — W11 slice 1.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="OrderRecordTests"/> covers what a stored order must be true of. Asserted here is
/// everything the aggregate cannot see: that the visit exists and is this rep's, that a sealed visit
/// refuses a new order, that a replay is a success, and that the read endpoints answer with what was
/// stored.
/// </para>
/// <para>
/// <b>Why this calls the service rather than an endpoint.</b> There is no HTTP write path, on
/// purpose — an order is taken at a counter with no signal and arrives through <c>/sync/push</c>,
/// wired in W11 slice 5. So these resolve <see cref="IOrderIngest"/> from the running server's own
/// container, which means standing up a tenant context by hand.
/// </para>
/// <para>
/// <b>That harness is copied from <c>AuditIngestTests</c>, and this is the second copy.</b> Both
/// exist because <c>KeycloakTenantContext</c> throws without an authenticated request — deliberately,
/// so a tenant-owned query can never run unscoped — and reaching around it with a stub would test a
/// different tenant context from the one the server uses. A third module written only through
/// <c>/sync/push</c> is the point at which this should be extracted rather than copied again.
/// </para>
/// </remarks>
[Collection(ServerCollection.Name)]
public class OrderIngestTests(ServerFixture fixture)
{
    private const string Zone = "Europe/Bucharest";

    /// <summary>A shop on Calea Dorobanți, and a doorway to stand in.</summary>
    private static readonly Coordinates Shop = new(44.4638, 26.0946);

    private static string Unique(string label) => $"{label}-{Guid.NewGuid():N}"[..18];

    private HttpClient Admin() => fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

    private static CapturedOrder Captured(Guid visitId, params CapturedOrderLine[] lines) => new(
        Guid.CreateVersion7(),
        visitId,
        "EUR",
        lines.Sum(line => line.LineTotal),
        DateTimeOffset.Parse("2026-08-11T09:30:00Z"),
        lines.Length == 0 ? [Line()] : lines);

    private static CapturedOrderLine Line(decimal quantity = 6m, decimal lineTotal = 27.00m) =>
        new(Guid.CreateVersion7(), quantity, "case", 12, 4.50m, lineTotal);

    [Fact]
    public async Task An_order_is_stored_against_the_visits_outlet_and_read_back()
    {
        using var admin = Admin();

        var (visitId, outletId) = await VisitAsync(admin);
        var captured = Captured(visitId, Line());

        var result = await IngestAsync(captured);
        Assert.Equal(OrderIngestRefusal.None, result.Refusal);

        var response = await admin.GetAsync($"/api/orders/by-visit/{visitId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var order = (await response.Content.ReadFromJsonAsync<OrderReadback>())!;

        Assert.Equal(outletId, order.OutletId);
        Assert.Equal("EUR", order.CurrencyCode);

        // The name, not the ordinal — the response renders it rather than leaning on a converter.
        Assert.Equal("Submitted", order.Status);
        Assert.Equal(27.00m, order.Total);
        Assert.Equal("case", Assert.Single(order.Lines).UnitOfMeasure);
    }

    [Fact]
    public async Task Another_reps_visit_is_refused_the_same_way_a_missing_one_is()
    {
        // A device sends ids it read out of its own store. Distinguishing "no such visit" from "not
        // yours" would make this a way to discover whose visits exist — the same call Audit makes.
        using var admin = Admin();

        var (visitId, _) = await VisitAsync(admin);

        var stranger = await IngestAsync(Captured(visitId, Line()), userId: "someone-else");
        var missing = await IngestAsync(Captured(Guid.CreateVersion7(), Line()));

        Assert.Equal(OrderIngestRefusal.UnknownVisit, stranger.Refusal);
        Assert.Equal(OrderIngestRefusal.UnknownVisit, missing.Refusal);
        Assert.Equal(stranger.Message, missing.Message);
    }

    [Fact]
    public async Task A_repeat_of_the_same_order_succeeds_even_after_the_visit_is_sealed()
    {
        /*
         * The window that matters. Order and Sync commit separately, so a mutation can land and lose
         * its ledger entry; the device retries. If the replay check ran *after* the visit lookup, a
         * retry arriving once the rep had checked out would be refused forever — work that is done,
         * with no way back.
         */
        using var admin = Admin();

        var (visitId, _) = await VisitAsync(admin);
        var captured = Captured(visitId, Line());

        Assert.Equal(OrderIngestRefusal.None, (await IngestAsync(captured)).Refusal);

        var checkedOut = await admin.PostAsJsonAsync(
            $"/api/visits/{visitId}/check-out", new CheckOutRequest(VisitOutcome.Productive));
        Assert.Equal(HttpStatusCode.OK, checkedOut.StatusCode);

        Assert.Equal(OrderIngestRefusal.None, (await IngestAsync(captured)).Refusal);
    }

    [Fact]
    public async Task A_sealed_visit_refuses_a_new_order()
    {
        // The other half of the rule above: a *new* order attached to a filed visit would change a
        // record already counted. Only the replay is allowed through.
        using var admin = Admin();

        var (visitId, _) = await VisitAsync(admin);

        var checkedOut = await admin.PostAsJsonAsync(
            $"/api/visits/{visitId}/check-out", new CheckOutRequest(VisitOutcome.Productive));
        Assert.Equal(HttpStatusCode.OK, checkedOut.StatusCode);

        var result = await IngestAsync(Captured(visitId, Line()));

        Assert.Equal(OrderIngestRefusal.UnknownVisit, result.Refusal);
    }

    [Fact]
    public async Task An_order_the_aggregate_refuses_says_which_rule_it_broke()
    {
        using var admin = Admin();

        var (visitId, _) = await VisitAsync(admin);
        var duplicate = Line();

        var result = await IngestAsync(
            Captured(visitId) with { Lines = [duplicate, duplicate] });

        Assert.Equal(OrderIngestRefusal.Invalid, result.Refusal);
        Assert.Contains("more than one line", result.Message);
    }

    [Fact]
    public async Task An_outlets_orders_come_back_newest_captured_first()
    {
        /*
         * By when the rep *captured* them, not when this server heard. Two orders pushed in one
         * batch arrive milliseconds apart, so ordering by arrival would put a Thursday order taken
         * in a car park ahead of the Tuesday one it was queued behind.
         *
         * The later-captured order is stored *first* so that a sort by arrival would fail this.
         */
        using var admin = Admin();

        var (firstVisit, outletId) = await VisitAsync(admin);

        var later = Captured(firstVisit, Line()) with
        {
            CapturedAtUtc = DateTimeOffset.Parse("2026-08-11T16:00:00Z"),
        };

        Assert.Equal(OrderIngestRefusal.None, (await IngestAsync(later)).Refusal);

        var (secondVisit, _) = await VisitAsync(admin, outletId);

        var earlier = Captured(secondVisit, Line()) with
        {
            CapturedAtUtc = DateTimeOffset.Parse("2026-08-11T08:00:00Z"),
        };

        Assert.Equal(OrderIngestRefusal.None, (await IngestAsync(earlier)).Refusal);

        var orders = await admin.GetFromJsonAsync<List<OrderReadback>>(
            $"/api/orders/by-outlet/{outletId}");

        Assert.Equal([later.OrderId, earlier.OrderId], orders!.Select(order => order.Id));
    }

    [Fact]
    public async Task A_visit_with_no_order_is_a_404_rather_than_an_empty_one()
    {
        // "No order was taken" and "an order for nothing" are different facts, and an empty order is
        // one the aggregate refuses to store in the first place.
        using var admin = Admin();

        var (visitId, _) = await VisitAsync(admin);

        var response = await admin.GetAsync($"/api/orders/by-visit/{visitId}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private Task<OrderIngestResult> IngestAsync(CapturedOrder captured, string? userId = null) =>
        AsAsync(fixture.AdminAccessToken, services => services
            .GetRequiredService<IOrderIngest>()
            .IngestAsync(captured, userId ?? SubjectOf(fixture.AdminAccessToken)));

    private async Task<(Guid VisitId, Guid OutletId)> VisitAsync(
        HttpClient client, Guid? existingOutlet = null)
    {
        var outletId = existingOutlet ?? await OutletAsync(client);

        var response = await client.PostAsJsonAsync(
            "/api/visits/check-in", new CheckInRequest(outletId, Shop.Latitude, Shop.Longitude));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var visit = (await response.Content.ReadFromJsonAsync<VisitDetailResponse>())!.Visit;

        return (visit.Id, outletId);
    }

    private static async Task<Guid> OutletAsync(HttpClient client)
    {
        var channel = await client.PostAsJsonAsync(
            "/api/outlets/channels", new ChannelRequest(Unique("Channel")));

        var channelId = (await channel.Content.ReadFromJsonAsync<ChannelResponse>())!.Id;

        var created = await client.PostAsJsonAsync(
            "/api/outlets",
            new CreateOutletRequest(Unique("OUT"), "Corner Shop", channelId, Zone, Location: Shop));

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        return (await created.Content.ReadFromJsonAsync<OutletResponse>())!.Id;
    }

    /// <summary>Runs <paramref name="work"/> in a scope whose tenant context matches a real token.</summary>
    private async Task<T> AsAsync<T>(string token, Func<IServiceProvider, Task<T>> work)
    {
        using var scope = fixture.Services.CreateScope();

        var accessor = scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>();
        var previous = accessor.HttpContext;

        accessor.HttpContext = new DefaultHttpContext { User = PrincipalOf(token) };

        try
        {
            return await work(scope.ServiceProvider);
        }
        finally
        {
            accessor.HttpContext = previous;
        }
    }

    /// <summary>The claims inside a JWT, without validating it — the server already did that.</summary>
    private static ClaimsPrincipal PrincipalOf(string token)
    {
        var payload = token.Split('.')[1].Replace('-', '+').Replace('_', '/');
        var padded = payload.PadRight(payload.Length + ((4 - (payload.Length % 4)) % 4), '=');

        using var document = JsonDocument.Parse(Convert.FromBase64String(padded));

        var claims = new List<Claim>
        {
            new("tenant", document.RootElement.GetProperty("tenant").GetString()!),
            new("sub", document.RootElement.GetProperty("sub").GetString()!),
        };

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    private static string SubjectOf(string token) => PrincipalOf(token).FindFirst("sub")!.Value;

    private sealed record OrderLineReadback(string UnitOfMeasure, decimal Quantity, decimal LineTotal);

    private sealed record OrderReadback(
        Guid Id,
        Guid OutletId,
        string Status,
        string CurrencyCode,
        decimal Total,
        IReadOnlyList<OrderLineReadback> Lines);
}
