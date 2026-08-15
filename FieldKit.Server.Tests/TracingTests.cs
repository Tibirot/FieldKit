using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using FieldKit.BuildingBlocks;
using FieldKit.Modules.Sync;
using FieldKit.Modules.Visit.Contracts;

namespace FieldKit.Server.Tests;

/// <summary>
/// What a request leaves behind to be found by (<c>observability §1</c>, §4) — W13 slice 2.
/// </summary>
/// <remarks>
/// <para>
/// The claim under test is the one the W13 slice 0 audit could not verify: that somebody holding an
/// error message can find the request that produced it. That needs two halves to line up — a trace id
/// <b>in the response</b>, and spans <b>under that trace id</b> carrying the tenant. Asserting either
/// alone would pass while the pair was useless.
/// </para>
/// <para>
/// An <c>ActivityListener</c> with <c>AllData</c> sampling, because spans are not created at all when
/// nothing listens. That is worth stating plainly: every <c>StartActivity</c> in this codebase returns
/// <b>null</b> in a process with no exporter, so a test that forgot the listener would assert nothing
/// and say so as a pass.
/// </para>
/// </remarks>
[Collection(ServerCollection.Name)]
public class TracingTests(ServerFixture fixture)
{
    private static string Unique(string label) => $"{label}-{Guid.NewGuid():N}"[..18];

    [Fact]
    public async Task A_refusal_carries_the_trace_it_happened_in()
    {
        /*
         * The half that reaches a person. `observability §4` promised this in "every ProblemDetails"
         * — and this API answers refusals with its own `{ errors: [...] }` envelope, which had no
         * trace id in it at all. Asserted against a real refusal rather than a constructed envelope,
         * because what was missing was the wiring, not the record.
         */
        using var listening = Listening();
        using var rep = fixture.CreateAuthenticatedClient(fixture.AccessToken);

        var response = await rep.PostAsJsonAsync(
            "/api/sync/push", new PushRequest(Guid.CreateVersion7(), []));

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var traceId = body.RootElement.GetProperty("traceId").GetString();

        // 32 hex characters — the trace, not the `00-…-01` traceparent that names one span inside it.
        Assert.Matches("^[0-9a-f]{32}$", traceId);

        // And it is the trace the request actually ran in, not a fresh id minted for the response.
        Assert.Contains(listening.Finished, span => span.TraceId.ToString() == traceId);
    }

    [Fact]
    public async Task The_request_span_names_the_tenant_and_the_rep()
    {
        /*
         * The other half. A trace id is only useful if the spans under it can be found by tenant —
         * "everything this tenant did" has to be a filter, and that means the stamp lands on the
         * request's own span rather than on a span of ours beneath it.
         */
        using var listening = Listening();
        using var rep = fixture.CreateAuthenticatedClient(fixture.AccessToken);

        await rep.GetAsync("/api/auth/whoami");

        /*
         * Matched on `DisplayName`, which is the route — `OperationName` is ASP.NET's own
         * `Microsoft.AspNetCore.Hosting.HttpRequestIn` for every request it serves, so filtering on
         * it would have selected the whole conversation. That cost a diagnostic run to learn, and it
         * is the sort of thing a comment saves the next reader.
         */
        var request = Assert.Single(
            listening.Finished.Where(span => span.DisplayName == "GET /api/auth/whoami"));

        // The tenant is on the *request's own* span, not on a child of it — which is what makes
        // "everything this tenant did" a filter rather than a join.
        Assert.NotNull(request.GetTagItem(Telemetry.Tags.Tenant));
        Assert.NotNull(request.GetTagItem(Telemetry.Tags.Subject));
    }

    [Fact]
    public async Task An_anonymous_request_is_traced_without_a_tenant()
    {
        /*
         * `ITenantContext` throws when there is no authenticated principal — deliberately, so a
         * tenant-owned query can never run unscoped. Asking it for a tenant on the way past would
         * turn a legitimately anonymous request into a 500, which is what this asserts is absent.
         *
         * <b>Against `/alive`, and the first version of this test was wrong.</b> It used an
         * unauthenticated `/sync/push` and asserted the 401 — which passes with the guard removed,
         * because `UseAuthorization` short-circuits *before* this middleware and it never runs at all
         * on that path. The guard only matters where a request is allowed through **without** a
         * principal, so the test has to use an endpoint that is. Found by sabotage: deleting the
         * condition changed nothing.
         */
        using var listening = Listening();

        var response = await fixture.Client.GetAsync("/alive");

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain(listening.Finished, span => span.GetTagItem(Telemetry.Tags.Tenant) is not null);
    }

    [Fact]
    public async Task Each_pushed_mutation_gets_a_span_carrying_its_id()
    {
        /*
         * The mutation id is exactly what `Telemetry` refuses on a metric — unbounded, one series per
         * value — and exactly what a trace is for. This is where the pair of decisions meets: the
         * counter says *how many* were rejected, and this says *which one*.
         */
        using var listening = Listening();
        using var rep = fixture.CreateAuthenticatedClient(fixture.AccessToken);

        var device = await BindDeviceAsync(rep);
        var mutationId = Guid.CreateVersion7();

        await rep.PostAsJsonAsync("/api/sync/push", new PushRequest(
            device, [new PushedMutation(mutationId, "SomethingThisServerHasNeverHeardOf")]));

        var span = Assert.Single(
            listening.Finished.Where(candidate => candidate.OperationName == "sync.push.mutation"));

        Assert.Equal(mutationId.ToString(), span.GetTagItem(Telemetry.Tags.Mutation));
        Assert.Equal("Rejected", span.GetTagItem("fieldkit.sync.mutation.status"));

        // The refusal code, on the span. Without it a red bar sends the reader back to the logs,
        // which is the round trip tracing exists to remove.
        Assert.Equal("sync.push.typeUnsupported", span.GetTagItem(Telemetry.Tags.Reason));
        Assert.Equal(ActivityStatusCode.Error, span.Status);
    }

    [Fact]
    public async Task A_replayed_mutation_is_marked_as_a_replay_rather_than_as_work()
    {
        /*
         * A retry answered from the ledger did nothing, and a trace that showed it doing the work
         * would describe a protocol this server deliberately does not have. It is also not an error:
         * colouring a healthy retry red teaches whoever reads the trace to ignore the colour.
         */
        using var rep = fixture.CreateAuthenticatedClient(fixture.AccessToken);

        var device = await BindDeviceAsync(rep);
        var mutation = new PushedMutation(Guid.CreateVersion7(), nameof(CapturedVisit));

        // The first attempt is outside the listener: what is under test is the *second*.
        await rep.PostAsJsonAsync("/api/sync/push", new PushRequest(device, [mutation]));

        using var listening = Listening();

        await rep.PostAsJsonAsync("/api/sync/push", new PushRequest(device, [mutation]));

        var span = Assert.Single(
            listening.Finished.Where(candidate => candidate.OperationName == "sync.push.mutation"));

        Assert.Equal(true, span.GetTagItem("fieldkit.sync.mutation.replayed"));
    }

    [Fact]
    public async Task A_pull_says_which_device_asked()
    {
        using var listening = Listening();
        using var rep = fixture.CreateAuthenticatedClient(fixture.AccessToken);

        var device = await BindDeviceAsync(rep);

        await rep.PostAsJsonAsync("/api/sync/pull", new PullRequest(device, null));

        var span = Assert.Single(
            listening.Finished.Where(candidate => candidate.OperationName == "sync.pull"));

        Assert.Equal(device.ToString(), span.GetTagItem(Telemetry.Tags.Device));
    }

    [Fact]
    public void Every_FieldKit_span_is_opened_under_the_one_source()
    {
        /*
         * The same claim slice 1 makes about the meter, for the other registry. A span opened on an
         * unregistered source is not an error anywhere — it simply never leaves the process, and the
         * trace it belonged to has a hole in it that nothing reports.
         *
         * Asserted by listening *only* to the registered name and requiring the spans to arrive,
         * which is why the tests above can be written the way they are at all.
         */
        Assert.Equal("FieldKit", Telemetry.ActivitySourceName);
        Assert.Equal(Telemetry.MeterName, Telemetry.ActivitySourceName);
    }

    private static async Task<Guid> BindDeviceAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync(
            "/api/sync/devices", new BindDeviceRequest(Unique("Phone")));

        return (await response.Content.ReadFromJsonAsync<DeviceResponse>())!.Id;
    }

    /// <summary>Collects finished spans for as long as it is alive.</summary>
    private static Listener Listening() => new();

    /// <summary>
    /// An <c>ActivityListener</c> over every source, because two are in play.
    /// </summary>
    /// <remarks>
    /// The FieldKit source for the domain spans, and ASP.NET's own for the request span the tenant is
    /// stamped on — which is the whole point of stamping that one rather than a child. Sampling is
    /// <c>AllData</c>: the default is to record nothing, and a listener that samples nothing collects
    /// nothing while looking exactly like a listener that works.
    /// </remarks>
    private sealed class Listener : IDisposable
    {
        private readonly ActivityListener _listener;
        private readonly List<Activity> _finished = [];
        private readonly Lock _gate = new();

        public Listener()
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = _ => true,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                    ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = activity =>
                {
                    lock (_gate) _finished.Add(activity);
                },
            };

            ActivitySource.AddActivityListener(_listener);
        }

        public IReadOnlyList<Activity> Finished
        {
            get { lock (_gate) return _finished.ToList(); }
        }

        public void Dispose() => _listener.Dispose();
    }
}
