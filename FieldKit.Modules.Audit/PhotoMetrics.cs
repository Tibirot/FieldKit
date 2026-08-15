using System.Diagnostics.Metrics;
using Microsoft.EntityFrameworkCore;
using FieldKit.BuildingBlocks;

namespace FieldKit.Modules.Audit;

/// <summary>
/// The binary-sync backlog: photographs referenced and not yet arrived
/// (<c>observability §2</c>, <c>OFF-08</c>, <c>B5</c>) — W13 slice 4.
/// </summary>
/// <remarks>
/// <para>
/// <b>A level, recorded on the two events that move it, rather than sampled.</b> The outbox backlog
/// needed a loop because rows arrive from every request; this one does not, because the count only
/// ever changes when an audit brings new photo references or when one is confirmed. Recording it at
/// exactly those two points makes the gauge <i>exact</i> at the moment of every change, which
/// polling would only approximate.
/// </para>
/// <para>
/// <b>And a sampler could not have done it anyway.</b> <c>PhotoEntry</c> is <c>ITenantOwned</c>, so
/// the global query filter reads the ambient tenant — and a background service has no principal to
/// read one from. Counting across tenants from a loop would need <c>IgnoreQueryFilters</c>, which is
/// banned at compile time, or a tenant list to iterate, which Audit has no business holding. The
/// isolation control that makes this safe is the same thing that makes a tenant-wide aggregate
/// impossible from outside a request; the outbox escaped it only because <c>OutboxMessage</c> is not
/// tenant-owned. Worth knowing before the next gauge over a tenant-owned table is designed.
/// </para>
/// <para>
/// The cost of that choice: a restart leaves the gauge unreported until the next audit or
/// confirmation in that tenant. That is the ordinary behaviour of a last-value gauge and the
/// alternative — a loop — cannot run at all here.
/// </para>
/// </remarks>
public sealed class PhotoMetrics
{
    private readonly Gauge<int> _pending;

    public PhotoMetrics(IMeterFactory factory) =>
        _pending = factory.Create(Telemetry.MeterName).CreateGauge<int>(
            "fieldkit.photos.upload.pending",
            unit: "{photo}",
            description: "Photographs an audit refers to whose bytes have not arrived.");

    /// <summary>Counts what this tenant is still waiting for, and records it.</summary>
    /// <remarks>
    /// A count rather than an increment, because the two events that move the level are not
    /// symmetric: an ingest adds a known number, a confirmation removes an unknown one — a device
    /// confirms the keys it holds, some of which it has confirmed before. One indexed count cannot
    /// drift; two counters kept by hand would. Written once here rather than at each of the two call
    /// sites, so the two can never come to disagree about what "pending" means.
    /// </remarks>
    public async Task ReportPendingAsync(AuditDbContext db, CancellationToken cancellationToken)
    {
        var pending = await db.Photos.CountAsync(photo => photo.UploadedAtUtc == null, cancellationToken);

        _pending.Record(
            pending,
            new KeyValuePair<string, object?>(Telemetry.Tags.Tenant, db.CurrentTenantId.Value.ToString()));
    }
}
