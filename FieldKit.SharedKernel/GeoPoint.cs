namespace FieldKit.SharedKernel;

/// <summary>A WGS-84 geographic point. Used for outlet location and visit check-in.</summary>
public readonly record struct GeoPoint
{
    public double Latitude { get; }
    public double Longitude { get; }

    public GeoPoint(double latitude, double longitude)
    {
        // Phrased as "must be within" rather than "must not be outside", which is not the same test
        // for a double: NaN fails every comparison, so `is < -90 or > 90` is false for it and a NaN
        // latitude would have been accepted and stored. Infinity is excluded by the same change.
        if (latitude is not (>= -90 and <= 90))
            throw new ArgumentOutOfRangeException(nameof(latitude), latitude, "Latitude must be within [-90, 90].");
        if (longitude is not (>= -180 and <= 180))
            throw new ArgumentOutOfRangeException(nameof(longitude), longitude, "Longitude must be within [-180, 180].");

        Latitude = latitude;
        Longitude = longitude;
    }

    /// <summary>
    /// Creates a point from untrusted input, or reports that it is not a place.
    /// </summary>
    /// <remarks>
    /// The constructor throws on purpose — domain code that has already established a coordinate is
    /// good should not have to keep proving it. This is the other half: request handlers receive
    /// arbitrary numbers and need to answer with a 400 naming the field, not an exception. One range
    /// definition, two ways in.
    /// </remarks>
    public static bool TryCreate(double latitude, double longitude, out GeoPoint point)
    {
        if (latitude is not (>= -90 and <= 90) || longitude is not (>= -180 and <= 180))
        {
            point = default;
            return false;
        }

        point = new GeoPoint(latitude, longitude);
        return true;
    }

    public override string ToString() => $"({Latitude}, {Longitude})";
}
