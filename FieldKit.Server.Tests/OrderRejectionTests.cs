using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using FieldKit.Modules.Order;
using FieldKit.Modules.Order.Contracts;
using FieldKit.Modules.Outlets;
using FieldKit.Modules.Visit;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace FieldKit.Server.Tests;

/// <summary>
/// Refusing an order back to the rep, and the correction that follows (<c>ORD-12</c>,
/// <c>BR-ORD-9</c>) — W11 slice 4a.
/// </summary>
/// <remarks>
/// <para>
/// <b>The one documented exception to <c>BR-ORD-4</c>.</b> Slice 3 made a submitted order
/// unchangeable and left the exception unbuilt; these are the tests that say what "re-opens" means —
/// a second submission under a <i>new</i> mutation id is taken, while the rejected one stays terminal.
/// </para>
/// <para>
/// <b>Rejection is an API with no screen</b> (<c>ORD-09</c> is <c>Could</c>/Phase 4), so this drives
/// it over HTTP the way the demo will. The resubmission goes through <see cref="IOrderIngest"/>,
/// because that is the door a device uses and <c>/sync/push</c> does not grow its order arm until
/// slice 5.
/// </para>
/// </remarks>
[Collection(ServerCollection.Name)]
public class OrderRejectionTests(ServerFixture fixture)
{
    private const string Zone = "Europe/Bucharest";

    private static readonly Coordinates Shop = new(44.4638, 26.0946);

    private static string Unique(string label) => $"{label}-{Guid.NewGuid():N}"[..18];

    private HttpClient Admin() => fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

    private static CapturedOrderLine Line(Guid? productId = null, decimal quantity = 6m) =>
        new(productId ?? Guid.CreateVersion7(), quantity, "case", 12, 4.50m, 27.00m);

    private static CapturedOrder Captured(Guid visitId, params CapturedOrderLine[] lines) => new(
        Guid.CreateVersion7(),
        visitId,
        "EUR",
        lines.Sum(line => line.LineTotal),
        DateTimeOffset.Parse("2026-08-12T09:30:00Z"),
        lines.Length == 0 ? [Line()] : lines);

    [Fact]
    public async Task A_rejected_order_says_why_and_which_line()
    {
        using var admin = Admin();

        var (visitId, _) = await VisitAsync(admin);
        var offending = Guid.CreateVersion7();
        var captured = Captured(visitId, Line(offending), Line());

        Assert.Equal(OrderIngestRefusal.None, (await IngestAsync(captured)).Refusal);

        var rejected = await RejectAsync(
            admin,
            captured.OrderId,
            new OrderRejectionRequest(OrderRejectionReason.OffAssortment, offending, "Delisted last week."));

        Assert.Equal(HttpStatusCode.OK, rejected.StatusCode);

        var order = (await rejected.Content.ReadFromJsonAsync<OrderReadback>())!;

        Assert.Equal("Rejected", order.Status);
        Assert.Equal("OffAssortment", order.Rejection!.Reason);
        Assert.Equal(offending, order.Rejection.OffendingProductId);
        Assert.Equal("Delisted last week.", order.Rejection.Note);
    }

    [Fact]
    public async Task A_rejection_can_point_at_no_line_at_all()
    {
        // Half of F4's own examples do: an outlet that closed during the offline window is not a line
        // the rep can edit. The rejection is still whole-order — there is simply nowhere to point.
        using var admin = Admin();

        var (visitId, _) = await VisitAsync(admin);
        var captured = Captured(visitId, Line());

        Assert.Equal(OrderIngestRefusal.None, (await IngestAsync(captured)).Refusal);

        var rejected = await RejectAsync(
            admin, captured.OrderId, new OrderRejectionRequest(OrderRejectionReason.OutletClosed));

        Assert.Equal(HttpStatusCode.OK, rejected.StatusCode);

        var order = (await rejected.Content.ReadFromJsonAsync<OrderReadback>())!;

        Assert.Equal("OutletClosed", order.Rejection!.Reason);
        Assert.Null(order.Rejection.OffendingProductId);
    }

    [Fact]
    public async Task A_line_that_is_not_on_the_order_cannot_be_the_one_at_fault()
    {
        // Storing it would send the rep hunting for a product they never ordered.
        using var admin = Admin();

        var (visitId, _) = await VisitAsync(admin);
        var captured = Captured(visitId, Line());

        Assert.Equal(OrderIngestRefusal.None, (await IngestAsync(captured)).Refusal);

        var refused = await RejectAsync(
            admin,
            captured.OrderId,
            new OrderRejectionRequest(OrderRejectionReason.OffAssortment, Guid.CreateVersion7()));

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
    }

    [Fact]
    public async Task Rejecting_twice_is_refused_rather_than_overwriting_the_first_reason()
    {
        /*
         * Not idempotent, deliberately. The second rejection carries its own reason, and taking it
         * would replace the one the rep is already acting on — a rep reading "off assortment" while
         * the server holds "outlet closed" is worse than an error somebody sees.
         */
        using var admin = Admin();

        var (visitId, _) = await VisitAsync(admin);
        var captured = Captured(visitId, Line());

        Assert.Equal(OrderIngestRefusal.None, (await IngestAsync(captured)).Refusal);

        var first = await RejectAsync(
            admin, captured.OrderId, new OrderRejectionRequest(OrderRejectionReason.OffAssortment));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var second = await RejectAsync(
            admin, captured.OrderId, new OrderRejectionRequest(OrderRejectionReason.OutletClosed));

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

        // …and the first reason is still the one that stands.
        var stored = await admin.GetFromJsonAsync<OrderReadback>(
            $"/api/orders/by-visit/{visitId}");

        Assert.Equal("OffAssortment", stored!.Rejection!.Reason);
    }

    [Fact]
    public async Task A_rejected_order_takes_a_correction_under_a_new_mutation_id()
    {
        /*
         * `BR-ORD-9` in one test. The rep fixes the flagged line and pushes again; the order keeps its
         * identity — so "how many orders did this outlet place" still counts intent rather than
         * attempts — and comes back to `Submitted` carrying what they corrected.
         */
        using var admin = Admin();

        var (visitId, _) = await VisitAsync(admin);
        var offending = Guid.CreateVersion7();
        var captured = Captured(visitId, Line(offending));

        Assert.Equal(OrderIngestRefusal.None, (await IngestAsync(captured)).Refusal);

        await RejectAsync(
            admin,
            captured.OrderId,
            new OrderRejectionRequest(OrderRejectionReason.OffAssortment, offending));

        var corrected = captured with { Lines = [Line(quantity: 3m)], Total = 27.00m };

        Assert.Equal(OrderIngestRefusal.None, (await IngestAsync(corrected)).Refusal);

        var stored = await admin.GetFromJsonAsync<OrderReadback>($"/api/orders/by-visit/{visitId}");

        Assert.Equal(captured.OrderId, stored!.Id);
        Assert.Equal("Submitted", stored.Status);
        Assert.Equal(3m, Assert.Single(stored.Lines).Quantity);

        // The rejection is gone because the attempt that carried it is no longer the latest one.
        Assert.Null(stored.Rejection);
    }

    [Fact]
    public async Task Replaying_the_rejected_submission_leaves_the_correction_alone()
    {
        /*
         * "The original submission's mutation id is terminal" — `BR-ORD-9` — and this is the test that
         * makes the word mean something.
         *
         * The device retries the *rejected* push after the rep has already corrected the order, which
         * is an ordinary lost-ledger-entry retry. Re-applying it would put the offending line back and
         * re-reject an order that is now fine.
         */
        using var admin = Admin();

        var (visitId, _) = await VisitAsync(admin);
        var captured = Captured(visitId, Line(quantity: 6m));
        var rejectedMutation = Guid.CreateVersion7();

        Assert.Equal(
            OrderIngestRefusal.None,
            (await IngestAsync(captured, mutationId: rejectedMutation)).Refusal);

        await RejectAsync(
            admin, captured.OrderId, new OrderRejectionRequest(OrderRejectionReason.OffAssortment));

        var corrected = captured with { Lines = [Line(quantity: 3m)], Total = 27.00m };
        Assert.Equal(OrderIngestRefusal.None, (await IngestAsync(corrected)).Refusal);

        // The retry of the terminal id: accepted as a replay, and it changes nothing.
        Assert.Equal(
            OrderIngestRefusal.None,
            (await IngestAsync(captured, mutationId: rejectedMutation)).Refusal);

        var stored = await admin.GetFromJsonAsync<OrderReadback>($"/api/orders/by-visit/{visitId}");

        Assert.Equal("Submitted", stored!.Status);
        Assert.Equal(3m, Assert.Single(stored.Lines).Quantity);
    }

    [Fact]
    public async Task An_order_nobody_has_rejected_cannot_be_corrected()
    {
        // The lock still holds everywhere except the documented exception: a second, different push
        // against a *submitted* order is an edit after submit, which is what slice 3 closed.
        using var admin = Admin();

        var (visitId, _) = await VisitAsync(admin);
        var captured = Captured(visitId, Line());

        Assert.Equal(OrderIngestRefusal.None, (await IngestAsync(captured)).Refusal);

        var edited = captured with { Lines = [Line(quantity: 99m)] };

        Assert.Equal(OrderIngestRefusal.AlreadySubmitted, (await IngestAsync(edited)).Refusal);
    }

    [Fact]
    public async Task Rejecting_needs_the_permission_minted_for_it()
    {
        // `order:reject` names the act rather than the table: a holder can refuse an order, never
        // change one. The rep's own token gates `/sync/push`, and must not gate this.
        using var rep = fixture.CreateAuthenticatedClient();
        using var admin = Admin();

        var (visitId, _) = await VisitAsync(admin);
        var captured = Captured(visitId, Line());

        Assert.Equal(OrderIngestRefusal.None, (await IngestAsync(captured)).Refusal);

        var attempted = await RejectAsync(
            rep, captured.OrderId, new OrderRejectionRequest(OrderRejectionReason.OffAssortment));

        Assert.Equal(HttpStatusCode.Forbidden, attempted.StatusCode);
    }

    private static Task<HttpResponseMessage> RejectAsync(
        HttpClient client, Guid orderId, OrderRejectionRequest request) =>
        client.PostAsJsonAsync($"/api/orders/{orderId}/rejection", request);

    private Task<OrderIngestResult> IngestAsync(
        CapturedOrder captured, string? userId = null, Guid? mutationId = null) =>
        AsAsync(fixture.AdminAccessToken, services => services
            .GetRequiredService<IOrderIngest>()
            .IngestAsync(
                captured,
                mutationId ?? Guid.CreateVersion7(),
                userId ?? SubjectOf(fixture.AdminAccessToken)));

    private async Task<(Guid VisitId, Guid OutletId)> VisitAsync(HttpClient client)
    {
        var channel = await client.PostAsJsonAsync(
            "/api/outlets/channels", new ChannelRequest(Unique("Channel")));

        var channelId = (await channel.Content.ReadFromJsonAsync<ChannelResponse>())!.Id;

        var created = await client.PostAsJsonAsync(
            "/api/outlets",
            new CreateOutletRequest(Unique("OUT"), "Corner Shop", channelId, Zone, Location: Shop));

        var outletId = (await created.Content.ReadFromJsonAsync<OutletResponse>())!.Id;

        var response = await client.PostAsJsonAsync(
            "/api/visits/check-in", new CheckInRequest(outletId, Shop.Latitude, Shop.Longitude));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var visit = (await response.Content.ReadFromJsonAsync<VisitDetailResponse>())!.Visit;

        return (visit.Id, outletId);
    }

    /// <summary>
    /// Runs <paramref name="work"/> in a scope whose tenant context matches a real token.
    /// </summary>
    /// <remarks>
    /// <b>The fourth copy of this harness</b>, after <c>AuditIngestTests</c>, <c>OrderIngestTests</c>
    /// and <c>PricingServiceTests</c>. W10 named the third as the point to extract rather than copy
    /// again; that point is behind us, and this copies it once more rather than refactoring four test
    /// classes inside a slice about rejection. A debt recorded, not paid.
    /// </remarks>
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

    private sealed record RejectionReadback(string Reason, Guid? OffendingProductId, string? Note);

    private sealed record OrderLineReadback(string UnitOfMeasure, decimal Quantity, decimal LineTotal);

    private sealed record OrderReadback(
        Guid Id,
        Guid OutletId,
        string Status,
        string CurrencyCode,
        decimal Total,
        IReadOnlyList<OrderLineReadback> Lines,
        RejectionReadback? Rejection);
}

