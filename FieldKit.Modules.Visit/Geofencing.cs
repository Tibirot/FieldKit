using FieldKit.SharedKernel;

namespace FieldKit.Modules.Visit;

/// <summary>What a check-in's position means, before anything is written down.</summary>
/// <param name="DistanceMetres">
/// How far the rep was from the outlet, or null when that could not be worked out — see
/// <see cref="Geofencing.Assess"/>.
/// </param>
public sealed record GeofenceAssessment(bool Inside, double? DistanceMetres, bool ReasonRequired);

/// <summary>
/// Whether a rep checking in is at the outlet, and whether that needs explaining
/// (<c>VIS-01</c>, <c>VIS-02</c>, <c>BR-VIS-2</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Pure</b>, like <c>PriceResolver</c> and <c>JourneyGenerator</c>: two positions, a radius and a
/// policy flag in, one assessment out. The same rules have to run on a device that is offline
/// (<c>§7</c>), so they cannot be entangled with a database — and a rule about where somebody is
/// standing is exactly the kind that is argued about, which means it has to be testable by writing
/// down two coordinates.
/// </para>
/// <para>
/// <b>Never blocks.</b> <c>BR-VIS-2</c> is emphatic: outside the geofence the visit is still
/// allowed, it just has to be explained. Nothing here returns "no" — the strongest thing it says is
/// <see cref="GeofenceAssessment.ReasonRequired"/>, and refusing a check-in that arrives without one
/// is the endpoint asking for the sentence, not the rule turning a rep away from a shop.
/// </para>
/// </remarks>
public static class Geofencing
{
    /// <summary>Mean Earth radius, in metres — the sphere the haversine formula assumes.</summary>
    private const double EarthRadiusMetres = 6_371_000;

    /// <summary>
    /// Assesses a check-in position.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three things make a reason unnecessary, and they are different in kind:
    /// </para>
    /// <list type="bullet">
    /// <item><b>The rep is inside the radius.</b> Nothing to explain.</item>
    /// <item><b>The channel does not expect presence</b> (<c>IVisitWorkflow</c>). A phone call is
    /// legitimately not at the outlet, and demanding a reason would record an exception where
    /// nothing exceptional happened — the assumption <c>BR-VIS-2</c> spells out.</item>
    /// <item><b>The outlet has no coordinates.</b> Nobody has placed the shop, so "were you there?"
    /// has no answer — and making a rep justify a gap in master data would blame them for it. The
    /// visit records what it knows: a position, and no distance.</item>
    /// </list>
    /// <para>
    /// The device having no fix is the one case that <i>does</i> need explaining even though nothing
    /// can be measured: a rep whose phone reports no position at a shop that has one is exactly the
    /// case a supervisor would want to see, and it is also how a check-in would be faked.
    /// </para>
    /// </remarks>
    /// <param name="at">Where the rep says they are, or null when the device had no fix.</param>
    /// <param name="outlet">Where the outlet is, or null when nobody has placed it.</param>
    /// <param name="radiusMetres">How close counts as there.</param>
    /// <param name="presenceExpected">Whether being at the outlet is part of this visit at all.</param>
    public static GeofenceAssessment Assess(
        GeoPoint? at, GeoPoint? outlet, int radiusMetres, bool presenceExpected)
    {
        if (!presenceExpected) return new GeofenceAssessment(Inside: false, null, ReasonRequired: false);

        if (outlet is null) return new GeofenceAssessment(Inside: false, null, ReasonRequired: false);

        if (at is not { } here) return new GeofenceAssessment(Inside: false, null, ReasonRequired: true);

        var distance = DistanceMetres(here, outlet.Value);
        var inside = distance <= radiusMetres;

        return new GeofenceAssessment(inside, distance, ReasonRequired: !inside);
    }

    /// <summary>
    /// Great-circle distance between two points, in metres.
    /// </summary>
    /// <remarks>
    /// Haversine on a sphere. Wrong by up to about half a percent against the real, slightly squashed
    /// Earth — twenty-odd centimetres over the distances this compares, against a GPS fix that is
    /// routinely tens of metres out. Reaching for Vincenty here would be precision spent on the one
    /// term that is already the smallest source of error.
    /// </remarks>
    public static double DistanceMetres(GeoPoint from, GeoPoint to)
    {
        var lat1 = double.DegreesToRadians(from.Latitude);
        var lat2 = double.DegreesToRadians(to.Latitude);
        var deltaLat = lat2 - lat1;
        var deltaLon = double.DegreesToRadians(to.Longitude - from.Longitude);

        var a = (Math.Sin(deltaLat / 2) * Math.Sin(deltaLat / 2))
            + (Math.Cos(lat1) * Math.Cos(lat2) * Math.Sin(deltaLon / 2) * Math.Sin(deltaLon / 2));

        return 2 * EarthRadiusMetres * Math.Asin(Math.Min(1, Math.Sqrt(a)));
    }
}
