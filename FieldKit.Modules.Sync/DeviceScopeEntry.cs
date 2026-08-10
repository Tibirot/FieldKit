using FieldKit.BuildingBlocks;
using FieldKit.SharedKernel;

namespace FieldKit.Modules.Sync;

/// <summary>
/// One outlet a device is known to be holding — the membership half of the pull (sync engine §3).
/// </summary>
/// <remarks>
/// <para>
/// <b>Row version orders content; this decides membership.</b> They are different questions and only
/// one of them a cursor can answer. An outlet edited since the last pull has a higher row version
/// and arrives in the delta. An outlet that *entered* the rep's territory may not have been touched
/// for a year — its row version is far below the cursor, and `rowVersion > cursor` will never send
/// it. Without this table that outlet is invisible until somebody happens to edit it.
/// </para>
/// <para>
/// Per device rather than per user, because it records what a particular device was told. Only one
/// device is active at a time today, but a scope set that outlived a swap would tell a new phone it
/// already had rows it has never seen.
/// </para>
/// <para>
/// The consequence worth knowing: membership is idempotent through this table, content through the
/// cursor. A scope change delivers even when nothing has been edited and the cursor does not move,
/// and it delivers exactly once, because the set is rewritten as part of the same pull.
/// </para>
/// </remarks>
public sealed class DeviceScopeEntry : ITenantOwned
{
    public Guid DeviceId { get; set; }

    public Guid OutletId { get; set; }

    /// <summary>Carried for the tenant query filter, like every other row this system stores.</summary>
    public TenantId TenantId { get; set; }
}
