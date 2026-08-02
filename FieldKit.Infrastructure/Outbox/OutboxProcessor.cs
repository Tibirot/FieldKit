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
public sealed class OutboxProcessor(IServiceProvider services, IClock clock)
{
    private static readonly MethodInfo DispatchGeneric =
        typeof(OutboxProcessor).GetMethod(nameof(DispatchTypedAsync), BindingFlags.NonPublic | BindingFlags.Static)!;

    /// <summary>Processes up to <paramref name="batchSize"/> pending messages. Returns how many were handled.</summary>
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

        var processed = 0;
        foreach (var message in messages)
        {
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

                message.ProcessedOnUtc = clock.UtcNow;
                message.Error = null;
                processed++;
            }
            catch (Exception ex)
            {
                // Leave unprocessed for retry; record the reason (at-least-once).
                message.Error = ex.Message;
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
