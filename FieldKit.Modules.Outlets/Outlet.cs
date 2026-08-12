using System.Text.Json.Serialization;
using System.Text.Json;
using FieldKit.BuildingBlocks;
using FieldKit.SharedKernel;

namespace FieldKit.Modules.Outlets;

/// <summary>
/// Where an outlet is in its life (<c>OUT-04</c>).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<OutletStatus>))]
public enum OutletStatus
{
    /// <summary>Visited normally.</summary>
    Active = 0,

    /// <summary>Temporarily not visited — refurbishment, a seasonal closure, a dispute.</summary>
    Inactive = 1,

    /// <summary>Permanently closed. Excluded from new journeys, keeps its history (BR-OUT-4).</summary>
    Closed = 2,
}

/// <summary>
/// A retail location — the master data the field app is organized around (<c>OUT-01</c>).
/// </summary>
public sealed class Outlet : AggregateRoot, ITenantOwned, IAuditable, ISyncTracked
{
    /// <summary>
    /// Set by the row-version interceptor, never by this aggregate (ADR-0013). It is the first
    /// entity to carry one because it is the first the device pulls (W8 slice 3).
    /// </summary>
    public long RowVersion { get; set; }

    private readonly List<OutletContact> _contacts = [];
    private Dictionary<string, JsonElement> _customFields = [];

    public Guid Id { get; private set; }

    /// <summary>
    /// The tenant's own identifier for the location — what it is called in their ERP.
    /// </summary>
    /// <remarks>
    /// Unique within the tenant and supplied rather than generated: outlets arrive from an existing
    /// system, and an import that invented codes would be unable to say whether it had seen a
    /// location before.
    /// </remarks>
    public string Code { get; private set; } = null!;

    public string Name { get; private set; } = null!;

    /// <summary>Mandatory (BR-OUT-1) — it decides assortment, pricing and the visit workflow.</summary>
    public Guid ChannelId { get; private set; }

    /// <summary>A finer grade — A/B/C by volume. Free text until something branches on it.</summary>
    public string? Segment { get; private set; }

    /// <summary>The retail group this location belongs to, if any.</summary>
    public string? Banner { get; private set; }

    public OutletStatus Status { get; private set; }

    public Address? Address { get; private set; }

    /// <summary>Latitude, or null. Never set without <see cref="Longitude"/> — see <see cref="Location"/>.</summary>
    public double? Latitude { get; private set; }

    /// <summary>Longitude, or null.</summary>
    public double? Longitude { get; private set; }

    /// <summary>
    /// Where the outlet is, or null. Optional: journey planning and geofenced check-in need it,
    /// recording an outlet does not, and onboarding data routinely arrives without it.
    /// </summary>
    /// <remarks>
    /// Stored as two nullable columns and composed here rather than mapped as an owned type, because
    /// <see cref="GeoPoint"/> is a struct and EF owns only reference types. The half-set state that
    /// arrangement would otherwise allow is forbidden by a check constraint — a stronger guarantee
    /// than the owned-type mapping gave, since it holds against anything that writes the table.
    /// </remarks>
    public GeoPoint? Location =>
        Latitude is { } latitude && Longitude is { } longitude ? new GeoPoint(latitude, longitude) : null;

    /// <summary>
    /// The IANA zone this outlet trades in — <c>Europe/Bucharest</c>, not an offset.
    /// </summary>
    /// <remarks>
    /// Required, and explicit rather than derived from <see cref="Location"/>. A visit's business
    /// "day" and a promotion's validity both resolve here (BR-PRD-6), a rep may cross zones during a
    /// shift, and an offset would be wrong twice a year. Deriving it on the device would make the
    /// answer depend on which device asked.
    /// </remarks>
    public string TimeZoneId { get; private set; } = null!;

    /// <summary>People at the outlet. <b>Personal data</b> — see <see cref="OutletContact"/>.</summary>
    public IReadOnlyList<OutletContact> Contacts => _contacts;

    /// <summary>
    /// Tenant-defined values, validated against the Configuration module's catalogue (<c>OUT-02</c>,
    /// <c>CFG-02</c>, ADR-0009).
    /// </summary>
    /// <remarks>
    /// JSONB rather than EAV tables: a read stays one row, and Postgres can index a hot custom field
    /// with a GIN or expression index if one turns out to matter. What is allowed in here is not this
    /// module's decision — it is whatever the tenant has defined, which is why the values arrive
    /// already validated rather than being parsed on the way in.
    /// </remarks>
    public IReadOnlyDictionary<string, JsonElement> CustomFields => _customFields;

    public TenantId TenantId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset? ModifiedAtUtc { get; set; }
    public string? ModifiedBy { get; set; }

    private Outlet() { } // EF

    public static Outlet Create(
        string code,
        string name,
        Guid channelId,
        string? segment,
        string? banner,
        string timeZoneId,
        Address? address,
        GeoPoint? location,
        IEnumerable<OutletContact>? contacts,
        IReadOnlyDictionary<string, JsonElement>? customFields)
    {
        var outlet = new Outlet
        {
            Id = Guid.CreateVersion7(),
            Code = code,
            Name = name,
            ChannelId = channelId,
            Segment = segment,
            Banner = banner,
            Status = OutletStatus.Active,
            TimeZoneId = timeZoneId,
            Address = Canonical(address),
            Latitude = location?.Latitude,
            Longitude = location?.Longitude,
        };

        outlet.SetContacts(contacts);
        outlet.SetCustomFields(customFields);
        return outlet;
    }

    /// <summary>
    /// Updates the details. Deliberately does not touch <see cref="Status"/> — see
    /// <see cref="ChangeStatus"/>.
    /// </summary>
    /// <remarks>
    /// The code is not editable either. It is how an import recognises a location it has already
    /// seen, so letting an edit change it would make the next import create a duplicate rather than
    /// update the original.
    /// </remarks>
    public void Update(
        string name,
        Guid channelId,
        string? segment,
        string? banner,
        string timeZoneId,
        Address? address,
        GeoPoint? location,
        IEnumerable<OutletContact>? contacts,
        IReadOnlyDictionary<string, JsonElement>? customFields,
        IClock clock)
    {
        Name = name;
        ChannelId = channelId;
        Segment = segment;
        Banner = banner;
        TimeZoneId = timeZoneId;
        Address = Canonical(address);
        Latitude = location?.Latitude;
        Longitude = location?.Longitude;
        SetContacts(contacts);
        SetCustomFields(customFields);
        ModifiedAtUtc = clock.UtcNow;
    }

    /// <summary>
    /// Replaces the contact list wholesale.
    /// </summary>
    /// <remarks>
    /// Wholesale rather than add/remove deltas, for the same reason a role's permissions are: a delta
    /// requires the caller to know the current state, and two people editing the same outlet would
    /// silently interleave. It also gives erasure a trivial shape — an empty list removes every
    /// contact, and the rows are actually gone rather than flagged.
    /// </remarks>
    private void SetContacts(IEnumerable<OutletContact>? contacts)
    {
        _contacts.Clear();
        if (contacts is not null) _contacts.AddRange(contacts);
    }

    /// <summary>
    /// The address as it is stored: country code upper-cased, everything else untouched.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A lower-case country code made every product at the outlet untaxable.</b>
    /// <c>IOutletClassification</c> documents <c>CountryCode</c> as "ISO-3166-1 alpha-2, upper-cased",
    /// and Products' tax resolution compares it to <c>TaxRate.CountryCode</c> — which <i>is</i>
    /// upper-cased on the way in. So <c>'ro'</c> matched no rate, resolution returned no tax, and
    /// that is indistinguishable from a class nobody has set a rate for. Nothing errored; the shop
    /// was simply never taxed.
    /// </para>
    /// <para>
    /// Normalised here rather than at the endpoint because the endpoint is not the only door: the
    /// CSV importer builds an <see cref="Address"/> too, and a rule that only one caller applies is
    /// a rule the other caller breaks. The endpoint still <i>refuses</i> a code that is not two
    /// letters — a caller who typed "Romania" wants to hear about it, not to have it quietly
    /// truncated to "RO".
    /// </para>
    /// </remarks>
    private static Address? Canonical(Address? address) =>
        address?.CountryCode is null
            ? address
            : address with { CountryCode = address.CountryCode.ToUpperInvariant() };

    /// <summary>
    /// Replaces the custom-field values wholesale, like the contacts and for the same reason.
    /// </summary>
    /// <remarks>
    /// Wholesale also makes "this field was cleared" expressible at all: a patch cannot distinguish
    /// omitting a key from emptying it, so an optional field could never be unset once written.
    ///
    /// The elements are cloned because a <see cref="JsonElement"/> borrows the buffer of the document
    /// it came from — keeping the originals would leave the entity pointing into a request body that
    /// is disposed the moment the response is written.
    /// </remarks>
    private void SetCustomFields(IReadOnlyDictionary<string, JsonElement>? customFields) =>
        _customFields = customFields is null
            ? []
            : customFields.ToDictionary(entry => entry.Key, entry => entry.Value.Clone(), StringComparer.Ordinal);

    /// <summary>
    /// Moves the outlet through its lifecycle, or returns why it cannot (<c>OUT-04</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A status change is its own act, not a field on an edit form. "This store is shut" is a
    /// different decision from "the name was spelled wrong", and merging them means a careless PUT
    /// can close an outlet as a side effect of fixing a typo.
    /// </para>
    /// <para>
    /// <b><see cref="OutletStatus.Closed"/> is terminal.</b> That is what makes it mean anything
    /// beyond <see cref="OutletStatus.Inactive"/> — BR-OUT-4 excludes closed outlets from new
    /// journeys while keeping their history, and a status that can be walked back is just a
    /// long-lived inactive. A location that genuinely reopens is a new outlet with its own code,
    /// because its trading history as a different business should not silently continue.
    /// </para>
    /// </remarks>
    public string? ChangeStatus(OutletStatus status, IClock clock)
    {
        if (Status == OutletStatus.Closed)
        {
            return "A closed outlet cannot be reopened. Create a new outlet for the new location.";
        }

        if (Status == status) return null; // Idempotent: asking for what is already true is not an error.

        Status = status;
        ModifiedAtUtc = clock.UtcNow;

        return null;
    }
}
