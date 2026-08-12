using FieldKit.Modules.Visit.Contracts;

namespace FieldKit.Server.Tests;

/// <summary>
/// Whether a visit was open at a moment (<c>BR-AUD-6</c>, <c>BR-VIS-4</c>) — W11 slice 8d.
/// </summary>
/// <remarks>
/// <para>
/// Pure, so no fixture and no database: the whole point of putting this on <see cref="VisitFacts"/>
/// was that Audit and Order stop each carrying their own comparison. The two ingest suites cover
/// what each module <i>does</i> with the answer; this covers the answer.
/// </para>
/// <para>
/// <b>The boundary is inclusive, and that is the case the bug turned on.</b> An offline order sealed
/// in the same second the rep checked out is the ordinary end of a call, not work smuggled in after
/// the fact — and a device rounds both timestamps from the same clock.
/// </para>
/// </remarks>
public class VisitFactsTests
{
    private static readonly DateTimeOffset Noon = new(2026, 8, 13, 12, 0, 0, TimeSpan.Zero);

    private static VisitFacts Open() =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), "rep", Sealed: false, CheckedOutAtUtc: null);

    private static VisitFacts SealedAt(DateTimeOffset? at) =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), "rep", Sealed: true, CheckedOutAtUtc: at);

    [Fact]
    public void An_open_visit_was_open_at_every_moment()
    {
        // Including one a device claims is in the future. `Visit` already refuses a check-out later
        // than the push; a second opinion here would be two modules judging the same clock.
        Assert.True(Open().WasOpenAt(Noon));
        Assert.True(Open().WasOpenAt(Noon.AddYears(10)));
    }

    [Fact]
    public void Work_captured_before_the_seal_finds_the_visit_open()
    {
        /*
         * The case that was broken, and it is not an edge: a pushed `CapturedVisit` is created
         * already sealed and a device only enqueues one at check-out, so *every* offline order and
         * audit arrives at a visit sealed after the work was done.
         */
        Assert.True(SealedAt(Noon).WasOpenAt(Noon.AddHours(-1)));
    }

    [Fact]
    public void Work_captured_in_the_same_moment_as_the_seal_finds_it_open()
    {
        // Inclusive on purpose — an order sealed as the rep walks out is the ordinary end of a call.
        Assert.True(SealedAt(Noon).WasOpenAt(Noon));
    }

    [Fact]
    public void Work_captured_after_the_seal_finds_it_closed()
    {
        // The rule that survives `BR-AUD-6`: a measurement taken after the visit was filed would
        // change a record already counted.
        Assert.False(SealedAt(Noon).WasOpenAt(Noon.AddSeconds(1)));
    }

    [Fact]
    public void Sealed_with_no_moment_is_closed_at_every_moment()
    {
        // A row that should not exist. Refusing is the safe reading, because nothing about it can
        // prove the work came first.
        Assert.False(SealedAt(null).WasOpenAt(Noon));
        Assert.False(SealedAt(null).WasOpenAt(Noon.AddYears(-10)));
    }
}
