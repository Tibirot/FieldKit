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

    /// <summary>
    /// What <c>AddSource</c> is given in the host, and the name every FieldKit span is created under.
    /// </summary>
    /// <remarks>
    /// <b>Deliberately the same string as <see cref="MeterName"/>.</b> They are separate
    /// subscriptions — a meter and an activity source are different registries — and naming them
    /// differently would mean an operator who found the metrics still had to be told a second word to
    /// find the traces beside them. One product, one name, two subscriptions.
    /// </remarks>
    public const string ActivitySourceName = MeterName;

    /// <summary>
    /// The tag keys a FieldKit instrument may use — and, where noted, a span.
    /// </summary>
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

        /// <summary>
        /// Which module a measurement is about — one per <c>ModuleDbContext</c> in the solution.
        /// </summary>
        /// <remarks>
        /// Bounded by the same test as <see cref="Tenant"/>, and more obviously: the value set is a
        /// list of projects, and adding to it is a pull request rather than a customer signing up.
        /// </remarks>
        public const string Module = "fieldkit.module";

        /// <summary>What sort of thing happened — always an enum, so always a closed set.</summary>
        public const string Kind = "fieldkit.kind";

        /// <summary>How a visit ended — the <c>VisitOutcome</c> enum, so a closed set.</summary>
        public const string Outcome = "fieldkit.outcome";

        /// <summary>
        /// The currency an amount is in. Bounded, and <b>load-bearing rather than descriptive</b>:
        /// a histogram mixing RON and EUR describes nothing, so an amount without this is a number
        /// with no unit.
        /// </summary>
        public const string Currency = "fieldkit.currency";

        /*
         * Below this line: span-only. Every one of them is unbounded, which is exactly why they are
         * here rather than on an instrument — a unique value costs one trace, and being able to
         * follow *one* rep's sync is the reason the doc asks for them at all (observability §4).
         *
         * Putting any of these on a metric is the mistake this file exists to prevent. They are kept
         * in the same class as the two above so that the boundary is visible when somebody reaches
         * for a tag name, rather than discoverable by reading two files.
         */

        /// <summary>The authenticated subject. <b>Span only.</b></summary>
        public const string Subject = "fieldkit.subject";

        /// <summary>The device a rep is syncing from. <b>Span only.</b></summary>
        public const string Device = "fieldkit.device";

        /// <summary>The device-minted id of one pushed mutation. <b>Span only.</b></summary>
        public const string Mutation = "fieldkit.mutation";

        /// <summary>The outlet a question is about. <b>Span only.</b></summary>
        public const string Outlet = "fieldkit.outlet";
    }
}
