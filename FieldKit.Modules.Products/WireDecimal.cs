using System.Globalization;

namespace FieldKit.Modules.Products;

/// <summary>
/// How a decimal reaches a device on the pull feeds (<c>BR-PRD-8</c>, <c>OFF-03</c>) — W11 slice 7a.
/// </summary>
/// <remarks>
/// <para>
/// <b>Money and percentages travel as strings, and this is the one place that is decided.</b> A bare
/// <c>4.50</c> is a JSON number, and <c>JSON.parse</c> hands the device an IEEE-754 float — before
/// the pricing engine, which reads decimal strings into <c>decimal.js</c> precisely so a rep's total
/// and the server's recomputation agree to the cent (<c>BR-ORD-2</c>), has seen it.
/// </para>
/// <para>
/// <b>This was already the rule and the feeds did not follow it.</b>
/// <c>ScoreWeightSnapshot.Percentage</c> got it right in W10 with a comment making exactly this
/// argument; prices and promotions shipped in W6/W8 as numbers and nothing noticed, because the
/// parity vectors feed the engine strings from a file and never touch a pull feed. The first thing
/// to price from the device's own tables found it.
/// </para>
/// <para>
/// <b>Invariant culture, always.</b> A device parsing <c>"4,50"</c> under a comma-decimal locale is
/// one separator away from charging four hundred and fifty — the same hazard <c>MoneyJsonConverter</c>
/// refuses a thousands separator for.
/// </para>
/// </remarks>
internal static class WireDecimal
{
    /// <summary>
    /// The shape <c>ScoreWeightSnapshot</c> uses: at least two places, up to four.
    /// </summary>
    /// <remarks>
    /// Two because money has them and a price list showing <c>4.5</c> reads as an error to anyone who
    /// works with prices. Up to four because a unit price legitimately carries them — <c>BR-PRD-8</c>
    /// rounds at the line rather than at the price — and a format that truncated would round twice.
    /// </remarks>
    private const string Shape = "0.00##";

    public static string From(decimal value) => value.ToString(Shape, CultureInfo.InvariantCulture);

    public static string? From(decimal? value) => value is { } present ? From(present) : null;
}
