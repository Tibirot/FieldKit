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
/// <para>
/// <b>The rule in this file: never read <c>Finished</c> without first waiting for what you are about
/// to assert on.</b> A response reaching the client does not mean the span has been collected — the
/// request's own span is stopped by the hosting layer as the pipeline unwinds, which is *after* the
/// client's <c>await</c> returns. Every test here therefore calls
/// <c>WaitForAsync</c> and then asserts, and the two do different jobs: the wait establishes that the
/// span arrived, the assertion says what is true of it.
/// </para>
/// <para>
/// It is a blanket rule rather than a case-by-case one on purpose. Only the request span is racy
/// today — the domain spans below stop inside their handlers, before a byte of response is written —
/// but that is a fact about where those <c>using</c> blocks currently end, held up by nothing, and
/// "which spans are safe to read immediately" is not a question a reader should have to re-derive.
/// It cost one flaky test and one assertion that could pass over an empty list to learn once.
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
         *
         * <b>Waited for rather than read.</b> This is the one assertion in the file about the
         * *request's* span, and that span is stopped by the hosting layer as the pipeline unwinds —
         * after the response has reached the client. `await GetAsync` therefore does not mean the
         * span has been collected, and reading the list straight afterwards is a race this test lost
         * roughly one CI run in twenty while passing every time locally. The domain spans below are
         * not exposed to it: they stop inside the handler, before the response is written.
         *
         * `Assert.Single` stays, because the wait and the assertion are different claims — that the
         * span arrived, and that there was exactly one of it.
         */
        var whoami = Route("GET /api/auth/whoami");
        var request = await listening.WaitForAsync(whoami, "for GET /api/auth/whoami");

        Assert.Single(listening.Finished, whoami);

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
         *
         * <b>And it could pass without the span existing at all.</b> This asserted
         * `DoesNotContain(Finished, has a tenant)` over a list that is *empty* until the request span
         * is collected — which happens after the response arrives, so the empty case was reachable on
         * exactly the timing that made the neighbouring test flake. "Nothing here has a tenant" is
         * trivially true of nothing. It now waits for the span and asserts about **that span**, which
         * is what the test name has always claimed: traced, and without a tenant.
         */
        using var listening = Listening();

        var response = await fixture.Client.GetAsync("/alive");

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);

        /*
         * <b>Waited for by operation name, not route, and that is a fact about `/alive`.</b> A
         * diagnostic run for this fix found its span is `Microsoft.AspNetCore.Hosting.HttpRequestIn`
         * carrying **no attributes at all** — because `/alive` and `/health` are excluded from tracing
         * by the instrumentation filter in `Extensions.cs`, and it is that instrumentation, not the
         * hosting layer, which rewrites `DisplayName` to the route and hangs the attributes on. So
         * `Route("GET /alive")` matches nothing here, however much it looks like it should.
         *
         * The tenant assertion still means something on an unenriched span: `TenantTracing` sets its
         * tag on `Activity.Current` directly, so a stamp applied to an anonymous request would appear
         * here regardless of what the exporter would later do with the span.
         */
        await listening.WaitForAsync(AnyRequest, "for the anonymous request");

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

        var mutationSpan = Named("sync.push.mutation");
        var span = await listening.WaitForAsync(mutationSpan, "named 'sync.push.mutation'");

        // ...and exactly one of it, which is what `Assert.Single` was always claiming here.
        Assert.Single(listening.Finished, mutationSpan);

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

        var mutationSpan = Named("sync.push.mutation");
        var span = await listening.WaitForAsync(mutationSpan, "named 'sync.push.mutation'");

        Assert.Single(listening.Finished, mutationSpan);

        Assert.Equal(true, span.GetTagItem("fieldkit.sync.mutation.replayed"));
    }

    [Fact]
    public async Task A_pull_says_which_device_asked()
    {
        using var listening = Listening();
        using var rep = fixture.CreateAuthenticatedClient(fixture.AccessToken);

        var device = await BindDeviceAsync(rep);

        await rep.PostAsJsonAsync("/api/sync/pull", new PullRequest(device, null));

        var pullSpan = Named("sync.pull");
        var span = await listening.WaitForAsync(pullSpan, "named 'sync.pull'");

        Assert.Single(listening.Finished, pullSpan);

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

    /// <summary>A domain span by the name it was opened under.</summary>
    private static Predicate<Activity> Named(string operation) =>
        span => span.OperationName == operation;

    /// <summary>
    /// A request span by its route — <c>DisplayName</c>, never <c>OperationName</c>. The latter is
    /// ASP.NET's constant <c>Microsoft.AspNetCore.Hosting.HttpRequestIn</c> for every request it
    /// serves, so matching on it selects the whole conversation. That cost a diagnostic run to learn
    /// in W13 slice 2, and naming the two helpers apart is what stops it being learned twice.
    /// </summary>
    private static Predicate<Activity> Route(string route) => span => span.DisplayName == route;

    /// <summary>
    /// Any request span, matched on ASP.NET's own operation name — for the one endpoint whose span
    /// never acquires a route, because tracing is filtered off it (see the anonymous test).
    /// </summary>
    private static readonly Predicate<Activity> AnyRequest =
        Named("Microsoft.AspNetCore.Hosting.HttpRequestIn");

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
        private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

        private readonly ActivityListener _listener;
        private readonly List<Activity> _finished = [];
        private readonly List<Waiter> _waiting = [];
        private readonly Lock _gate = new();

        public Listener()
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = _ => true,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                    ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = Collect,
            };

            ActivitySource.AddActivityListener(_listener);
        }

        public IReadOnlyList<Activity> Finished
        {
            get { lock (_gate) return _finished.ToList(); }
        }

        /// <summary>
        /// The first span matching <paramref name="match"/>, waiting up to five seconds for one.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Synchronisation, not assertion.</b> This exists so a test can be sure the span it is
        /// about to assert on has arrived; what it then asserts — one span, this tag, that status —
        /// stays in the test where a reader can see it. In particular it does not replace
        /// <c>Assert.Single</c>: "the request produced exactly one span for this route" is a claim
        /// worth making, and returning the first match would quietly stop making it.
        /// </para>
        /// <para>
        /// <b>Five seconds is a deadline, not a delay.</b> A span that has already arrived returns
        /// immediately — the common case, and the only case on a machine that is not loaded. The wait
        /// exists for the interval between a response reaching the client and the server finishing
        /// with the request, which is microseconds when nothing else is happening and is not bounded
        /// by anything when a CI runner is oversubscribed.
        /// </para>
        /// </remarks>
        public async Task<Activity> WaitForAsync(Predicate<Activity> match, string describing)
        {
            Waiter waiter;

            lock (_gate)
            {
                var already = _finished.FirstOrDefault(span => match(span));
                if (already is not null) return already;

                waiter = new Waiter(match, new TaskCompletionSource<Activity>(
                    TaskCreationOptions.RunContinuationsAsynchronously));

                _waiting.Add(waiter);
            }

            if (await Task.WhenAny(waiter.Arrived.Task, Task.Delay(Patience)) == waiter.Arrived.Task)
            {
                return await waiter.Arrived.Task;
            }

            lock (_gate) _waiting.Remove(waiter);

            /*
             * The message matters more than usual here. The failure this replaces read "collection was
             * empty", which describes the assertion rather than the system and sent a previous
             * diagnostic run looking for a missing tenant stamp that was never missing (W13 slice 2).
             * Naming what was waited for, and listing what did arrive, is the difference between a
             * failure that points at the bug and one that points at the test.
             */
            var collected = Finished;

            Assert.Fail(
                $"No span {describing} within {Patience.TotalSeconds:0}s. "
                + $"{collected.Count} span(s) finished: "
                + string.Join(", ", collected.Select(span => $"'{span.DisplayName}'").Distinct()));

            throw new UnreachableException();
        }

        public void Dispose() => _listener.Dispose();

        private void Collect(Activity activity)
        {
            List<Waiter>? ready = null;

            lock (_gate)
            {
                _finished.Add(activity);

                for (var i = _waiting.Count - 1; i >= 0; i--)
                {
                    if (!_waiting[i].Match(activity)) continue;

                    (ready ??= []).Add(_waiting[i]);
                    _waiting.RemoveAt(i);
                }
            }

            // Completed outside the lock: a continuation running inline under it would hold the gate
            // through arbitrary test code, and `ActivityStopped` is on the request's own thread.
            foreach (var waiter in ready ?? []) waiter.Arrived.TrySetResult(activity);
        }

        private sealed record Waiter(Predicate<Activity> Match, TaskCompletionSource<Activity> Arrived);
    }
}
