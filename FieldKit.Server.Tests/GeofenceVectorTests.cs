using System.Text.Json;
using FieldKit.Modules.Visit;
using FieldKit.SharedKernel;

namespace FieldKit.Server.Tests;

/// <summary>
/// Runs the shared geofence vectors against the C# engine (<c>VIS-01</c>, <c>VIS-02</c>,
/// <c>PRD-08</c>'s regime) — W9 slice 3.
/// </summary>
/// <remarks>
/// <para>
/// The fourth rule this repository implements twice, and the first that is not about money. It has
/// to run on the device because a rep in a shop with no signal still has to be told whether they are
/// inside the fence — and it has to <i>agree</i> because
/// <see cref="FieldKit.Modules.Visit.Contracts.CapturedVisit"/> carries the device's verdict and the
/// server stores it unmodified.
/// </para>
/// <para>
/// <b>That makes disagreement worse here than in pricing.</b> A price is recomputed server-side when
/// an order is placed, so a mirror that drifts is caught by the next order. This is never recomputed
/// by design: re-judging a visit against today's radius would reclassify a rep who was legitimately
/// inside yesterday's. A device that decides "outside" writes a supervisor an exception that never
/// happened, and nothing downstream can tell.
/// </para>
/// <para>
/// <b>Distances are compared with a tolerance and verdicts are not.</b> Every step of the haversine
/// is IEEE-754 double arithmetic except three: <c>sin</c>, <c>cos</c> and <c>asin</c> are not
/// required to be correctly rounded, and .NET and V8 do not use the same implementations — so two
/// correct engines may differ in the last bit or two. <see cref="ToleranceMetres"/> is a micron,
/// which is ten orders of magnitude below the GPS error this rule exists to tolerate and several
/// above the disagreement doubles can produce at these magnitudes. The booleans are exact because
/// they are the answer; the distance is evidence attached to it.
/// </para>
/// </remarks>
public class GeofenceVectorTests
{
    /// <summary>
    /// How far apart two correct implementations may land, in metres.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A micron. The largest case here is antipodal — about 20,000 km — where one ULP of a
    /// <c>double</c> is roughly 4 nanometres, so this leaves nearly three orders of magnitude of
    /// headroom for library differences while being far too small to hide a real disagreement: the
    /// smallest bug worth catching moves a distance by metres, not microns.
    /// </para>
    /// <para>
    /// <b>Measured, not assumed — and the measurement is not the justification.</b> On the machine
    /// these expectations were computed on, .NET and V8 agreed to the last bit: the suite passes at
    /// <c>1e-12</c>. The tolerance is not there for that pair. It is there for the pairs nobody has
    /// measured — CI's Linux runtime, a rep's phone — where <c>sin</c>, <c>cos</c> and <c>asin</c>
    /// come from different libraries and are not required to be correctly rounded. Pinning exact
    /// equality would be pinning a coincidence observed on one machine.
    /// </para>
    /// </remarks>
    private const double ToleranceMetres = 1e-6;

    private static readonly VectorFile File = Load();

    public static TheoryData<string> Cases()
    {
        var data = new TheoryData<string>();
        foreach (var vector in File.Assessment) data.Add(vector.Name);
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void The_engine_agrees_with_the_shared_vector(string name)
    {
        var vector = File.Assessment.Single(candidate => candidate.Name == name);

        var assessment = Geofencing.Assess(
            Point(vector.At), Point(vector.Outlet), vector.RadiusMetres, vector.PresenceExpected);

        Assert.Equal(vector.Expected.Inside, assessment.Inside);
        Assert.Equal(vector.Expected.ReasonRequired, assessment.ReasonRequired);

        if (vector.Expected.DistanceMetres is null)
        {
            // Not the same as "zero", and the file has cases for both. A visit with no measurable
            // distance and one taken at the pin are different records.
            Assert.Null(assessment.DistanceMetres);
            return;
        }

        Assert.NotNull(assessment.DistanceMetres);
        Assert.Equal(vector.Expected.DistanceMetres.Value, assessment.DistanceMetres.Value, ToleranceMetres);
    }

    [Fact]
    public void The_file_carries_the_version_this_suite_was_written_against()
    {
        // A file whose cases changed meaning bumps its version, so a suite running an older one
        // fails loudly rather than quietly proving yesterday's rule (vectors/README.md).
        Assert.Equal(1, File.Version);
    }

    private static GeoPoint? Point(VectorPoint? point) =>
        point is null ? null : new GeoPoint(point.Latitude, point.Longitude);

    private static VectorFile Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "vectors", "visits", "geofence.v1.json");

        return JsonSerializer.Deserialize<VectorFile>(
                   System.IO.File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web))
               ?? throw new InvalidOperationException($"{path} deserialized to null");
    }

    private sealed record VectorFile(int Version, IReadOnlyList<AssessmentVector> Assessment);

    private sealed record AssessmentVector(
        string Name,
        VectorPoint? At,
        VectorPoint? Outlet,
        int RadiusMetres,
        bool PresenceExpected,
        ExpectedAssessment Expected);

    private sealed record VectorPoint(double Latitude, double Longitude);

    private sealed record ExpectedAssessment(bool Inside, double? DistanceMetres, bool ReasonRequired);
}
