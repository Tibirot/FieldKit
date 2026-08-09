using System.Globalization;
using System.Text;
using FieldKit.Modules.Products;
using FieldKit.SharedKernel;

namespace FieldKit.Server.Tests;

/// <summary>
/// Emits the generated halves of the shared vector files (<c>PRD-08</c>, <c>BR-PRD-8/9</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>What generated vectors can and cannot do, because it is not symmetric.</b> The expectations
/// here are produced by the C# engine, so running them back against that engine proves nothing about
/// it — a bug would be generated into the file and then confirmed by it. Their whole value is on the
/// other side: they pin C#'s behaviour across a far wider input range than anyone would hand-write,
/// so the TypeScript mirror has an oracle for regions nobody thought about.
/// </para>
/// <para>
/// What tests <i>C#</i> is the hand-written cases (which encode what the rules should be, decided
/// before the code) and <see cref="VectorPropertyTests"/> (which assert invariants the engine's own
/// output cannot satisfy circularly). This file is the third leg, and it is the one aimed at W7.
/// Saying so is the point — a suite that looked like it doubled the C# coverage would be a suite
/// nobody thought to supplement.
/// </para>
/// <para>
/// <b>Deterministic, and the committed files are checked against it</b>
/// (<see cref="GeneratedVectorTests"/>). A generator whose output drifted from what is on disk would
/// let the mirror test yesterday's engine while C# tests today's, which is the one failure this
/// apparatus exists to prevent.
/// </para>
/// </remarks>
internal static class VectorGenerator
{
    /// <summary>
    /// A hand-rolled PRNG, because <see cref="Random"/>'s seeded sequence is <b>not stable across
    /// .NET versions</b>.
    /// </summary>
    /// <remarks>
    /// .NET 6 changed the algorithm behind <c>new Random(seed)</c>. Committed artifacts generated on
    /// one runtime and regenerated on another would then differ in every case, producing an enormous
    /// diff that says nothing — and, worse, a regeneration that looks like a deliberate change.
    /// Sixteen lines of xorshift64* owned here is stable for as long as this file is.
    /// </remarks>
    private sealed class Prng(ulong seed)
    {
        private ulong _state = seed == 0 ? 0x9E3779B97F4A7C15 : seed;

        public ulong Next()
        {
            _state ^= _state >> 12;
            _state ^= _state << 25;
            _state ^= _state >> 27;
            return _state * 0x2545F4914F6CDD1D;
        }

        /// <summary>A value in <c>[0, bound)</c>.</summary>
        public int Next(int bound) => (int)(Next() % (ulong)bound);

        public T Pick<T>(IReadOnlyList<T> options) => options[Next(options.Count)];
    }

    /// <summary>Every generated file, by path under <c>vectors/</c>.</summary>
    public static IReadOnlyDictionary<string, string> Files() => new Dictionary<string, string>
    {
        ["pricing/tax-application.generated.v1.json"] = TaxApplication(),
        ["pricing/price-resolution.generated.v1.json"] = PriceResolution(),
        ["pricing/promotion-resolution.generated.v1.json"] = PromotionResolution(),
    };

    // ── Tax application ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Nets chosen to sit on the boundaries rounding actually breaks at.
    /// </summary>
    /// <remarks>
    /// Not random. A sweep is better than a sample here because the interesting inputs are known and
    /// few: sub-cent values, exact half-cents once a rate is applied, values at every scale from 2 to
    /// 3 decimals, and magnitudes large enough that a float would have started drifting.
    /// </remarks>
    private static readonly decimal[] Nets =
    [
        0.01m, 0.02m, 0.03m, 0.04m, 0.05m, 0.125m, 0.99m,
        1.00m, 1.005m, 2.50m, 3.33m, 7.77m, 9.99m,
        12.345m, 12.99m, 99.99m, 1234.56m, 99999.99m,
    ];

    /// <summary>Real VAT rates, plus both boundaries and the fractional ones.</summary>
    private static readonly decimal[] Rates =
    [
        0m, 1m, 4.5m, 5m, 5.5m, 7m, 8.25m, 9m, 13.5m, 17.5m, 19m, 21m, 27m, 100m,
    ];

    /// <summary>
    /// The currencies this sweep runs, and the nets worth running in each.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Added in W7 slice 15, because the file had been EUR-only and the currency was therefore the
    /// one input the whole sweep never varied.</b> Rounding to the minor unit is the rule this file
    /// exists to pin, and every case in it asked a two-decimal currency — so an implementation that
    /// hard-coded 2 passed all 252 of them. The TypeScript mirror's own tests covered JPY and KWD by
    /// hand; the shared oracle did not, which is the wrong way round.
    /// </para>
    /// <para>
    /// EUR keeps the full sweep, unchanged, so the existing expectations stay put and the diff is
    /// additive. The rest carry nets chosen for what their scale does: <b>RON</b> is a second
    /// two-decimal currency and only has to prove nothing special-cases "EUR"; <b>JPY</b> has no
    /// minor unit at all, so its interesting nets are the ones with fractions to lose; <b>KWD</b> has
    /// three, so its are the ones with a fourth digit to round.
    /// </para>
    /// </remarks>
    private static readonly (string Currency, decimal[] Nets)[] TaxCurrencies =
    [
        ("EUR", Nets),
        ("RON", [0.125m, 12.99m, 99999.99m]),
        ("JPY", [1m, 5m, 7.5m, 100m, 1234.5m, 99999m]),
        ("KWD", [0.0005m, 1.2345m, 12.3455m, 1234.5675m, 99999.999m]),
    ];

    private static string TaxApplication()
    {
        var body = new StringBuilder();

        foreach (var (currency, nets) in TaxCurrencies)
        {
            foreach (var net in nets)
            {
                foreach (var rate in Rates)
                {
                    var applied = TaxEngine.Apply(new Money(net, currency), rate);
                    var scale = applied.Net.MinorUnits;

                    body.Append(body.Length == 0 ? "\n    " : ",\n    ");
                    body.Append(
                        $$"""
                          { "net": "{{Text(net)}}", "currency": "{{currency}}", "percentage": "{{Text(rate)}}", "expected": { "net": "{{TextAt(applied.Net.Amount, scale)}}", "tax": "{{TextAt(applied.Tax.Amount, scale)}}", "gross": "{{TextAt(applied.Gross.Amount, scale)}}" } }
                          """);
                }
            }
        }

        return Wrap(
            "PRD-07 / BR-PRD-9",
            "Every net crossed with every rate, in four currencies. The expectations come from the "
            + "C# engine, so this file is an oracle for the TypeScript mirror rather than a test of "
            + "C# — see vectors/README.md. Nets sit on the boundaries rounding breaks at; rates "
            + "include both ends and the fractional ones real jurisdictions use; the currencies "
            + "cover no minor unit (JPY), two (EUR, RON) and three (KWD), because rounding to the "
            + "currency's scale is the rule this file exists to pin.",
            "application",
            body.ToString());
    }

    // ── Price resolution ─────────────────────────────────────────────────────────────────────────

    private static string PriceResolution()
    {
        var random = new Prng(20260807);
        var body = new StringBuilder();

        for (var index = 0; index < 120; index++)
        {
            var on = new DateOnly(2026, 1 + random.Next(12), 1 + random.Next(28));
            var candidates = new List<PriceCandidate>();

            foreach (var slot in Enumerable.Range(0, 1 + random.Next(4)))
            {
                var from = new DateOnly(2026, 1 + random.Next(12), 1 + random.Next(28));
                var open = random.Next(3) == 0;

                candidates.Add(new PriceCandidate(
                    Id(index, slot),
                    random.Next(2) == 0 ? PriceScope.Channel : PriceScope.Outlet,
                    random.Pick(["EUR", "RON", "GBP"]),
                    from,
                    open ? null : from.AddDays(1 + random.Next(180)),
                    random.Pick(Nets)));
            }

            var resolved = PriceResolver.Resolve(candidates, on);

            body.Append(body.Length == 0 ? "\n    " : ",\n    ");
            body.Append(
                $$"""
                  {
                        "name": "generated {{index}}",
                        "on": "{{on:yyyy-MM-dd}}",
                        "candidates": [{{string.Join(", ", candidates.Select(Candidate))}}],
                        "expected": {{(resolved is null
                            ? "null"
                            : $$"""{ "priceListId": "{{resolved.PriceListId}}", "scope": "{{resolved.Scope}}", "currency": "{{resolved.Currency}}", "amount": "{{Text(resolved.Amount)}}" }""")}}
                      }
                  """);
        }

        return Wrap(
            "PRD-04 / BR-PRD-2",
            "Randomly shaped candidate sets, seeded so the file is reproducible. Expectations come "
            + "from the C# engine — an oracle for the mirror, not a test of C#.",
            "cases",
            body.ToString());
    }

    private static string Candidate(PriceCandidate candidate) =>
        $$"""
          { "priceListId": "{{candidate.PriceListId}}", "scope": "{{candidate.Scope}}", "currency": "{{candidate.Currency}}", "effectiveFrom": "{{candidate.EffectiveFrom:yyyy-MM-dd}}", "effectiveTo": {{(candidate.EffectiveTo is { } to ? $"\"{to:yyyy-MM-dd}\"" : "null")}}, "amount": "{{Text(candidate.Amount)}}" }
          """;

    // ── Promotion resolution ─────────────────────────────────────────────────────────────────────

    private static string PromotionResolution()
    {
        var random = new Prng(20260808);
        var body = new StringBuilder();

        for (var index = 0; index < 120; index++)
        {
            var on = new DateOnly(2026, 1 + random.Next(12), 1 + random.Next(28));
            var quantity = 1 + random.Next(40);
            var candidates = new List<PromotionCandidate>();

            foreach (var slot in Enumerable.Range(0, 1 + random.Next(4)))
            {
                var from = new DateOnly(2026, 1 + random.Next(12), 1 + random.Next(28));
                var open = random.Next(3) == 0;
                var to = open ? (DateOnly?)null : from.AddDays(1 + random.Next(180));
                var priority = random.Next(5) * 10 - 10;

                // Every type, so the generated set exercises the tier scan and the bundle gate as
                // well as the flat comparison.
                candidates.Add((random.Next(4)) switch
                {
                    0 => new PromotionCandidate(
                        Id(index, slot), PromotionType.PercentOff, priority, from, to,
                        PercentOff: random.Pick([5m, 12.5m, 40m, 100m])),

                    1 => new PromotionCandidate(
                        Id(index, slot), PromotionType.FixedAmountOff, priority, from, to,
                        AmountOff: random.Pick([0.5m, 2.5m, 19.99m]), Currency: "EUR"),

                    2 => new PromotionCandidate(
                        Id(index, slot), PromotionType.VolumeTiered, priority, from, to,
                        Tiers:
                        [
                            .. new[] { 2, 6, 12, 24 }
                                .Take(1 + random.Next(4))
                                .Select(min => new PromotionTierCandidate(
                                    min, min * 0.5m, null, null)),
                        ]),

                    _ => new PromotionCandidate(
                        Id(index, slot), PromotionType.BuyXGetY, priority, from, to,
                        Bundle: new BundleCandidate(
                            1 + random.Next(6), 1 + random.Next(3),
                            random.Pick([50m, 100m]), null)),
                });
            }

            var resolved = PromotionResolver.Resolve(candidates, quantity, on);

            body.Append(body.Length == 0 ? "\n    " : ",\n    ");
            body.Append(
                $$"""
                  {
                        "name": "generated {{index}}",
                        "on": "{{on:yyyy-MM-dd}}",
                        "quantity": {{quantity}},
                        "candidates": [{{string.Join(", ", candidates.Select(Candidate))}}],
                        "expected": {{(resolved is null ? "null" : Resolved(resolved))}}
                      }
                  """);
        }

        return Wrap(
            "PRD-06 / BR-PRD-3",
            "Randomly shaped candidate sets across all four promotion types, seeded so the file is "
            + "reproducible. Expectations come from the C# engine — an oracle for the mirror, not a "
            + "test of C#.",
            "cases",
            body.ToString());
    }

    private static string Candidate(PromotionCandidate candidate)
    {
        var parts = new List<string>
        {
            $"\"promotionId\": \"{candidate.PromotionId}\"",
            $"\"type\": \"{candidate.Type}\"",
            $"\"priority\": {candidate.Priority}",
            $"\"validFrom\": \"{candidate.ValidFrom:yyyy-MM-dd}\"",
            $"\"validTo\": {(candidate.ValidTo is { } to ? $"\"{to:yyyy-MM-dd}\"" : "null")}",
        };

        if (candidate.PercentOff is { } percent) parts.Add($"\"percentOff\": \"{Text(percent)}\"");
        if (candidate.AmountOff is { } amount) parts.Add($"\"amountOff\": \"{Text(amount)}\"");
        if (candidate.Currency is { } currency) parts.Add($"\"currency\": \"{currency}\"");

        if (candidate.Tiers is { } tiers)
        {
            parts.Add(
                "\"tiers\": ["
                + string.Join(", ", tiers.Select(tier =>
                    $$"""{ "minQuantity": {{tier.MinQuantity}}, "percentOff": "{{Text(tier.PercentOff!.Value)}}" }"""))
                + "]");
        }

        if (candidate.Bundle is { } bundle) parts.Add($"\"bundle\": {Bundle(bundle)}");

        return "{ " + string.Join(", ", parts) + " }";
    }

    private static string Bundle(BundleCandidate bundle) =>
        $$"""
          { "buyQuantity": {{bundle.BuyQuantity}}, "getQuantity": {{bundle.GetQuantity}}, "getPercentOff": "{{Text(bundle.GetPercentOff)}}", "getProductId": null }
          """;

    private static string Resolved(ResolvedPromotion resolved) =>
        "{ "
        + $"\"promotionId\": \"{resolved.PromotionId}\", "
        + $"\"type\": \"{resolved.Type}\", "
        + $"\"priority\": {resolved.Priority}, "
        + $"\"percentOff\": {Quoted(resolved.PercentOff)}, "
        + $"\"amountOff\": {Quoted(resolved.AmountOff)}, "
        + $"\"currency\": {(resolved.Currency is { } c ? $"\"{c}\"" : "null")}, "
        + $"\"bundle\": {(resolved.Bundle is { } b ? Bundle(b) : "null")}"
        + " }";

    // ── Shared ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A stable id per case and slot, so regenerating does not churn every line.
    /// </summary>
    /// <remarks>
    /// Deliberately <i>not</i> <see cref="Guid.CreateVersion7"/>, which embeds a timestamp — every
    /// regeneration would rewrite every id and bury a real change in noise. These are also spread
    /// across the first four bytes on purpose, so the tiebreak comparison meets pairs that a
    /// little-endian <c>Guid.ToByteArray()</c> would order backwards.
    /// </remarks>
    private static Guid Id(int index, int slot) =>
        new($"{(uint)(index * 2654435761 + slot):x8}-0000-7000-8000-{index:x8}{slot:x4}");

    /// <summary>
    /// An *input* amount, at the scale it was written with (and at least two places).
    /// </summary>
    /// <remarks>
    /// Inputs are deliberately sub-cent in places — <c>0.125</c>, <c>1.005</c> — because that is
    /// where rounding breaks. Printing them at the currency's scale would round the input away and
    /// leave a file full of cases that no longer test anything.
    /// </remarks>
    private static string Text(decimal value) =>
        value.ToString("0.00##", CultureInfo.InvariantCulture);

    /// <summary>
    /// An *expected* amount, at exactly the currency's scale.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Text"/> because the two say different things. An expectation is what
    /// the engine returned, and the engine returns money at the currency's minor units — a yen with
    /// no decimals, a dinar with three. Formatting it with a two-decimal minimum would write
    /// <c>"1.00"</c> for a yen: a scale no invoice can express, and one the TypeScript mirror
    /// (whose <c>toWire</c> asks the currency) would correctly disagree with.
    /// </remarks>
    private static string TextAt(decimal value, int decimals) =>
        value.ToString("F" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

    private static string Quoted(decimal? value) => value is { } d ? $"\"{Text(d)}\"" : "null";

    /// <summary>
    /// Wraps a body in the file envelope. Emits <c>\n</c> line endings explicitly.
    /// </summary>
    /// <remarks>
    /// Not <see cref="Environment.NewLine"/>: the committed file is compared against this output
    /// byte for byte, and a Windows author writing CRLF where CI reads LF would fail the comparison
    /// on every line while nothing had actually changed. The same trap the promotion check
    /// constraint fell into.
    /// </remarks>
    private static string Wrap(string rule, string description, string key, string body) =>
        "{\n"
        + "  \"version\": 1,\n"
        + "  \"generated\": true,\n"
        + $"  \"rule\": \"{rule}\",\n"
        + $"  \"description\": \"{description}\",\n"
        + $"  \"{key}\": [{body}\n  ]\n"
        + "}\n";
}
