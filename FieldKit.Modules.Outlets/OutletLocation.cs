namespace FieldKit.Modules.Outlets;

/// <summary>
/// Where an outlet is, as a postal address (<c>OUT-01</c>).
/// </summary>
/// <remarks>
/// Structured rather than one free-text block, because the parts get used separately: postal code
/// and city are what territory membership rules key off (<c>ORG-07</c>), and a single string would
/// make those rules parse prose. Every part is optional — an address that must be complete before it
/// can be recorded means a half-known outlet cannot be recorded at all, and onboarding data is
/// routinely half-known.
/// </remarks>
public sealed record Address(string? Street, string? City, string? PostalCode, string? CountryCode);

/// <summary>
/// An outlet's position on the earth, used by journey planning and geofenced check-in.
/// </summary>
/// <remarks>
/// A pair, not two loose columns: a latitude without a longitude is not a partially-known location,
/// it is a broken one. Making them one value means the model cannot express that state.
/// </remarks>
public sealed record GeoPoint(double Latitude, double Longitude)
{
    /// <summary>Whether this point is on the earth. See <see cref="TenantOutletSettings"/> for when it is checked.</summary>
    public bool IsWithinRange() =>
        Latitude is >= -90 and <= 90 && Longitude is >= -180 and <= 180;
}

/// <summary>
/// A person at the outlet — store manager, buyer (<c>OUT-01</c>).
/// </summary>
/// <remarks>
/// <b>Personal data</b> under <c>B8</c>. It is tenant-isolated like everything else and carries no
/// more than a rep needs to walk in and ask for someone by name. Right-to-erasure is handled at the
/// IAM level for FieldKit users; erasure for outlet contacts is <c>OUT-10</c> and not built —
/// today, removing a contact from the outlet is the only deletion path, and it is a real delete
/// rather than a flag.
/// </remarks>
public sealed record OutletContact(string Name, string? Role, string? Phone, string? Email);
