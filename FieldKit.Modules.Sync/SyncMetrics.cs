using System.Diagnostics.Metrics;
using FieldKit.BuildingBlocks;
using FieldKit.SharedKernel;

namespace FieldKit.Modules.Sync;

/// <summary>
/// What a day's field work costs to reconcile (<c>observability §2</c>) — W13 slice 1.
/// </summary>
/// <remarks>
/// <para>
/// Three signals, and they answer different questions. <b>Batch size</b> is how much work a rep
/// accumulated before they got signal — a distribution that shifts right means reps are offline
/// longer, which is a coverage story rather than a server one. <b>Latency</b> is how long the server
/// took to absorb it, and is the one with a stated budget (<c>observability §6</c>: seconds after a
/// good reconnect). <b>Rejections</b> are per mutation and carry their refusal code, because "sync is
/// failing" and "one outlet was deleted while a rep was offline" look identical in a success rate.
/// </para>
/// <para>
/// <b>Latency is measured with a monotonic <c>Stopwatch</c> timestamp, not a clock.</b> <c>IClock</c> is the injected
/// wall clock every business rule uses, and a wall clock can step — an NTP correction mid-push would
/// produce a negative duration or an hour-long one. A monotonic timestamp cannot; it also never
/// answers "what time is it", which is why the banned-symbol list does not object to it.
/// </para>
/// <para>
/// Registered as a singleton: instruments are created once and are thread-safe by design. A scoped
/// one would build three instruments per request and publish a new instrument to every listener each
/// time, which is how a metrics backend learns about ten thousand identical series.
/// </para>
/// </remarks>
public sealed class SyncMetrics
{
    private readonly Histogram<int> _batchSize;
    private readonly Histogram<double> _pushDuration;
    private readonly Counter<long> _rejected;

    public SyncMetrics(IMeterFactory factory)
    {
        // Through the factory rather than `new Meter(...)`: the factory ties the meter's lifetime to
        // the container, so a test host that starts and stops repeatedly does not leak one per host.
        var meter = factory.Create(Telemetry.MeterName);

        _batchSize = meter.CreateHistogram<int>(
            "fieldkit.sync.push.batch_size",
            unit: "{mutation}",
            description: "How many mutations a device carried in one push.");

        _pushDuration = meter.CreateHistogram<double>(
            "fieldkit.sync.push.latency",
            unit: "ms",
            description: "How long the server took to absorb one push.");

        _rejected = meter.CreateCounter<long>(
            "fieldkit.sync.mutations.rejected",
            unit: "{mutation}",
            description: "Mutations refused, by the refusal code the device was given.");
    }

    /// <summary>Records one push: how much work arrived, and how long absorbing it took.</summary>
    /// <remarks>
    /// <b>Recorded for a push that was refused as well as one that was applied</b>, including a batch
    /// over the limit. The question this histogram answers is "how much work does a reconnect carry",
    /// and a device that tried to send three hundred mutations carried three hundred mutations — that
    /// it was turned away is what makes it the measurement worth having, not a reason to drop it. A
    /// histogram that silently excludes its own outliers describes a system nobody runs.
    /// </remarks>
    public void PushObserved(TenantId tenant, int mutations, TimeSpan elapsed)
    {
        var tag = new KeyValuePair<string, object?>(Telemetry.Tags.Tenant, Label(tenant));

        _batchSize.Record(mutations, tag);
        _pushDuration.Record(elapsed.TotalMilliseconds, tag);
    }

    /// <summary>Counts one refused mutation under the <c>ADR-0012</c> code the device was given.</summary>
    /// <remarks>
    /// The code, never the detail beside it. <c>sync.push.typeUnsupported</c> is one of a closed set;
    /// its detail names the type the device sent, which a modified client controls — an attacker-chosen
    /// tag value is an unbounded tag with extra steps.
    /// </remarks>
    public void MutationRejected(TenantId tenant, string reasonCode) =>
        _rejected.Add(
            1,
            new KeyValuePair<string, object?>(Telemetry.Tags.Tenant, Label(tenant)),
            new KeyValuePair<string, object?>(Telemetry.Tags.Reason, reasonCode));

    /// <summary>The tenant, as a tag value.</summary>
    /// <remarks>
    /// The id rather than the realm name, because the id is what every other signal will carry and a
    /// dashboard that joins them needs one spelling. Formatted in one place so it stays one spelling.
    /// </remarks>
    private static string Label(TenantId tenant) => tenant.Value.ToString();
}
