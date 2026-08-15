using FieldKit.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FieldKit.Infrastructure.Outbox;

/// <summary>
/// Drains one module's outbox, forever (<c>ADR-0006</c>) — W13 slice 3.
/// </summary>
/// <remarks>
/// <para>
/// <b>The half of ADR-0006 that was never built.</b> <see cref="OutboxProcessor"/> has claimed,
/// dispatched and marked since W5; nothing called it outside its own test. Nine event types were
/// being committed into tables that only ever grew. Nothing was visibly broken because no module
/// implements <c>IIntegrationEventHandler</c> yet — which is precisely what made it invisible, and
/// what would have made the first handler a silent no-op rather than an error.
/// </para>
/// <para>
/// <b>One per module context, registered by <c>AddModuleDbContext</c> itself.</b> A single dispatcher
/// with a list of contexts would be a list to keep in step, and the module that forgot to join it
/// would fail exactly the way this whole slice exists to fix — quietly. Registering it where the
/// context is registered means a module cannot have an outbox and not have a dispatcher.
/// </para>
/// <para>
/// <b>Drain, then wait.</b> A full batch means more is waiting, so the loop goes straight round
/// again; anything less means the queue is empty and it sleeps. A fixed interval would make the
/// worst case "backlog ÷ batch × interval", which for a burst is minutes of lag for no reason.
/// </para>
/// <para>
/// <b>Claiming is <c>FOR UPDATE SKIP LOCKED</c></b> inside the processor, so running one of these per
/// replica is safe by construction: two dispatchers never claim the same row, and neither waits for
/// the other. That is the mechanism ADR-0006 names, and it is why this needs no leader election.
/// </para>
/// </remarks>
public sealed class OutboxDispatcher<TContext>(
    IServiceProvider services,
    OutboxProcessor processor,
    OutboxMetrics metrics,
    OutboxHeartbeat heartbeat,
    IClock clock,
    ILogger<OutboxDispatcher<TContext>> logger) : BackgroundService
    where TContext : ModuleDbContext
{
    /// <summary>How many messages one claim takes. Small enough that a slow handler cannot hold a transaction open for long.</summary>
    private const int BatchSize = 20;

    /// <summary>How long to wait once the outbox is empty.</summary>
    private static readonly TimeSpan Idle = TimeSpan.FromSeconds(5);

    /// <summary>How long to wait after a cycle threw, rather than spinning on a database that is down.</summary>
    private static readonly TimeSpan AfterFailure = TimeSpan.FromSeconds(30);

    private static readonly string Module = OutboxProcessor.ModuleOf<TContext>();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan wait;

            try
            {
                var processed = await processor.ProcessAsync<TContext>(BatchSize, stoppingToken);
                var pending = await PendingAsync(stoppingToken);

                metrics.Backlog(Module, pending);

                // Stamped on a cycle that *completed*, which is why the catch below does not stamp:
                // a dispatcher that cannot reach its outbox is not delivering, whatever its thread
                // is doing, and a health check should read that as silence rather than as health.
                heartbeat.Beat(Module, clock.UtcNow);

                // A full batch means there is more behind it. Anything less means the queue is dry.
                wait = processed == BatchSize ? TimeSpan.Zero : Idle;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                /*
                 * Caught and slept rather than allowed to escape. An unhandled exception from
                 * `ExecuteAsync` stops the background service permanently — and by default takes the
                 * host with it — so a database blip at three in the morning would end delivery for
                 * the life of the process with a single log line to say so.
                 *
                 * The backlog gauge is what makes this visible: it stops falling. That is the reason
                 * the doc calls it the alertable one.
                 */
                logger.LogError(exception, "Outbox dispatch failed for {Module}; retrying.", Module);
                wait = AfterFailure;
            }

            if (wait > TimeSpan.Zero)
            {
                // Cancellation is the ordinary way this ends, at shutdown — not a fault to report.
                try { await Task.Delay(wait, stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    /// <summary>How many messages this module has committed and not yet delivered.</summary>
    /// <remarks>
    /// Its own scope and its own query rather than a number the processor returns, because "how many
    /// did I just handle" and "how many are left" are different facts — a dispatcher that reported
    /// the first as the second would read zero at exactly the moment a backlog was growing fastest.
    /// </remarks>
    private async Task<int> PendingAsync(CancellationToken cancellationToken)
    {
        using var scope = services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<TContext>();

        return await context.Set<OutboxMessage>()
            .CountAsync(message => message.ProcessedOnUtc == null, cancellationToken);
    }
}
