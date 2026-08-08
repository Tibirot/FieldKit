using FieldKit.Modules.Visit;
using FieldKit.SharedKernel;

namespace FieldKit.Server.Tests;

/// <summary>
/// Whether a rep is at the outlet, and whether that needs explaining (<c>BR-VIS-2</c>) — W7 slice 7.
/// </summary>
/// <remarks>
/// No fixture and no database: <see cref="Geofencing"/> is pure, and the same rules have to run on a
/// device with no signal. This is also the contested rule of the module — "never block the rep" is
/// the kind of thing that gets quietly tightened — so it is pinned by writing down two coordinates
/// rather than by seeding a shop.
/// </remarks>
public class GeofencingTests
{
    /// <summary>A shop on Calea Dorobanți, Bucharest.</summary>
    private static readonly GeoPoint Outlet = new(44.4638, 26.0946);

    [Fact]
    public void Two_points_a_known_distance_apart_measure_that_distance()
    {
        // A degree of latitude is about 111 km anywhere on earth, which is the one check on this
        // formula that does not require trusting the formula.
        var oneDegreeNorth = new GeoPoint(Outlet.Latitude + 1, Outlet.Longitude);

        var metres = Geofencing.DistanceMetres(Outlet, oneDegreeNorth);

        Assert.InRange(metres, 111_000, 111_400);
    }

    [Fact]
    public void The_same_point_is_no_distance_from_itself()
    {
        Assert.Equal(0, Geofencing.DistanceMetres(Outlet, Outlet), 6);
    }

    [Fact]
    public void A_rep_standing_in_the_shop_needs_to_explain_nothing()
    {
        var assessment = Geofencing.Assess(Outlet, Outlet, radiusMetres: 150, presenceExpected: true);

        Assert.True(assessment.Inside);
        Assert.False(assessment.ReasonRequired);
        Assert.Equal(0, assessment.DistanceMetres!.Value, 6);
    }

    [Fact]
    public void A_rep_just_inside_the_radius_is_inside_it()
    {
        // ~100 m north. Consumer GPS is routinely tens of metres out, which is the whole reason the
        // radius is 150 rather than 20 — this is the case that would flag honest reps.
        var nearby = new GeoPoint(Outlet.Latitude + 0.0009, Outlet.Longitude);

        var assessment = Geofencing.Assess(nearby, Outlet, radiusMetres: 150, presenceExpected: true);

        Assert.True(assessment.Inside);
        Assert.False(assessment.ReasonRequired);
    }

    [Fact]
    public void A_rep_well_outside_it_has_to_say_why()
    {
        // ~2.2 km north — not a GPS wobble.
        var elsewhere = new GeoPoint(Outlet.Latitude + 0.02, Outlet.Longitude);

        var assessment = Geofencing.Assess(elsewhere, Outlet, radiusMetres: 150, presenceExpected: true);

        Assert.False(assessment.Inside);
        Assert.True(assessment.ReasonRequired);

        // The number is carried, not just the verdict: "eighty metres" and "two kilometres" are
        // different conversations, and a boolean flattens both into "flagged".
        Assert.InRange(assessment.DistanceMetres!.Value, 2_000, 2_400);
    }

    [Fact]
    public void A_channel_that_does_not_expect_presence_never_asks_for_a_reason()
    {
        // BR-VIS-2's assumption, and the reason IVisitWorkflow was built a slice early. A phone call
        // is legitimately not at the outlet, so demanding a reason would record an exception every
        // time — and a flag that fires on ordinary work is one supervisors learn to ignore.
        var elsewhere = new GeoPoint(Outlet.Latitude + 0.5, Outlet.Longitude);

        var assessment = Geofencing.Assess(elsewhere, Outlet, radiusMetres: 150, presenceExpected: false);

        Assert.False(assessment.ReasonRequired);
    }

    [Fact]
    public void An_outlet_nobody_has_placed_cannot_make_a_rep_justify_anything()
    {
        // Coordinates are optional on an outlet (OUT-01). "Were you there?" has no answer, and making
        // a rep explain a gap in master data would blame them for it.
        var assessment = Geofencing.Assess(Outlet, outlet: null, radiusMetres: 150, presenceExpected: true);

        Assert.False(assessment.ReasonRequired);
        Assert.Null(assessment.DistanceMetres);
    }

    [Fact]
    public void A_device_with_no_fix_at_a_placed_shop_does_have_to_explain_itself()
    {
        // The one case where nothing can be measured and a reason is still required. A rep whose
        // phone reports no position at a shop that has one is exactly what a supervisor wants to
        // see — and it is also how a check-in would be faked.
        var assessment = Geofencing.Assess(at: null, Outlet, radiusMetres: 150, presenceExpected: true);

        Assert.True(assessment.ReasonRequired);
        Assert.Null(assessment.DistanceMetres);
    }

    [Fact]
    public void Nothing_it_can_answer_is_a_refusal()
    {
        // BR-VIS-2 is emphatic: outside the geofence the visit is still allowed, it just has to be
        // explained. The strongest thing this type says is "a reason is required" — there is no
        // shape of input that produces "no".
        GeoPoint?[] positions = [null, Outlet, new GeoPoint(-33.8688, 151.2093)];
        GeoPoint?[] outlets = [null, Outlet];

        foreach (var at in positions)
        {
            foreach (var outlet in outlets)
            {
                foreach (var presence in new[] { true, false })
                {
                    var assessment = Geofencing.Assess(at, outlet, radiusMetres: 150, presenceExpected: presence);

                    // The assertion is that it returned at all, and that "inside" is never claimed
                    // without a measurement to back it.
                    Assert.False(assessment.Inside && assessment.DistanceMetres is null);
                }
            }
        }
    }
}
