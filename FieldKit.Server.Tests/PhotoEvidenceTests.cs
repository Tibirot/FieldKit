using FieldKit.Modules.Audit;
using FieldKit.Modules.Audit.Contracts;
using FieldKit.Modules.Configuration.Contracts;

namespace FieldKit.Server.Tests;

/// <summary>
/// Telling a photograph that is late from one that is not coming (<c>OFF-08</c>, <c>B5</c>) —
/// W11 slice 13a.
/// </summary>
/// <remarks>
/// <para>
/// <b>Derived on read, which is what these cases are about.</b> Nothing stores "missing": the state
/// is the confirmation timestamp read against the audit's age, so a rep who finds signal on Monday
/// for Friday's photograph needs no job to undo a flag somebody's reconciler set over the weekend.
/// </para>
/// <para>
/// The confirmation itself, over HTTP and across tenants, is <see cref="PhotoConfirmTests"/>'s.
/// </para>
/// </remarks>
public class PhotoEvidenceTests
{
    private static readonly Guid Visit = Guid.CreateVersion7();
    private static readonly Guid Outlet = Guid.CreateVersion7();
    private static readonly DateTimeOffset Captured = new(2026, 4, 6, 9, 30, 0, TimeSpan.Zero);

    private static readonly string Key = $"audits/{Guid.CreateVersion7()}/{Guid.CreateVersion7()}.jpg";

    private static Modules.Audit.Audit Audited() =>
        Modules.Audit.Audit.Record(
            new CapturedAudit(
                Guid.CreateVersion7(), Visit, Captured, 3, 40, [], [], [],
                Photos: [new CapturedPhoto(AuditSection.ShareOfShelf, Key)]),
            Outlet,
            "rep-1",
            [new PillarWeight(ScorePillar.Availability, 100m)]).Audit!;

    [Fact]
    public void A_fresh_photograph_is_expected_rather_than_missing()
    {
        // The ordinary state of a just-filed audit, and the reason a bool would not do: the push
        // usually beats the upload, so every audit is briefly a set of references to objects that
        // are still on a phone. Nothing is wrong here and nobody should be told anything.
        var photo = Audited().Describe(Captured.AddMinutes(1)).Photos.Single();

        Assert.Equal(PhotoEvidenceState.Expected, photo.State);
        Assert.Null(photo.UploadedAtUtc);
    }

    [Fact]
    public void A_photograph_that_stopped_coming_reads_as_missing()
    {
        // A week and a day. A device that was going to upload has had every reconnect in between,
        // so this is a gap in the evidence rather than a slow morning.
        var photo = Audited()
            .Describe(Captured + PhotoLine.ExpectedWithin + TimeSpan.FromDays(1))
            .Photos.Single();

        Assert.Equal(PhotoEvidenceState.Missing, photo.State);
    }

    [Fact]
    public void The_threshold_is_a_boundary_rather_than_a_range()
    {
        /*
         * Exactly on the threshold is still expected, one tick past it is not.
         *
         * Asserted because "roughly a week" is not a rule anybody can implement twice the same way,
         * and because an off-by-one here is invisible: it would move the moment a photograph starts
         * looking lost by a day, which nothing else in the system would notice.
         */
        var audit = Audited();

        Assert.Equal(
            PhotoEvidenceState.Expected,
            audit.Describe(Captured + PhotoLine.ExpectedWithin).Photos.Single().State);

        Assert.Equal(
            PhotoEvidenceState.Missing,
            audit.Describe(Captured + PhotoLine.ExpectedWithin + TimeSpan.FromTicks(1))
                .Photos.Single().State);
    }

    [Fact]
    public void A_confirmed_photograph_stays_arrived_however_old_the_audit_gets()
    {
        /*
         * The case that makes deriving on read safe.
         *
         * A confirmation is a fact about an object in storage, and age cannot unmake it — an audit
         * from last quarter whose photographs all landed is complete evidence, not stale evidence.
         * Getting this wrong would turn every old audit in the system into a false alarm.
         *
         * Against the rule rather than a stored audit, because `Confirm` is the aggregate's own and
         * stays that way: what a test needs here is the derivation, and widening a write surface to
         * reach it would be paying in design for a convenience. Confirming for real is
         * `PhotoConfirmTests`.
         */
        Assert.Equal(
            PhotoEvidenceState.Arrived,
            PhotoLine.StateOf(Captured.AddHours(2), Captured, Captured.AddYears(1)));
    }
}
