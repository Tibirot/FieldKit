using System.Diagnostics;
using FieldKit.BuildingBlocks;

namespace FieldKit.Infrastructure.Outbox;

/// <summary>The span one delivered integration event leaves behind — W13 slice 3.</summary>
/// <remarks>
/// Listed in <c>observability §1</c> alongside sync and pricing, and deferred out of slice 2 because
/// there was nothing running to open it.
/// </remarks>
internal static class OutboxTracing
{
    private static readonly ActivitySource Source = new(Telemetry.ActivitySourceName);

    /// <summary>
    /// Opens a span for delivering one message.
    /// </summary>
    /// <remarks>
    /// The event <b>type</b> is here rather than on a metric. It is a closed vocabulary, so it would
    /// be admissible as a tag — but it would multiply every outbox series by the number of event
    /// types for a question ("which kind is slow") that a trace answers directly and a dashboard
    /// asks rarely.
    /// </remarks>
    public static Activity? Dispatch(string module, Guid messageId, string type)
    {
        var activity = Source.StartActivity("outbox.dispatch");

        activity?.SetTag(Telemetry.Tags.Module, module);
        activity?.SetTag("fieldkit.outbox.message", messageId.ToString());
        activity?.SetTag("fieldkit.outbox.type", type);

        return activity;
    }
}
