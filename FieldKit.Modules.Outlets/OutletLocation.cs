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
public sealed record Address(
    string? Street = null, string? City = null, string? PostalCode = null, string? CountryCode = null)
{
    /// <summary>Two ASCII letters, or absent. Anything else never matches a tax rate.</summary>
    /// <remarks>
    /// Here rather than in the endpoint that first needed it, because an outlet has two doors: the
    /// API and the CSV import. The import wrote whatever the spreadsheet held, which after
    /// upper-casing meant a cell reading "Romania" arrived at a <c>varchar(2)</c> column as
    /// "ROMANIA" — the same <c>DbUpdateException</c>-shaped <c>500</c> this rule exists to prevent,
    /// reached by the door nobody checked.
    /// </remarks>
    public static bool IsCountryCode(string? countryCode) =>
        countryCode is null || (countryCode.Length == 2 && countryCode.All(char.IsAsciiLetter));
}

/// <summary>
/// Coordinates as they arrive from a caller — untrusted, and not yet known to be a real place.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately separate from <see cref="FieldKit.SharedKernel.GeoPoint"/>, which is the validated
/// domain value and refuses to exist outside <c>[-90, 90]</c> / <c>[-180, 180]</c>. Binding the
/// request straight onto that type would make an out-of-range latitude throw inside JSON
/// deserialization — before any handler runs, so the caller gets a framework error instead of a
/// message naming the field.
/// </para>
/// <para>
/// This is the only place the two shapes differ: the wire format is identical, so a client sees
/// <c>{ "latitude": …, "longitude": … }</c> either way.
/// </para>
/// </remarks>
public sealed record Coordinates(double Latitude, double Longitude);

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
public sealed record OutletContact(
    string Name, string? Role = null, string? Phone = null, string? Email = null);
