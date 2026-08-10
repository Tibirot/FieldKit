using FieldKit.SharedKernel;

namespace FieldKit.Modules.Configuration.Contracts;

/// <summary>One step of a workflow, as it crosses the wire to a device.</summary>
/// <remarks>
/// Deliberately not <see cref="VisitStepDescriptor"/>, which is the in-process shape and carries the
/// <see cref="VisitStepType"/> enum. Serialised, an enum is an <i>ordinal</i>: inserting a value in
/// the middle of that list would silently reinterpret every workflow already stored on every device.
/// The name is the stable thing, so the name is what travels.
/// </remarks>
public sealed record VisitWorkflowStepSnapshot(int Order, string Type, bool Mandatory, string Label);

/// <summary>
/// One channel's visit workflow as the device holds it (<c>VIS-03</c>, sync engine §3).
/// </summary>
/// <remarks>
/// <b>The steps travel inside it, rather than as a second entity type with its own cursor.</b> A
/// workflow is only ever useful whole — a device holding four of five steps would run a visit that
/// silently asks for less than the tenant configured, and `BR-VIS-3` would gate check-out on a
/// mandatory step it never received. Sending the aggregate as one row makes a partial workflow
/// unrepresentable rather than merely unlikely.
/// </remarks>
public sealed record VisitWorkflowSnapshot(
    Guid Id,
    Guid ChannelId,
    bool PresenceExpected,
    IReadOnlyList<VisitWorkflowStepSnapshot> Steps,
    long RowVersion);

/// <summary>One page of workflow changes: what to upsert, what to drop, and how far the device is.</summary>
public sealed record VisitWorkflowChangePage(
    IReadOnlyList<VisitWorkflowSnapshot> Upserts,
    IReadOnlyList<ReferenceTombstone> Tombstones,
    long Cursor);

/// <summary>
/// The visit workflows a device should hold, as a delta (<c>OFF-03</c>, W8 slice 8b).
/// </summary>
/// <remarks>
/// <para>
/// <b>No scope argument, and that is the third distinct answer this protocol has given to "whose row
/// is it".</b> Outlets are scoped to the rep's territory; a planned call is scoped to the rep the
/// plan names; a workflow is scoped to <i>nobody</i> — every device in the tenant gets every one.
/// </para>
/// <para>
/// It could be narrowed: a rep's outlets have channels, and only those channels' workflows are ever
/// needed. That narrowing was rejected on both of its costs. It would reintroduce the membership
/// problem the outlet baseline exists to work around — moving one shop to another channel would put
/// a workflow in scope <i>without editing it</i>, so a pure delta would never send it — and it would
/// do so to save a payload of a handful of rows that a tenant's own administrators wrote. There is
/// nothing here that one rep may see and another may not.
/// </para>
/// <para>
/// <b>Tombstones are real here</b>, unlike journeys: an administrator can delete a workflow, and the
/// resulting tombstone is tenant-wide, so it can be sent to every device without telling anyone
/// anything about anybody. A device that dropped one falls back to the default — no steps, presence
/// expected — which is the same answer the server gives for an unconfigured channel.
/// </para>
/// </remarks>
public interface IVisitWorkflowFeed
{
    /// <summary>
    /// Workflows whose row version is above <paramref name="cursor"/>, plus tombstones for any
    /// deleted since.
    /// </summary>
    /// <param name="limit">
    /// A page size, for symmetry with the other feeds rather than because a tenant is expected to
    /// have five hundred channels. A limit that is never reached still has to be correct when it is.
    /// </param>
    Task<VisitWorkflowChangePage> GetChangesAsync(
        long cursor, int limit, CancellationToken cancellationToken = default);
}
