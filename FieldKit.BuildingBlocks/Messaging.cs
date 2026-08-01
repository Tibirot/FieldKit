namespace FieldKit.BuildingBlocks;

/// <summary>
/// A fact published across a module boundary. Carried in a module's <c>Contracts</c> and delivered
/// via the transactional outbox (ADR-0006). Handlers are idempotent; delivery is at-least-once.
/// </summary>
public interface IIntegrationEvent
{
    Guid Id { get; }

    /// <summary>When the event occurred (UTC, stamped by the publisher via <c>IClock</c>).</summary>
    DateTimeOffset OccurredOn { get; }
}

/// <summary>Handles an integration event published by another module. Must be idempotent.</summary>
public interface IIntegrationEventHandler<in TEvent>
    where TEvent : IIntegrationEvent
{
    Task HandleAsync(TEvent @event, CancellationToken cancellationToken = default);
}

/// <summary>
/// Publishes integration events. Publishing writes to the outbox in the same transaction as the
/// domain change (no dual-write); a dispatcher delivers them to handlers (ADR-0006).
/// </summary>
public interface IEventBus
{
    Task PublishAsync(IIntegrationEvent @event, CancellationToken cancellationToken = default);
}
