using System.Text.Json.Serialization;
using FieldKit.Modules.Configuration.Contracts;
using FieldKit.Modules.Outlets.Contracts;
using FieldKit.SharedKernel;
using FieldKit.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace FieldKit.Modules.Configuration;

/// <summary>One step, as an admin sets it. No order — position in the list is the order.</summary>
/// <remarks>
/// <para>
/// The type travels as its name, like every other enum on this API: an ordinal would make the
/// meaning depend on where a member happens to sit in the enum, and this one will grow.
/// </para>
/// <para>
/// <b>This is the endpoint that proved the rule needed enforcing.</b> While every enum said so one
/// property at a time, the paragraph above described an intention rather than the wire format:
/// <c>"Audit"</c> was refused with a 400 and only <c>0</c> was accepted. The response side never had
/// the problem, because <see cref="WorkflowStepResponse.Type"/> is a <c>string</c> — a request and its
/// own response disagreeing about how one enum is spelled is the shape of the bug. The attribute now
/// lives on <see cref="VisitStepType"/>, where forgetting it is not an option a caller has
/// (W11 slice 0b).
/// </para>
/// </remarks>
public sealed record VisitStepRequest(
    VisitStepType Type,
    bool Mandatory,
    string Label);

/// <summary>A channel's visit workflow, as an admin sets it.</summary>
public sealed record VisitWorkflowRequest(bool PresenceExpected, IReadOnlyList<VisitStepRequest> Steps);

/// <summary>
/// One step, as stored — what an admin configured, not what a rep has done with it.
/// </summary>
/// <remarks>
/// Named for the workflow rather than the visit because Visit has its own <c>VisitStepResponse</c>
/// for a step in progress, and the two are genuinely different things: this one has no status and
/// no timestamps, and it is the same for every visit in the channel.
/// </remarks>
public sealed record WorkflowStepResponse(int Order, string Type, bool Mandatory, string Label);

/// <summary>A channel's visit workflow, as stored.</summary>
public sealed record VisitWorkflowResponse(
    Guid ChannelId, bool PresenceExpected, IReadOnlyList<WorkflowStepResponse> Steps);

/// <summary>
/// The per-channel visit workflow (<c>VIS-03</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>PUT keyed by the channel</b>, like a segment's call frequency: a channel has at most one
/// workflow, so the natural identifier is the channel rather than a generated id, and setting it
/// twice has set it once.
/// </para>
/// <para>
/// <b>The steps are replaced wholesale</b> — see <see cref="VisitWorkflow"/> for why an ordered
/// thing cannot sensibly be patched.
/// </para>
/// </remarks>
internal static class VisitWorkflowEndpoints
{
    public static void MapVisitWorkflowEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var workflows = endpoints.MapGroup("/api/config/visit-workflows").WithTags("Configuration");

        workflows.MapGet("/", async (ConfigurationDbContext db, CancellationToken ct) =>
        {
            var rows = await db.VisitWorkflows
                .Include(workflow => workflow.Steps)
                .OrderBy(workflow => workflow.ChannelId)
                .ToListAsync(ct);

            return rows.Select(Respond).ToList();
        }).RequirePermission(ConfigurationPermissions.Read);

        /*
         * Answers for a channel nobody has configured too, with the default.
         *
         * That is the whole point of the contract's "never null", surfaced over HTTP so a screen
         * showing "how are visits worked here" gets the same answer check-in will act on — rather
         * than a 404 it has to translate into "no steps, and presence is expected", which is a rule
         * living in two places the moment it is written twice.
         */
        workflows.MapGet("/{channelId:guid}", async (
            Guid channelId, IVisitWorkflow catalog, CancellationToken ct) =>
        {
            var workflow = await catalog.ForChannelAsync(channelId, ct);

            return Results.Ok(new VisitWorkflowResponse(
                workflow.ChannelId,
                workflow.PresenceExpected,
                [.. workflow.Steps.Select(step =>
                    new WorkflowStepResponse(step.Order, step.Type.ToString(), step.Mandatory, step.Label))]));
        }).RequirePermission(ConfigurationPermissions.Read);

        workflows.MapPut("/{channelId:guid}", async (
            Guid channelId, VisitWorkflowRequest request, ConfigurationDbContext db,
            IOutletClassification outlets, IClock clock, CancellationToken ct) =>
        {
            if (StepsProblem(request.Steps) is { } problem) return problem;

            // The channel is Outlets' to confirm. A workflow keyed to a channel this tenant does not
            // have is one no visit will ever resolve — it would sit in the list looking configured.
            if (!await outlets.ChannelExistsAsync(channelId, ct))
            {
                return Problems.BadRequest(
                    "channelId", "No such channel in this tenant.", "config.workflow.unknownChannel");
            }

            var steps = request.Steps.Select(step => (step.Type, step.Mandatory, step.Label));

            var existing = await db.VisitWorkflows
                .Include(workflow => workflow.Steps)
                .SingleOrDefaultAsync(workflow => workflow.ChannelId == channelId, ct);

            if (existing is null)
            {
                existing = VisitWorkflow.Create(channelId, request.PresenceExpected, steps);
                db.VisitWorkflows.Add(existing);
            }
            else
            {
                // The replacements need no announcing to the context, and until `ModuleDbContext`
                // gained `ClientGeneratedKeyConvention` they did: EF read their client-set keys as
                // proof the rows existed and issued UPDATEs that matched none. Dropping the old rows
                // is orphan removal, which EF has always handled on its own.
                existing.Set(request.PresenceExpected, steps, clock);
            }

            await db.SaveChangesAsync(ct);

            return Results.Ok(Respond(existing));
        }).RequirePermission(ConfigurationPermissions.Write);

        workflows.MapDelete("/{channelId:guid}", async (
            Guid channelId, ConfigurationDbContext db, CancellationToken ct) =>
        {
            var existing = await db.VisitWorkflows
                .SingleOrDefaultAsync(workflow => workflow.ChannelId == channelId, ct);

            if (existing is null) return Results.NotFound();

            // Removing a workflow returns the channel to the default — no steps, presence expected —
            // rather than leaving visits in it unworkable. There is no "no workflow" state a visit
            // has to handle, which is what makes deleting one safe.
            db.VisitWorkflows.Remove(existing);
            await db.SaveChangesAsync(ct);

            return Results.NoContent();
        }).RequirePermission(ConfigurationPermissions.Write);
    }

    /// <summary>
    /// Refuses a sequence a rep could not work.
    /// </summary>
    /// <remarks>
    /// An empty list is allowed and is not a mistake: a visit that is just a check-in and a check-out
    /// is a real thing — a presence call — and refusing it would force an admin to invent a step.
    /// </remarks>
    private static IResult? StepsProblem(IReadOnlyList<VisitStepRequest> steps)
    {
        var problems = new List<FieldProblem>();

        if (steps.Count > VisitWorkflow.MaximumSteps)
        {
            problems.Add(new FieldProblem(
                "steps",
                $"A visit holds at most {VisitWorkflow.MaximumSteps} steps.",
                "config.workflow.tooManySteps",
                new Dictionary<string, string> { ["max"] = VisitWorkflow.MaximumSteps.ToString() }));
        }

        for (var index = 0; index < steps.Count; index++)
        {
            var step = steps[index];

            if (!Enum.IsDefined(step.Type))
            {
                problems.Add(new FieldProblem(
                    $"steps[{index}].type", "Unknown step type.", "config.workflow.unknownStepType"));
            }

            if (string.IsNullOrWhiteSpace(step.Label))
            {
                // Indexed, because an admin looking at eight steps cannot work out which one "a step
                // needs a label" is about.
                problems.Add(new FieldProblem(
                    $"steps[{index}].label",
                    "A step needs a label — it is what the rep reads.",
                    "config.workflow.labelRequired"));

                continue;
            }

            if (TextLimits.TooLong(
                    $"steps[{index}].label", step.Label.Trim(), VisitWorkflowStep.MaximumLabelLength,
                    "config.workflow.labelTooLong") is { } tooLong)
            {
                problems.Add(tooLong);
            }
        }

        return problems.Count == 0 ? null : Problems.BadRequest(problems);
    }

    private static VisitWorkflowResponse Respond(VisitWorkflow workflow)
    {
        var described = workflow.Describe();

        return new VisitWorkflowResponse(
            described.ChannelId,
            described.PresenceExpected,
            [.. described.Steps.Select(step =>
                new WorkflowStepResponse(step.Order, step.Type.ToString(), step.Mandatory, step.Label))]);
    }
}
