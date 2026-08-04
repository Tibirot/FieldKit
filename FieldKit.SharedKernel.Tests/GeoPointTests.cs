namespace FieldKit.SharedKernel.Tests;

/// <summary>
/// The one definition of "is this a place on the earth".
/// </summary>
/// <remarks>
/// Worth testing here rather than through an endpoint: this type is shared by outlet location and
/// visit check-in, so a range that drifts would be wrong in two modules at once and would show up as
/// two unrelated bugs.
/// </remarks>
public class GeoPointTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(44.4638, 26.0946)]
    [InlineData(90, 180)]    // the poles and the antimeridian are real places
    [InlineData(-90, -180)]
    public void Accepts_points_on_the_earth(double latitude, double longitude)
    {
        Assert.True(GeoPoint.TryCreate(latitude, longitude, out var point));
        Assert.Equal(latitude, point.Latitude);
        Assert.Equal(longitude, point.Longitude);
    }

    [Theory]
    [InlineData(91, 0)]
    [InlineData(-91, 0)]
    [InlineData(0, 181)]
    [InlineData(0, -181)]
    [InlineData(double.NaN, 0)]        // NaN fails every comparison, so it must be excluded, not compared
    [InlineData(0, double.PositiveInfinity)]
    public void Rejects_everything_else(double latitude, double longitude)
    {
        Assert.False(GeoPoint.TryCreate(latitude, longitude, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GeoPoint(latitude, longitude));
    }

    [Fact]
    public void The_constructor_and_TryCreate_agree()
    {
        // Two ways in, one range. They are separate because domain code that has already established
        // a coordinate is good should not keep proving it, while a request handler needs to answer
        // with a 400 rather than an exception — but if they ever disagreed, one of the two callers
        // would be enforcing a rule the other does not.
        foreach (var (latitude, longitude) in new[] { (90.0, 180.0), (90.1, 180.0), (0.0, -180.1) })
        {
            var accepted = GeoPoint.TryCreate(latitude, longitude, out _);
            var threw = Record.Exception(() => new GeoPoint(latitude, longitude)) is not null;

            Assert.Equal(accepted, !threw);
        }
    }
}
