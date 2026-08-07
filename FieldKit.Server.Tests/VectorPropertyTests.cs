using FieldKit.Modules.Products;
using FieldKit.SharedKernel;

namespace FieldKit.Server.Tests;

/// <summary>
/// Invariants the engines must satisfy for <i>every</i> input, not just the ones anyone wrote down
/// (<c>PRD-08</c>, <c>BR-PRD-8/9</c>) — W6 slice 14.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the half of <c>PRD-08</c> that can actually find a C# bug.</b> The generated vectors
/// take their expectations from the C# engine, so running them back against it is circular — their
/// value is as an oracle for the TypeScript mirror. What is left over for C# is the class of
/// statement that does not need an expected answer: <i>whatever</i> the engine returns, these things
/// must hold.
/// </para>
/// <para>
/// They are also the part the mirror should reimplement rather than read. A vector file transfers
/// answers; a property transfers a rule, and "the three numbers on an invoice add up" is a rule
/// worth W7 asserting in its own suite rather than trusting 252 examples of.
/// </para>
/// <para>
/// Deterministic, seeded input rather than a randomised run per build. A property suite that fails
/// once a fortnight on a seed nobody can reproduce teaches people to re-run CI, which is worse than
/// not having it.
/// </para>
/// </remarks>
public class VectorPropertyTests
{
    private static IEnumerable<(decimal Net, decimal Rate)> TaxInputs()
    {
        // The full sweep the generator emits, exercised here for properties instead of expectations.
        foreach (var net in new[]
                 {
                     0.01m, 0.02m, 0.03m, 0.04m, 0.05m, 0.125m, 0.99m, 1.00m, 1.005m,
                     2.50m, 3.33m, 7.77m, 9.99m, 12.345m, 12.99m, 99.99m, 1234.56m, 99999.99m,
                 })
        {
            foreach (var rate in new[]
                     {
                         0m, 1m, 4.5m, 5m, 5.5m, 7m, 8.25m, 9m, 13.5m, 17.5m, 19m, 21m, 27m, 100m,
                     })
            {
                yield return (net, rate);
            }
        }
    }

    [Fact]
    public void Net_plus_tax_is_always_gross()
    {
        // What a customer does with an invoice. Any scheme computing gross independently — net * 1.19
        // being the obvious one — satisfies individual expectations and breaks this somewhere.
        foreach (var (net, rate) in TaxInputs())
        {
            var applied = TaxEngine.Apply(new Money(net, "EUR"), rate);

            Assert.Equal(applied.Gross.Amount, applied.Net.Amount + applied.Tax.Amount);
        }
    }

    [Fact]
    public void Tax_is_never_more_precise_than_the_currency()
    {
        // A tax of 2.4681 cannot be charged. This catches a rounding step being dropped far more
        // reliably than any single expectation, because it holds for inputs nobody enumerated.
        foreach (var (net, rate) in TaxInputs())
        {
            var applied = TaxEngine.Apply(new Money(net, "EUR"), rate);

            Assert.Equal(applied.Tax.Amount, decimal.Round(applied.Tax.Amount, 2));
            Assert.Equal(applied.Net.Amount, decimal.Round(applied.Net.Amount, 2));
        }
    }

    [Fact]
    public void Tax_never_runs_backwards_as_the_rate_rises()
    {
        // Monotonicity. A sign error, a stray truncation or a rate read as a fraction rather than a
        // percentage all break this, and none of them is guaranteed to break a specific expectation.
        foreach (var net in new[] { 0.01m, 0.99m, 12.99m, 1234.56m })
        {
            var previous = -1m;

            foreach (var rate in new[] { 0m, 1m, 4.5m, 9m, 19m, 27m, 100m })
            {
                var tax = TaxEngine.Apply(new Money(net, "EUR"), rate).Tax.Amount;

                Assert.True(tax >= previous, $"{net} at {rate}% gave {tax} after {previous}");
                previous = tax;
            }
        }
    }

    [Fact]
    public void The_boundaries_mean_what_they_say()
    {
        // 0% takes nothing and 100% doubles the line. Stated as properties rather than two vectors,
        // because they have to hold for every net rather than the two someone picked.
        foreach (var (net, _) in TaxInputs())
        {
            var money = new Money(net, "EUR");

            var zero = TaxEngine.Apply(money, 0m);
            Assert.Equal(0m, zero.Tax.Amount);
            Assert.Equal(zero.Net.Amount, zero.Gross.Amount);

            var full = TaxEngine.Apply(money, 100m);
            Assert.Equal(full.Net.Amount, full.Tax.Amount);
            Assert.Equal(full.Net.Amount * 2, full.Gross.Amount);
        }
    }

    [Fact]
    public void Resolution_does_not_depend_on_the_order_candidates_arrive_in()
    {
        // The property that matters most for the two selection engines, and the one a hand-written
        // suite can only sample. Neither Postgres nor a device's local store promises an order, so a
        // resolver whose answer depended on it would be non-deterministic in production and stable in
        // every test.
        var random = new Prng(20260809);

        for (var round = 0; round < 200; round++)
        {
            var on = new DateOnly(2026, 1 + random.Next(12), 1 + random.Next(28));
            var candidates = PriceCandidates(random, round);

            var expected = PriceResolver.Resolve(candidates, on);

            foreach (var _ in Enumerable.Range(0, 5))
            {
                var shuffled = Shuffle(random, candidates);

                Assert.Equal(expected, PriceResolver.Resolve(shuffled, on));
            }
        }
    }

    [Fact]
    public void Promotion_resolution_does_not_depend_on_order_either()
    {
        var random = new Prng(20260810);

        for (var round = 0; round < 200; round++)
        {
            var on = new DateOnly(2026, 1 + random.Next(12), 1 + random.Next(28));
            var quantity = 1 + random.Next(40);
            var candidates = PromotionCandidates(random, round);

            var expected = PromotionResolver.Resolve(candidates, quantity, on);

            foreach (var _ in Enumerable.Range(0, 5))
            {
                var shuffled = Shuffle(random, candidates);

                Assert.Equal(expected, PromotionResolver.Resolve(shuffled, quantity, on));
            }
        }
    }

    [Fact]
    public void A_winner_is_always_one_of_the_candidates_and_always_covers_the_date()
    {
        // Two claims a resolver could violate without any expectation noticing: inventing an answer,
        // or returning one whose window does not cover the date it was asked about.
        var random = new Prng(20260811);

        for (var round = 0; round < 200; round++)
        {
            var on = new DateOnly(2026, 1 + random.Next(12), 1 + random.Next(28));
            var candidates = PriceCandidates(random, round);

            if (PriceResolver.Resolve(candidates, on) is not { } resolved) continue;

            var winner = Assert.Single(
                candidates.Where(candidate => candidate.PriceListId == resolved.PriceListId
                                              && candidate.Scope == resolved.Scope));

            Assert.True(on >= winner.EffectiveFrom);
            Assert.True(winner.EffectiveTo is not { } end || on < end);
            Assert.Equal(winner.Amount, resolved.Amount);
            Assert.Equal(winner.Currency, resolved.Currency);
        }
    }

    [Fact]
    public void A_selected_promotion_always_applies_at_the_quantity_it_was_asked_about()
    {
        // The filter that stops an inert promotion winning and doing nothing, asserted over the whole
        // random space rather than the two vectors that name it.
        var random = new Prng(20260812);

        for (var round = 0; round < 200; round++)
        {
            var on = new DateOnly(2026, 1 + random.Next(12), 1 + random.Next(28));
            var quantity = 1 + random.Next(40);
            var candidates = PromotionCandidates(random, round);

            if (PromotionResolver.Resolve(candidates, quantity, on) is not { } resolved) continue;

            var winner = candidates.Single(c => c.PromotionId == resolved.PromotionId);

            Assert.True(on >= winner.ValidFrom);
            Assert.True(winner.ValidTo is not { } end || on < end);

            switch (winner.Type)
            {
                case PromotionType.VolumeTiered:
                    // A tier was reached, and the resolved discount is that tier's rather than a
                    // different one's.
                    var tier = winner.Tiers!
                        .Where(t => t.MinQuantity <= quantity)
                        .MaxBy(t => t.MinQuantity);

                    Assert.NotNull(tier);
                    Assert.Equal(tier.PercentOff, resolved.PercentOff);
                    break;

                case PromotionType.BuyXGetY:
                    Assert.True(quantity >= winner.Bundle!.BuyQuantity);
                    break;

                default:
                    Assert.Equal(winner.PercentOff, resolved.PercentOff);
                    Assert.Equal(winner.AmountOff, resolved.AmountOff);
                    break;
            }
        }
    }

    [Fact]
    public void Nothing_that_covers_the_date_is_ever_beaten_by_something_that_does_not()
    {
        // Stated the other way round from the tests above: if any candidate is live and applicable,
        // the answer is never null. A filter written as `continue` in the wrong branch passes every
        // positive case and fails this.
        var random = new Prng(20260813);

        for (var round = 0; round < 200; round++)
        {
            var on = new DateOnly(2026, 1 + random.Next(12), 1 + random.Next(28));
            var candidates = PriceCandidates(random, round);

            var anyLive = candidates.Any(
                c => on >= c.EffectiveFrom && (c.EffectiveTo is not { } end || on < end));

            Assert.Equal(anyLive, PriceResolver.Resolve(candidates, on) is not null);
        }
    }

    // ── Input shaping ────────────────────────────────────────────────────────────────────────────

    private static List<PriceCandidate> PriceCandidates(Prng random, int round)
    {
        var candidates = new List<PriceCandidate>();

        foreach (var slot in Enumerable.Range(0, 1 + random.Next(5)))
        {
            var from = new DateOnly(2026, 1 + random.Next(12), 1 + random.Next(28));

            candidates.Add(new PriceCandidate(
                Id(round, slot),
                random.Next(2) == 0 ? PriceScope.Channel : PriceScope.Outlet,
                "EUR",
                from,
                random.Next(3) == 0 ? null : from.AddDays(1 + random.Next(180)),
                random.Next(10_000) / 100m));
        }

        return candidates;
    }

    private static List<PromotionCandidate> PromotionCandidates(Prng random, int round)
    {
        var candidates = new List<PromotionCandidate>();

        foreach (var slot in Enumerable.Range(0, 1 + random.Next(5)))
        {
            var from = new DateOnly(2026, 1 + random.Next(12), 1 + random.Next(28));
            var to = random.Next(3) == 0 ? null : (DateOnly?)from.AddDays(1 + random.Next(180));
            var priority = random.Next(5) * 10 - 10;

            candidates.Add(random.Next(4) switch
            {
                0 => new PromotionCandidate(
                    Id(round, slot), PromotionType.PercentOff, priority, from, to, PercentOff: 15m),

                1 => new PromotionCandidate(
                    Id(round, slot), PromotionType.FixedAmountOff, priority, from, to,
                    AmountOff: 2.5m, Currency: "EUR"),

                2 => new PromotionCandidate(
                    Id(round, slot), PromotionType.VolumeTiered, priority, from, to,
                    Tiers:
                    [
                        .. new[] { 2, 6, 12, 24 }
                            .Take(1 + random.Next(4))
                            .Select(min => new PromotionTierCandidate(min, min * 0.5m, null, null)),
                    ]),

                _ => new PromotionCandidate(
                    Id(round, slot), PromotionType.BuyXGetY, priority, from, to,
                    Bundle: new BundleCandidate(1 + random.Next(6), 1, 100m, null)),
            });
        }

        return candidates;
    }

    /// <summary>Ids spread across the first four bytes, so the tiebreak meets pairs a little-endian byte array would order backwards.</summary>
    private static Guid Id(int round, int slot) =>
        new($"{(uint)(round * 2654435761 + slot):x8}-0000-7000-8000-{round:x8}{slot:x4}");

    private static List<T> Shuffle<T>(Prng random, List<T> source)
    {
        var shuffled = new List<T>(source);

        for (var index = shuffled.Count - 1; index > 0; index--)
        {
            var swap = random.Next(index + 1);
            (shuffled[index], shuffled[swap]) = (shuffled[swap], shuffled[index]);
        }

        return shuffled;
    }

    /// <summary>The generator's PRNG, for the same stability reason — see <see cref="VectorGenerator"/>.</summary>
    private sealed class Prng(ulong seed)
    {
        private ulong _state = seed == 0 ? 0x9E3779B97F4A7C15 : seed;

        public int Next(int bound)
        {
            _state ^= _state >> 12;
            _state ^= _state << 25;
            _state ^= _state >> 27;

            return (int)(_state * 0x2545F4914F6CDD1D % (ulong)bound);
        }
    }
}
