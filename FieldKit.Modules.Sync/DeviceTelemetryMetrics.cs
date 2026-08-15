using System.Diagnostics.Metrics;
using FieldKit.BuildingBlocks;
using FieldKit.SharedKernel;

namespace FieldKit.Modules.Sync;

/// <summary>
/// How often field devices are in trouble (<c>observability §5</c>) — W13 slice 8.
/// </summary>
/// <remarks>
/// <para>
/// <b>The counter is what makes this alertable rather than merely searchable.</b> The logs carry the
/// detail and a person has to go looking for them; a rate of <c>StorageEvicted</c> climbing after a
/// release is a thing that should find the operator instead. This is the only signal in W13 that
/// measures the client rather than the server.
/// </para>
/// <para>
/// <b>Kind is a tag; the device is not.</b> The kinds are an enum, so the series count is fixed at
/// five per tenant. A device id would be one series per phone in the fleet — the unbounded-tag
/// mistake <c>Telemetry</c> exists to refuse — and it is already in the log line, which is where a
/// question about one phone belongs.
/// </para>
/// </remarks>
public sealed class DeviceTelemetryMetrics
{
    private readonly Counter<long> _reported;

    public DeviceTelemetryMetrics(IMeterFactory factory) =>
        _reported = factory.Create(Telemetry.MeterName).CreateCounter<long>(
            "fieldkit.device.events",
            unit: "{event}",
            description: "Things a field device reported having gone wrong, by kind.");

    public void Reported(TenantId tenant, DeviceEventKind kind) =>
        _reported.Add(
            1,
            new KeyValuePair<string, object?>(Telemetry.Tags.Tenant, tenant.Value.ToString()),
            new KeyValuePair<string, object?>(Telemetry.Tags.Kind, kind.ToString()));
}
