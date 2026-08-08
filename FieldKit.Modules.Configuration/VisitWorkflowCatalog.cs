using FieldKit.Modules.Configuration.Contracts;
using Microsoft.EntityFrameworkCore;

namespace FieldKit.Modules.Configuration;

/// <summary>
/// Answers <see cref="IVisitWorkflow"/> from the configured workflows (<c>VIS-03</c>).
/// </summary>
/// <remarks>
/// <para>
/// One query, and a default when it finds nothing — see the contract for why an unconfigured channel
/// gets an answer rather than a null.
/// </para>
/// <para>
/// <b>Per channel rather than in bulk</b>, unlike this module's other contract. The caller is a
/// visit, and a visit happens at one outlet in one channel: check-in asks once, and step gating asks
/// once more for the same visit. <c>IFieldDefinitionCatalog</c> is batched because its caller
/// validates a whole entity's custom fields at a time; shaping this the same way would be copying a
/// signature rather than answering a question.
/// </para>
/// <para>
/// It reads only Configuration's own schema. Whether the channel <i>exists</i> is Outlets' to say,
/// and this deliberately does not ask: a workflow for a deleted channel resolves to the default,
/// which is the same thing an unconfigured one does, and neither is a failure a visit should
/// inherit.
/// </para>
/// </remarks>
internal sealed class VisitWorkflowCatalog(ConfigurationDbContext db) : IVisitWorkflow
{
    public async Task<VisitWorkflowDescriptor> ForChannelAsync(
        Guid channelId, CancellationToken cancellationToken = default)
    {
        var workflow = await db.VisitWorkflows
            .Include(row => row.Steps)
            .SingleOrDefaultAsync(row => row.ChannelId == channelId, cancellationToken);

        return workflow?.Describe() ?? VisitWorkflow.DefaultFor(channelId);
    }
}
