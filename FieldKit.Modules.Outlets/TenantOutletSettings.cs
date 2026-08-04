using FieldKit.BuildingBlocks;
using FieldKit.SharedKernel;

namespace FieldKit.Modules.Outlets;

/// <summary>
/// Per-tenant policy for the outlet base — one row per tenant.
/// </summary>
/// <remarks>
/// <para>
/// Whether an estate runs geofenced check-in is a business-wide decision, so it is scoped to the
/// tenant rather than the person doing the editing. Two admins working the same outlet base under
/// different rules would make the data's integrity depend on who touched it last.
/// </para>
/// <para>
/// It lives here for now because Outlets is the only module that asks the question. The
/// Configuration module owns per-tenant settings properly (ADR-0009) and this should move there when
/// it lands — at which point this becomes a field definition like any other.
/// </para>
/// </remarks>
public sealed class TenantOutletSettings : AggregateRoot, ITenantOwned, IAuditable
{
    public Guid Id { get; private set; }

    /// <summary>
    /// Whether supplied geo-coordinates are validated on save.
    /// </summary>
    /// <remarks>
    /// <para>
    /// When on, coordinates that are present must be a real point on the earth. When off, no
    /// coordinate validation happens at all. An outlet with <b>no</b> coordinates is never rejected
    /// either way — they are optional, and this setting does not make them required.
    /// </para>
    /// <para>
    /// 📝 The consequence, recorded rather than discovered later: while this is off, out-of-range
    /// coordinates can be stored. Turning it on afterwards does not retroactively clean them, and the
    /// next save of such an outlet will fail against data that is already in the table. A tenant
    /// enabling this on an existing estate should expect to fix rows, not just flip a switch.
    /// </para>
    /// </remarks>
    public bool ValidateGeoCoordinates { get; private set; } = true;

    public TenantId TenantId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset? ModifiedAtUtc { get; set; }
    public string? ModifiedBy { get; set; }

    private TenantOutletSettings() { } // EF

    /// <summary>
    /// The defaults a tenant gets before anyone has changed anything.
    /// </summary>
    /// <remarks>
    /// Validation defaults to <b>on</b>: a tenant that has never thought about this is better served
    /// by rejecting a latitude of 91 than by storing it. Opting out is a deliberate act.
    /// </remarks>
    public static TenantOutletSettings CreateDefault() => new() { Id = Guid.CreateVersion7() };

    public void SetGeoValidation(bool enabled, IClock clock)
    {
        ValidateGeoCoordinates = enabled;
        ModifiedAtUtc = clock.UtcNow;
    }
}
