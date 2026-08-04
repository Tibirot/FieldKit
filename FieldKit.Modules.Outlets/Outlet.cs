using FieldKit.BuildingBlocks;
using FieldKit.SharedKernel;

namespace FieldKit.Modules.Outlets;

/// <summary>
/// Where an outlet is in its life (<c>OUT-04</c>).
/// </summary>
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
public sealed class Outlet : AggregateRoot, ITenantOwned, IAuditable
{
    private readonly List<OutletContact> _contacts = [];

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

    /// <summary>
    /// Optional. Journey planning and geofenced check-in need it; recording an outlet does not, and
    /// onboarding data routinely arrives without it.
    /// </summary>
    public GeoPoint? Location { get; private set; }

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
        IEnumerable<OutletContact>? contacts)
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
            Address = address,
            Location = location,
        };

        outlet.SetContacts(contacts);
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
        IClock clock)
    {
        Name = name;
        ChannelId = channelId;
        Segment = segment;
        Banner = banner;
        TimeZoneId = timeZoneId;
        Address = address;
        Location = location;
        SetContacts(contacts);
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
