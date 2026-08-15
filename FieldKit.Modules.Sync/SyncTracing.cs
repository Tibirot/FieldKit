using System.Diagnostics;
using FieldKit.BuildingBlocks;

namespace FieldKit.Modules.Sync;

/// <summary>
/// The spans a device's sync leaves behind (<c>observability §1</c>) — W13 slice 2.
/// </summary>
/// <remarks>
/// <para>
/// <b>Static, where <see cref="SyncMetrics"/> is injected</b>, and the difference is not
/// inconsistency. An instrument has to be created once and reused, which is a lifetime question and
/// therefore the container's. An <c>ActivitySource</c> is a name that spans are opened under; the
/// documented shape for one is a static field, and injecting it would buy a seam nothing needs while
/// making every call site that opens a span take a constructor argument.
/// </para>
/// <para>
/// <b>What these add over the request span ASP.NET already opens.</b> That one says a POST to
/// <c>/api/sync/push</c> took 400ms. These say which device, how many mutations, and — one child
/// span each — which mutation was the slow one and what it was answered. Without them a push is a
/// single opaque bar, and "one rep's sync, end to end" (<c>observability §4</c>) is a sentence about
/// something you cannot see.
/// </para>
/// </remarks>
internal static class SyncTracing
{
    public static readonly ActivitySource Source = new(Telemetry.ActivitySourceName);

    /// <summary>The span for one push: which device, and how much it carried.</summary>
    public static Activity? Push(Guid deviceId, int mutations)
    {
        var activity = Source.StartActivity("sync.push");

        // Null when nothing is listening, which is the normal state of a process with no exporter —
        // `?.` rather than a guard because that is the shape every one of these takes, and a
        // `if (activity is not null)` around three lines reads as though the null were exceptional.
        activity?.SetTag(Telemetry.Tags.Device, deviceId.ToString());
        activity?.SetTag("fieldkit.sync.mutations", mutations);

        return activity;
    }

    /// <summary>
    /// The span for one mutation inside a push — where the device-minted id belongs.
    /// </summary>
    /// <remarks>
    /// A child span per mutation rather than one tag on the parent, because a mutation id is exactly
    /// the sort of value <c>Telemetry</c> refuses on a metric: unbounded, and worth one trace apiece.
    /// A push of two hundred is two hundred children under a sampled parent, which is what sampling
    /// is for — and it is the only view in which "the audit was the one that took four seconds" is
    /// visible at all.
    /// </remarks>
    public static Activity? Mutation(Guid mutationId, string type)
    {
        var activity = Source.StartActivity("sync.push.mutation");

        activity?.SetTag(Telemetry.Tags.Mutation, mutationId.ToString());
        activity?.SetTag("fieldkit.sync.mutation.type", type);

        return activity;
    }

    /// <summary>The span for one pull: which device asked, and what it was given.</summary>
    public static Activity? Pull(Guid deviceId)
    {
        var activity = Source.StartActivity("sync.pull");

        activity?.SetTag(Telemetry.Tags.Device, deviceId.ToString());

        return activity;
    }

    /// <summary>Records how a mutation was answered, on its own span.</summary>
    /// <remarks>
    /// The refusal <b>code</b>, and the same one the device was given — a span that says "rejected"
    /// without saying why is a span that sends the reader back to the logs, which is the round trip
    /// tracing exists to remove.
    /// </remarks>
    public static void Answered(this Activity? activity, MutationOutcome outcome)
    {
        if (activity is null) return;

        activity.SetTag("fieldkit.sync.mutation.status", outcome.Status.ToString());

        if (outcome.ReasonCode is { } reason)
            activity.SetTag(Telemetry.Tags.Reason, reason);

        // `Error` only for a refusal, never for a replay answered from the ledger: a device retrying
        // something that already succeeded is the protocol working, and colouring it red on a trace
        // view would train whoever reads it to ignore the colour.
        if (outcome.Status == MutationStatus.Rejected)
            activity.SetStatus(ActivityStatusCode.Error, outcome.ReasonCode);
    }
}
