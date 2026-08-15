using System.Diagnostics.Metrics;
using FieldKit.BuildingBlocks;
using FieldKit.Infrastructure.Outbox;
using FieldKit.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Testcontainers.PostgreSql;

namespace FieldKit.Infrastructure.Tests;

/// <summary>
/// The dispatcher that <c>ADR-0006</c> describes and nothing ran (W13 slice 3).
/// </summary>
/// <remarks>
/// <para>
/// <b>Through a real host, not by calling the loop.</b> What was missing was never the processing —
/// <see cref="OutboxProcessor"/> has worked since W5 and has a test. What was missing was anything
/// <i>starting</i> it, so a test that invoked the dispatcher directly would re-prove the part that
/// already worked and skip the part that did not. These start an <see cref="IHost"/> and wait.
/// </para>
/// <para>
/// The handler is the same <c>WidgetCreatedHandler</c> the processor's test uses. It is still, as of
/// this slice, the <b>only</b> implementation of <c>IIntegrationEventHandler</c> in the solution —
/// which is exactly why the absence of a dispatcher stayed invisible for eight weeks.
/// </para>
/// </remarks>
public class OutboxDispatcherTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine").Build();
    private static readonly DateTimeOffset Committed = new(2026, 8, 2, 9, 0, 0, TimeSpan.Zero);
    private readonly TenantId _tenant = TenantId.New();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact]
    public async Task A_committed_event_is_delivered_without_anybody_asking()
    {
        /*
         * The whole slice, in one assertion. Before this, the row below was written and stayed
         * written: `OutboxProcessor` was registered and invoked by nothing, so an event committed by
         * a request reached its handler only if a test called the processor by hand.
         */
        using var host = Host();

        var widgetId = await CommitAWidgetAsync(host.Services);
        var recorder = host.Services.GetRequiredService<EventRecorder>();

        await host.StartAsync();

        await Eventually(() => recorder.Handled.Contains(widgetId), "the event to be delivered");

        // And the row is marked, so a restart does not deliver it a second time.
        Assert.Equal(0, await PendingAsync(host.Services));

        await host.StopAsync();
    }

    [Fact]
    public async Task The_backlog_is_reported_as_it_drains()
    {
        /*
         * The alertable signal (`observability §2`). It is the only thing that turns "a subscriber
         * stopped working" into something anybody notices: delivery is at-least-once and handlers are
         * idempotent, so a module that stops draining throws nowhere a user can see. The rows just
         * accumulate.
         *
         * Asserted as *reaching zero* rather than as a particular first value — the dispatcher may
         * well have drained the batch before the first observation, and a test that demanded to see
         * the backlog non-zero would be asserting that the system is slow.
         */
        using var host = Host();
        using var recorded = new MeterRecorder();

        await CommitAWidgetAsync(host.Services);
        await host.StartAsync();

        await Eventually(
            () => recorded.Latest("fieldkit.outbox.backlog") == 0,
            "the backlog to be observed at zero");

        // Tagged by module, so a dashboard can say *which* outbox is filling up.
        Assert.Equal("Test", recorded.Tag("fieldkit.outbox.backlog", Telemetry.Tags.Module));

        await host.StopAsync();
    }

    [Fact]
    public async Task Dispatch_latency_measures_the_wait_rather_than_the_work()
    {
        /*
         * `OccurredOn` is the fixed clock's `Committed`; delivery happens at the same instant on the
         * same clock, so the lag here is zero. That is the point: a histogram timing the *batch*
         * would report a millisecond or two of real work, and a histogram timing the *lag* reports
         * the eventual-consistency window — which is what `ADR-0006` asks a reader to accept and the
         * only one of the two that would show a queue falling behind.
         */
        using var host = Host();
        using var recorded = new MeterRecorder();

        await CommitAWidgetAsync(host.Services);
        await host.StartAsync();

        await Eventually(
            () => recorded.Any("fieldkit.outbox.dispatch.latency"),
            "a delivery to be measured");

        Assert.Equal(0, recorded.Latest("fieldkit.outbox.dispatch.latency"));

        await host.StopAsync();
    }

    [Fact]
    public async Task A_handler_that_throws_leaves_the_message_for_another_attempt()
    {
        /*
         * At-least-once, from the other side. A subscriber that fails must not consume the event —
         * and the failure has to be *countable*, because a backlog that is high from a burst and one
         * that is high from a broken handler need different answers and look identical otherwise.
         */
        using var host = Host(handler: new Angry());
        using var recorded = new MeterRecorder();

        var widgetId = await CommitAWidgetAsync(host.Services);

        await host.StartAsync();

        await Eventually(
            () => recorded.Any("fieldkit.outbox.dispatch.failed"),
            "the failure to be counted");

        // Still pending, and it will be tried again — the row is the queue.
        Assert.Equal(1, await PendingAsync(host.Services));
        Assert.DoesNotContain(widgetId, host.Services.GetRequiredService<EventRecorder>().Handled);

        await host.StopAsync();
    }

    /// <summary>A host wired the way a module is, with the dispatcher `AddModuleDbContext` registers.</summary>
    private IHost Host(IIntegrationEventHandler<WidgetCreated>? handler = null)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();

        builder.Services.AddSingleton<IClock>(new FixedClock(Committed));
        builder.Services.AddScoped<ITenantContext>(_ => new FakeTenantContext(_tenant, "maria"));
        builder.Services.AddSingleton<EventRecorder>();
        builder.Services.AddModuleDbContext<TestDbContext>(_postgres.GetConnectionString(), "test");

        if (handler is null)
            builder.Services.AddScoped<IIntegrationEventHandler<WidgetCreated>, WidgetCreatedHandler>();
        else
            builder.Services.AddSingleton(handler);

        var host = builder.Build();

        using var scope = host.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TestDbContext>().Database.EnsureCreated();

        return host;
    }

    private async Task<Guid> CommitAWidgetAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<TestDbContext>();

        var widget = new Widget { Name = "Shelf strip" };
        widget.MarkCreated(Committed);

        context.Widgets.Add(widget);
        await context.SaveChangesAsync();

        return widget.Id;
    }

    private static async Task<int> PendingAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<TestDbContext>();

        return await context.Set<OutboxMessage>().CountAsync(message => message.ProcessedOnUtc == null);
    }

    /// <summary>
    /// Waits for a background loop to get there, or fails saying what it was waiting for.
    /// </summary>
    /// <remarks>
    /// Polling rather than a fixed sleep, because the two failure modes are different: a sleep long
    /// enough to be reliable makes every run slow, and a sleep short enough to be fast makes the
    /// suite flaky on a loaded CI box. The message matters — "timed out" says nothing, and this is
    /// the kind of test that fails months later for an unrelated reason.
    /// </remarks>
    private static async Task Eventually(Func<bool> condition, string what)
    {
        var deadline = TimeSpan.FromSeconds(20);
        var waited = TimeSpan.Zero;
        var step = TimeSpan.FromMilliseconds(50);

        while (waited < deadline)
        {
            if (condition()) return;

            await Task.Delay(step);
            waited += step;
        }

        Assert.Fail($"Waited {deadline.TotalSeconds:0}s for {what} and it did not happen.");
    }

    /// <summary>A handler that refuses, so the retry path can be asserted.</summary>
    private sealed class Angry : IIntegrationEventHandler<WidgetCreated>
    {
        public Task HandleAsync(WidgetCreated @event, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("This subscriber is broken.");
    }

    /// <summary>Collects what the FieldKit meter publishes, the way an exporter would.</summary>
    private sealed class MeterRecorder : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly List<(string Instrument, double Value, Dictionary<string, string?> Tags)> _readings = [];
        private readonly Lock _gate = new();

        public MeterRecorder()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == Telemetry.MeterName)
                    listener.EnableMeasurementEvents(instrument);
            };

            _listener.SetMeasurementEventCallback<int>((i, m, t, _) => Add(i.Name, m, t));
            _listener.SetMeasurementEventCallback<long>((i, m, t, _) => Add(i.Name, m, t));
            _listener.SetMeasurementEventCallback<double>((i, m, t, _) => Add(i.Name, m, t));

            _listener.Start();
        }

        public bool Any(string instrument)
        {
            lock (_gate) return _readings.Any(reading => reading.Instrument == instrument);
        }

        /// <summary>The most recent value for an instrument, or null if it has published nothing.</summary>
        public double? Latest(string instrument)
        {
            lock (_gate)
                return _readings.LastOrDefault(reading => reading.Instrument == instrument) is { Instrument: not null } last
                    ? last.Value
                    : null;
        }

        public string? Tag(string instrument, string key)
        {
            lock (_gate)
                return _readings.Last(reading => reading.Instrument == instrument).Tags[key];
        }

        public void Dispose() => _listener.Dispose();

        private void Add(string instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var flattened = new Dictionary<string, string?>(tags.Length);

            foreach (var tag in tags) flattened[tag.Key] = tag.Value?.ToString();

            lock (_gate) _readings.Add((instrument, value, flattened));
        }
    }
}
