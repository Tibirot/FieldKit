namespace FieldKit.BuildingBlocks;

/// <summary>
/// The one meter every FieldKit signal hangs off, and the rules for what may label a measurement
/// (<c>observability §2</c>) — W13 slice 1.
/// </summary>
/// <remarks>
/// <para>
/// <b>One meter, not one per module.</b> A meter name is what an exporter subscribes to and what an
/// operator types into a query; nine of them would mean nine subscriptions to keep in step and a
/// dashboard that silently loses a panel when the tenth arrives unregistered. The instruments still
/// belong to the areas that emit them — <c>SyncMetrics</c> owns the sync ones — so this file names
/// the meter and the rules, and holds no instrument of its own.
/// </para>
/// <para>
/// <b>Tenant is a tag, never part of a name.</b> "Visits completed, by tenant" is a dimension of one
/// series; <c>fieldkit.visits.completed.acme</c> is a new series per customer, and a metric name that
/// grows with the customer list cannot be aggregated, alerted on, or dashboarded once. The
/// [observability doc](../docs/architecture/15-observability.md) writes every metric as one name, and
/// this is the reading of it.
/// </para>
/// <para>
/// <b>What must never become a tag: anything unbounded.</b> A mutation id, a device id, a subject, an
/// outlet id, a free-text refusal detail. Each is a fresh time series per value, which is how a
/// metrics backend is brought down by the thing that was supposed to warn you. They are not lost —
/// they belong on a **span**, where one high-cardinality value costs one trace rather than one
/// series forever (slice 2). Tenant is the one identifier admitted here, and only because it is
/// bounded by construction: a tenant is a Keycloak realm, provisioned by hand
/// ([ADR-0008](../docs/architecture/adr/0008-authentication-and-multitenancy.md)).
/// </para>
/// <para>
/// A refusal <b>code</b> is admitted for the same reason — <c>ADR-0012</c> codes are a closed
/// vocabulary declared in source. The <i>detail</i> beside one is a sentence, and is not.
/// </para>
/// </remarks>
public static class Telemetry
{
    /// <summary>
    /// What an exporter subscribes to, and what <c>AddMeter</c> is given in the host.
    /// </summary>
    /// <remarks>
    /// Unprefixed and unversioned on purpose: the instruments carry the <c>fieldkit.</c> prefix, so a
    /// meter called <c>FieldKit.Metrics.V1</c> would repeat the product's name and promise a v2 that
    /// nothing plans. Renaming this breaks every dashboard, which is the argument for choosing it
    /// once rather than for choosing it carefully.
    /// </remarks>
    public const string MeterName = "FieldKit";

    /// <summary>The tag keys a FieldKit instrument may use.</summary>
    /// <remarks>
    /// Constants rather than literals so a typo is a build error rather than a second series with a
    /// name one character off the first — the failure mode that makes a graph look half-empty and a
    /// count look half-right.
    /// </remarks>
    public static class Tags
    {
        /// <summary>Which tenant the measurement belongs to. Bounded; see the remarks above.</summary>
        public const string Tenant = "fieldkit.tenant";

        /// <summary>An <c>ADR-0012</c> refusal code. A closed vocabulary, never a sentence.</summary>
        public const string Reason = "fieldkit.reason";
    }
}
