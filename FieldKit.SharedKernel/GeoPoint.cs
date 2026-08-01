namespace FieldKit.SharedKernel;

/// <summary>A WGS-84 geographic point. Used for outlet location and visit check-in.</summary>
public readonly record struct GeoPoint
{
    public double Latitude { get; }
    public double Longitude { get; }

    public GeoPoint(double latitude, double longitude)
    {
        if (latitude is < -90 or > 90)
            throw new ArgumentOutOfRangeException(nameof(latitude), latitude, "Latitude must be within [-90, 90].");
        if (longitude is < -180 or > 180)
            throw new ArgumentOutOfRangeException(nameof(longitude), longitude, "Longitude must be within [-180, 180].");

        Latitude = latitude;
        Longitude = longitude;
    }

    public override string ToString() => $"({Latitude}, {Longitude})";
}
