using System.Text.Json;
using FieldKit.BuildingBlocks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace FieldKit.Infrastructure.Outbox;

/// <summary>
/// The outbox write-path: before save, drains integration events from tracked aggregates into
/// <see cref="OutboxMessage"/> rows on the same context — so they commit in the **same transaction**
/// as the state change (no dual-write, ADR-0006).
/// </summary>
public sealed class ConvertIntegrationEventsToOutboxInterceptor : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData, InterceptionResult<int> result)
    {
        if (eventData.Context is not null) DrainToOutbox(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is not null) DrainToOutbox(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private static void DrainToOutbox(DbContext context)
    {
        var aggregates = context.ChangeTracker
            .Entries<IHasIntegrationEvents>()
            .Where(entry => entry.Entity.IntegrationEvents.Count > 0)
            .Select(entry => entry.Entity)
            .ToList();

        foreach (var aggregate in aggregates)
        {
            foreach (var @event in aggregate.IntegrationEvents)
            {
                context.Set<OutboxMessage>().Add(new OutboxMessage
                {
                    Id = @event.Id,
                    Type = @event.GetType().AssemblyQualifiedName!,
                    Content = JsonSerializer.Serialize(@event, @event.GetType()),
                    OccurredOnUtc = @event.OccurredOn,
                });
            }

            aggregate.ClearIntegrationEvents();
        }
    }
}
