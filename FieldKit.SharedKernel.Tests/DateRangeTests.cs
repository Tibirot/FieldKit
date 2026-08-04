namespace FieldKit.SharedKernel.Tests;

/// <summary>
/// Interval logic, tested here rather than through an endpoint.
/// </summary>
/// <remarks>
/// BR-ORG-2 ("overlapping assignments are rejected") is this method and nothing else. The edge cases
/// are the ones that matter — touching ranges, containment, two open ends — and each would need its
/// own HTTP round trip to reach from the outside.
/// </remarks>
public class DateRangeTests
{
    private static DateOnly D(int day) => new(2026, 3, day);

    private static DateRange Range(int from, int? to) => new(D(from), to is { } t ? D(t) : null);

    [Fact]
    public void A_range_cannot_end_before_it_starts()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DateRange(D(10), D(9)));
        Assert.False(DateRange.TryCreate(D(10), D(9), out _));

        // One day long is a range, not a mistake.
        Assert.True(DateRange.TryCreate(D(10), D(10), out _));
    }

    [Theory]
    [InlineData(10, true)]   // the first day is inside
    [InlineData(20, true)]   // …and so is the last: both ends are inclusive
    [InlineData(15, true)]
    [InlineData(9, false)]
    [InlineData(21, false)]
    public void Contains_is_inclusive_at_both_ends(int day, bool expected) =>
        Assert.Equal(expected, Range(10, 20).Contains(D(day)));

    [Fact]
    public void An_open_ended_range_contains_everything_after_it_starts()
    {
        var open = Range(10, null);

        Assert.True(open.Contains(D(10)));
        Assert.True(open.Contains(new DateOnly(2099, 1, 1)));
        Assert.False(open.Contains(D(9)));
    }

    [Theory]
    // Identical, contained, and partially covering — all overlap.
    [InlineData(10, 20, 10, 20, true)]
    [InlineData(10, 20, 12, 15, true)]
    [InlineData(10, 20, 15, 25, true)]
    [InlineData(10, 20, 5, 15, true)]
    // Touching at a single day still overlaps, because both ends are inclusive. This is the case a
    // half-open implementation gets wrong, and it is exactly what BR-ORG-2 must reject: two reps
    // covering the same territory on the same day.
    [InlineData(10, 20, 20, 25, true)]
    [InlineData(10, 20, 5, 10, true)]
    // Adjacent but not touching — the handover case, which must be allowed.
    [InlineData(10, 20, 21, 25, false)]
    [InlineData(10, 20, 5, 9, false)]
    public void Overlaps_covers_the_closed_cases(int aFrom, int aTo, int bFrom, int bTo, bool expected)
    {
        var a = Range(aFrom, aTo);
        var b = Range(bFrom, bTo);

        Assert.Equal(expected, a.Overlaps(b));
        Assert.Equal(expected, b.Overlaps(a)); // symmetry is not optional for this rule
    }

    [Theory]
    [InlineData(10, null, 25, null, true)]   // two open ends always meet eventually
    [InlineData(10, 20, 25, null, false)]    // open end starts after the closed one finishes
    [InlineData(10, 20, 15, null, true)]
    [InlineData(10, null, 5, 9, false)]      // closed range ends before the open one begins
    [InlineData(10, null, 5, 10, true)]
    public void Overlaps_treats_an_open_end_as_infinity(int aFrom, int? aTo, int bFrom, int? bTo, bool expected)
    {
        var a = Range(aFrom, aTo);
        var b = Range(bFrom, bTo);

        Assert.Equal(expected, a.Overlaps(b));
        Assert.Equal(expected, b.Overlaps(a));
    }

    [Fact]
    public void A_range_overlaps_itself()
    {
        // Trivial, and the reason it is here: it is the assertion that fails first if the comparison
        // operators are ever flipped to exclusive.
        Assert.True(Range(10, 20).Overlaps(Range(10, 20)));
        Assert.True(Range(10, null).Overlaps(Range(10, null)));
    }
}
