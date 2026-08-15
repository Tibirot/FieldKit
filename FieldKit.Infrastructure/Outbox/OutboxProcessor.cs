using System.Reflection;
using System.Text.Json;
using FieldKit.BuildingBlocks;
using FieldKit.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FieldKit.Infrastructure.Outbox;

/// <summary>
/// Delivers pending outbox messages for a module: claims a batch of unprocessed rows with
/// <c>FOR UPDATE SKIP LOCKED</c> (so multiple replicas never process the same row), rehydrates each
/// event, dispatches it to its <see cref="IIntegrationEventHandler{TEvent}"/> handlers, and marks it
/// processed — all in one transaction. At-least-once delivery; handlers must be idempotent.
/// </summary>
public sealed class OutboxProcessor(IServiceProvider services, IClock clock, OutboxMetrics metrics)
{
    private static readonly MethodInfo DispatchGeneric =
        typeof(OutboxProcessor).GetMethod(nameof(DispatchTypedAsync), BindingFlags.NonPublic | BindingFlags.Static)!;

    /// <summary>
    /// The module a context belongs to, as a tag value — <c>VisitDbContext</c> reads as "Visit".
    /// </summary>
    /// <remarks>
    /// Derived from the type name rather than from <c>ModuleDbContext.Schema</c>, which is protected
    /// and would need widening for a label. The names agree today and the derivation is one line; if
    /// they ever disagree, the schema is the one to expose.
    /// </remarks>
    internal static string ModuleOf<TContext>() where TContext : ModuleDbContext =>
        typeof(TContext).Name.Replace("DbContext", string.Empty, StringComparison.Ordinal);

    /// <summary>
    /// Processes up to <paramref name="batchSize"/> pending messages. Returns how many were handled.
    /// </summary>
    /// <remarks>
    /// <b>The signals are recorded here rather than by the caller</b> (W13 slice 3), because only
    /// this method can see a message's <c>OccurredOn</c> — and the number worth having is the
    /// <i>lag</i> between committing an event and delivering it, not how long a batch took to run.
    /// A caller timing <c>ProcessAsync</c> would measure this server; this measures the promise
    /// <c>ADR-0006</c> asks a reader to accept.
    /// </remarks>
    public async Task<int> ProcessAsync<TContext>(int batchSize = 20, CancellationToken cancellationToken = default)
        where TContext : ModuleDbContext
    {
        using var scope = services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<TContext>();

        var entityType = context.Model.FindEntityType(typeof(OutboxMessage))!;
        var schema = entityType.GetSchema() ?? "public";
        var table = entityType.GetTableName();

        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        var claimSql =
            $"""
             SELECT * FROM "{schema}"."{table}"
             WHERE "ProcessedOnUtc" IS NULL
             ORDER BY "OccurredOnUtc"
             LIMIT {batchSize}
             FOR UPDATE SKIP LOCKED
             """;

        var messages = await context.Set<OutboxMessage>()
            .FromSqlRaw(claimSql)
            .ToListAsync(cancellationToken);

        var module = ModuleOf<TContext>();
        var processed = 0;

        foreach (var message in messages)
        {
            /*
             * A span per message (W13 slice 2 deferred this here, having nothing to instrument).
             *
             * It has no parent: the request that committed the event finished long ago, and its
             * trace is closed. That is the honest shape — an event delivered ten minutes later is
             * not part of the request that raised it — and it is why the message *type* is a tag
             * rather than a metric label, since the trace is where "which event was slow" is asked.
             */
            using var span = OutboxTracing.Dispatch(module, message.Id, message.Type);

            try
            {
                var eventType = System.Type.GetType(message.Type)
                    ?? throw new InvalidOperationException($"Unknown event type '{message.Type}'.");
                var @event = JsonSerializer.Deserialize(message.Content, eventType)
                    ?? throw new InvalidOperationException($"Could not deserialize outbox message {message.Id}.");

                var dispatch = (Task)DispatchGeneric
                    .MakeGenericMethod(eventType)
                    .Invoke(null, [scope.ServiceProvider, @event, cancellationToken])!;
                await dispatch;

                var deliveredAt = clock.UtcNow;

                message.ProcessedOnUtc = deliveredAt;
                message.Error = null;
                processed++;

                // Clamped at zero. The two timestamps come from the same `IClock`, but a fixed clock
                // in a test and a real one across a restart can both produce an event stamped later
                // than its delivery — and a negative duration in a histogram is not a small error,
                // it is a bucket that cannot exist.
                var lag = deliveredAt - message.OccurredOnUtc;
                metrics.Delivered(module, lag > TimeSpan.Zero ? lag : TimeSpan.Zero);
            }
            catch (Exception ex)
            {
                // Leave unprocessed for retry; record the reason (at-least-once).
                message.Error = ex.Message;

                metrics.Failed(module);
                span?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, ex.Message);
            }
        }

        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return processed;
    }

    private static async Task DispatchTypedAsync<TEvent>(
        IServiceProvider serviceProvider, TEvent @event, CancellationToken cancellationToken)
        where TEvent : IIntegrationEvent
    {
        foreach (var handler in serviceProvider.GetServices<IIntegrationEventHandler<TEvent>>())
            await handler.HandleAsync(@event, cancellationToken);
    }
}
