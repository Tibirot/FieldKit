using System.Diagnostics.Metrics;
using FieldKit.BuildingBlocks;

namespace FieldKit.Infrastructure.Outbox;

/// <summary>
/// Whether the outbox is keeping up (<c>observability §2</c>) — W13 slice 3.
/// </summary>
/// <remarks>
/// <para>
/// <b>The backlog is the alertable one</b>, and the doc says so. It is the only signal that turns a
/// module quietly failing to deliver into a page: handlers are idempotent and delivery is
/// at-least-once, so a broken subscriber does not throw anywhere a user can see — the rows simply
/// stop draining. Zero in steady state, rising when something is wrong.
/// </para>
/// <para>
/// <b>Dispatch latency is the <i>lag</i>, not the work.</b> How long the processor took to run a
/// batch is a number about this server; how long an event waited between being committed and being
/// delivered is the eventual-consistency window that <c>ADR-0006</c> asks a reader to accept. The
/// second is the one worth a histogram, so this measures <c>ProcessedOn − OccurredOn</c> per message.
/// </para>
/// <para>
/// <b>Module is a tag and is bounded</b> — one per <c>ModuleDbContext</c> in the solution, which is a
/// number a person could count. No event type, no message id, no tenant: the first is closed but
/// would multiply every series by nine for a question a trace answers better, and the last two are
/// exactly what <c>Telemetry</c> refuses.
/// </para>
/// </remarks>
public sealed class OutboxMetrics
{
    private readonly Gauge<int> _backlog;
    private readonly Histogram<double> _lag;
    private readonly Counter<long> _failed;

    public OutboxMetrics(IMeterFactory factory)
    {
        var meter = factory.Create(Telemetry.MeterName);

        _backlog = meter.CreateGauge<int>(
            "fieldkit.outbox.backlog",
            unit: "{message}",
            description: "Integration events committed and not yet delivered.");

        _lag = meter.CreateHistogram<double>(
            "fieldkit.outbox.dispatch.latency",
            unit: "ms",
            description: "How long an event waited between being committed and being delivered.");

        _failed = meter.CreateCounter<long>(
            "fieldkit.outbox.dispatch.failed",
            unit: "{message}",
            description: "Delivery attempts that threw and left the message for retry.");
    }

    /// <summary>Records how many messages are waiting in one module's outbox.</summary>
    /// <remarks>
    /// A gauge rather than an up-down counter: the dispatcher <i>observes</i> a count it did not
    /// author. Rows arrive from every request that saves an aggregate, so a counter this loop
    /// incremented and decremented would drift the first time anything wrote one behind its back —
    /// which is the ordinary case, not an edge one.
    /// </remarks>
    public void Backlog(string module, int pending) =>
        _backlog.Record(pending, new KeyValuePair<string, object?>(Telemetry.Tags.Module, module));

    /// <summary>Records how long one delivered message waited.</summary>
    public void Delivered(string module, TimeSpan lag) =>
        _lag.Record(lag.TotalMilliseconds, new KeyValuePair<string, object?>(Telemetry.Tags.Module, module));

    /// <summary>Counts one delivery that threw and left its message for another attempt.</summary>
    /// <remarks>
    /// Separate from the backlog on purpose. A backlog that is high because a subscriber is throwing
    /// and one that is high because a burst arrived need different answers, and a single number
    /// cannot tell them apart — the counter moving is what says which.
    /// </remarks>
    public void Failed(string module) =>
        _failed.Add(1, new KeyValuePair<string, object?>(Telemetry.Tags.Module, module));
}
