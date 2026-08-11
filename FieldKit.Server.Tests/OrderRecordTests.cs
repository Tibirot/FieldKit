using FieldKit.Modules.Order;
using FieldKit.Modules.Order.Contracts;

namespace FieldKit.Server.Tests;

/// <summary>
/// What an order must be true of to be stored at all (<c>ORD-01</c>, <c>BR-ORD-7</c>) — W11 slice 1.
/// </summary>
/// <remarks>
/// Pure: <see cref="Order.Record"/> takes a payload and answers, so none of this needs a database.
/// The HTTP path, the visit checks and the read side are <c>OrderTests</c>.
/// </remarks>
public class OrderRecordTests
{
    private static readonly Guid OutletId = Guid.CreateVersion7();

    private static CapturedOrderLine Line(Guid? productId = null, decimal quantity = 6m) =>
        new(productId ?? Guid.CreateVersion7(), quantity, "case", 12, 4.50m, 27.00m);

    private static CapturedOrder Captured(
        IReadOnlyList<CapturedOrderLine>? lines = null, string currency = "EUR") =>
        new(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            currency,
            27.00m,
            DateTimeOffset.Parse("2026-08-11T09:30:00Z"),
            lines ?? [Line()]);

    [Fact]
    public void An_order_arrives_already_submitted()
    {
        // B4 puts Draft on the device. The first status this server ever writes is Submitted —
        // there is no create-a-draft path, and an order that could be drafted here would be a
        // second writer into a record whose conflict story depends on one (B7).
        var (order, refusal) = Order.Record(Captured(), OutletId, "rep-1");

        Assert.Equal(OrderRefusal.None, refusal);
        Assert.Equal(OrderStatus.Submitted, order!.Status);
    }

    [Fact]
    public void The_outlet_is_copied_from_the_visit_rather_than_supplied()
    {
        // The payload has no outlet id in it — deliberately. A device that could name the outlet
        // could name a different one from the visit's, and "which shop was this" would have two
        // answers. It comes from the visit the ingest already had to look up.
        var (order, _) = Order.Record(Captured(), OutletId, "rep-1");

        Assert.Equal(OutletId, order!.OutletId);
    }

    [Fact]
    public void Positions_are_assigned_rather_than_accepted()
    {
        var lines = new[] { Line(), Line(), Line() };

        var (order, _) = Order.Record(Captured(lines), OutletId, "rep-1");

        Assert.Equal([1, 2, 3], order!.Lines.Select(line => line.Position));
    }

    [Fact]
    public void An_order_for_nothing_is_refused()
    {
        var (order, refusal) = Order.Record(Captured([]), OutletId, "rep-1");

        Assert.Null(order);
        Assert.Equal(OrderRefusal.Empty, refusal);
    }

    [Fact]
    public void A_quantity_of_zero_is_refused()
    {
        // Not rounded away and not dropped. A line the rep left at zero is either a mistake or a
        // removal they did not finish, and storing it would put a product on an order nobody bought.
        var (_, refusal) = Order.Record(Captured([Line(quantity: 0m)]), OutletId, "rep-1");

        Assert.Equal(OrderRefusal.NonPositiveQuantity, refusal);
    }

    [Fact]
    public void The_same_product_twice_is_refused_rather_than_summed()
    {
        // Summing would invent a quantity nobody typed, and picking one would discard a line the
        // rep entered. Neither is something a later reader could unpick.
        var productId = Guid.CreateVersion7();

        var (_, refusal) = Order.Record(
            Captured([Line(productId), Line(productId)]), OutletId, "rep-1");

        Assert.Equal(OrderRefusal.DuplicateProduct, refusal);
    }

    [Fact]
    public void A_line_without_its_unit_is_refused()
    {
        // "12" means nothing without the word beside it, and a UoM that arrived empty cannot be
        // recovered from the product later — by then it may have been corrected.
        var line = Line() with { UnitOfMeasure = "  " };

        var (_, refusal) = Order.Record(Captured([line]), OutletId, "rep-1");

        Assert.Equal(OrderRefusal.UnitOfMeasureMissing, refusal);
    }

    [Theory]
    [InlineData("EU")]
    [InlineData("EURO")]
    [InlineData("E1R")]
    [InlineData("")]
    public void A_currency_that_is_not_three_letters_is_refused(string currency)
    {
        var (_, refusal) = Order.Record(Captured(currency: currency), OutletId, "rep-1");

        Assert.Equal(OrderRefusal.CurrencyInvalid, refusal);
    }

    [Fact]
    public void A_lowercase_currency_is_normalised_rather_than_refused()
    {
        // A device sending "eur" means EUR. Refusing it would strand an order over capitalisation,
        // which is the sort of refusal a rep in a shop can do nothing about.
        var (order, refusal) = Order.Record(Captured(currency: "eur"), OutletId, "rep-1");

        Assert.Equal(OrderRefusal.None, refusal);
        Assert.Equal("EUR", order!.CurrencyCode);
    }

    [Fact]
    public void The_devices_money_is_stored_exactly_as_it_arrived()
    {
        /*
         * BR-ORD-6 and W11 slice 0: the device's numbers are the record, because they are what the
         * rep and the shopkeeper agreed. Nothing here recomputes them — slice 2 adds the server's
         * arithmetic *beside* these, never over them.
         *
         * Asserted on the exact decimals rather than a rounded comparison: a `numeric(18,4)` column
         * that silently truncated would still pass a `Math.Round` assertion.
         */
        var line = Line() with { UnitPrice = 4.4550m, LineTotal = 26.7300m };

        var (order, _) = Order.Record(Captured([line]) with { Total = 26.7300m }, OutletId, "rep-1");

        Assert.Equal(4.4550m, order!.Lines[0].UnitPrice);
        Assert.Equal(26.7300m, order.Lines[0].LineTotal);
        Assert.Equal(26.7300m, order.Total);
    }

    [Fact]
    public void The_capture_time_is_the_devices_not_the_servers()
    {
        // An order taken in a basement on Tuesday and pushed from a car park on Thursday happened
        // on Tuesday. `CreatedAtUtc` records when this server heard; the gap between them is a fact
        // about the sync rather than about the order.
        var captured = Captured() with { CapturedAtUtc = DateTimeOffset.Parse("2026-08-04T07:15:00Z") };

        var (order, _) = Order.Record(captured, OutletId, "rep-1");

        Assert.Equal(DateTimeOffset.Parse("2026-08-04T07:15:00Z"), order!.CapturedAtUtc);
    }

    [Fact]
    public void A_line_carries_the_unit_and_pack_it_was_captured_under()
    {
        // Copied, never reached for: a product's UoM can be corrected in the back office, and
        // "12 cases" re-described as "12 bottles" is a tenfold error nobody typed.
        var (order, _) = Order.Record(Captured(), OutletId, "rep-1");

        Assert.Equal("case", order!.Lines[0].UnitOfMeasure);
        Assert.Equal(12, order.Lines[0].PackSize);
    }

    [Fact]
    public void More_lines_than_the_bound_are_refused()
    {
        var lines = Enumerable.Range(0, Order.MaximumLines + 1).Select(_ => Line()).ToList();

        var (_, refusal) = Order.Record(Captured(lines), OutletId, "rep-1");

        Assert.Equal(OrderRefusal.TooManyLines, refusal);
    }
}
