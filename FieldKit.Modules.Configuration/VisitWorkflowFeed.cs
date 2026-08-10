using FieldKit.Infrastructure;
using FieldKit.Modules.Configuration.Contracts;
using FieldKit.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace FieldKit.Modules.Configuration;

/// <summary>
/// Configuration's side of the pull protocol (<c>OFF-03</c>, W8 slice 8b).
/// </summary>
/// <remarks>
/// The same two-source merge as the other feeds — live rows above the cursor, tombstones above the
/// cursor — with no scope predicate at all. The tenant filter is the DbContext's, as everywhere.
/// </remarks>
internal sealed class VisitWorkflowFeed(ConfigurationDbContext db) : IVisitWorkflowFeed
{
    public async Task<VisitWorkflowChangePage> GetChangesAsync(
        long cursor, int limit, CancellationToken cancellationToken = default)
    {
        // `Include`, not a projection over the steps: EF cannot translate the ordered sub-select
        // into the record's constructor, and a workflow is small enough that loading it whole costs
        // nothing worth optimising.
        var changed = await db.VisitWorkflows
            .Include(workflow => workflow.Steps)
            .Where(workflow => workflow.RowVersion > cursor)
            .OrderBy(workflow => workflow.RowVersion)
            .Take(limit)
            .ToListAsync(cancellationToken);

        var upserts = changed.Select(Describe).ToList();

        var tombstones = await db.Set<Tombstone>()
            .Where(tombstone => tombstone.RowVersion > cursor
                && tombstone.EntityType == nameof(VisitWorkflow))
            .OrderBy(tombstone => tombstone.RowVersion)
            .Take(limit)
            .Select(tombstone => new ReferenceTombstone(tombstone.EntityId, tombstone.RowVersion))
            .ToListAsync(cancellationToken);

        // The highest version *in this page*, never the table's maximum — a truncated page must
        // resume rather than skip everything between the last row sent and the high-water mark.
        var highest = cursor;
        if (upserts.Count > 0) highest = Math.Max(highest, upserts[^1].RowVersion);
        if (tombstones.Count > 0) highest = Math.Max(highest, tombstones[^1].RowVersion);

        return new VisitWorkflowChangePage(upserts, tombstones, highest);
    }

    private static VisitWorkflowSnapshot Describe(VisitWorkflow workflow) => new(
        workflow.Id,
        workflow.ChannelId,
        workflow.PresenceExpected,
        [.. workflow.Steps
            .OrderBy(step => step.Order)
            .Select(step => new VisitWorkflowStepSnapshot(
                step.Order, step.Type.ToString(), step.Mandatory, step.Label))],
        workflow.RowVersion);
}
