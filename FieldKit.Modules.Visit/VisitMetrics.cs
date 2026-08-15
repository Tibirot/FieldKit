using System.Diagnostics.Metrics;
using FieldKit.BuildingBlocks;
using FieldKit.SharedKernel;

namespace FieldKit.Modules.Visit;

/// <summary>
/// Business throughput: visits finished (<c>observability §2</c>) — W13 slice 4.
/// </summary>
/// <remarks>
/// <para>
/// <b>Emitted where the work happens, not from an integration-event handler.</b> Visit raises
/// <c>VisitCompleted</c> and — since slice 3 — something actually delivers it, so a handler was the
/// tempting shape: no domain code touched, the metric assembled from a fact the module already
/// publishes. It is the wrong one twice over.
/// </para>
/// <para>
/// First, <b>business throughput must not depend on the outbox draining.</b> A dispatcher that
/// stalls would flatten this counter, and a flat line on "visits completed" reads as *reps have
/// stopped working* rather than *delivery has stopped* — the most expensive possible confusion
/// between a business signal and an infrastructure one. Second, <c>VisitCompleted</c> does not carry
/// a tenant, so a handler could not tag by tenant at all without changing a published integration
/// event for the convenience of a metric.
/// </para>
/// <para>
/// <b>Outcome is a tag</b> because it is the difference between "reps are busy" and "reps are
/// selling": a productive-visit rate is `BR-VIS-3`'s own vocabulary and the same number the
/// supervisor dashboard puts on screen. Bounded — the enum has two members and adding a third is a
/// pull request.
/// </para>
/// </remarks>
public sealed class VisitMetrics
{
    private readonly Counter<long> _completed;

    public VisitMetrics(IMeterFactory factory) =>
        _completed = factory.Create(Telemetry.MeterName).CreateCounter<long>(
            "fieldkit.visits.completed",
            unit: "{visit}",
            description: "Visits checked out, by outcome.");

    /// <summary>Counts one finished visit.</summary>
    /// <remarks>
    /// Called on both paths that finish one — the online check-out and the offline ingest — because
    /// they are the same event to a supervisor and differ only in how the rep's phone got here.
    /// Counting one and not the other would make the number a measurement of connectivity.
    /// </remarks>
    public void Completed(TenantId tenant, VisitOutcome outcome) =>
        _completed.Add(
            1,
            new KeyValuePair<string, object?>(Telemetry.Tags.Tenant, tenant.Value.ToString()),
            new KeyValuePair<string, object?>(Telemetry.Tags.Outcome, outcome.ToString()));
}
