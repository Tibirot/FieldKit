namespace FieldKit.BuildingBlocks;

/// <summary>An aggregate that raises integration events for other modules to react to.</summary>
public interface IHasIntegrationEvents
{
    IReadOnlyList<IIntegrationEvent> IntegrationEvents { get; }
    void ClearIntegrationEvents();
}

/// <summary>
/// Base for aggregate roots. Events raised here are **not dispatched inline** — on save they are
/// written to the transactional outbox in the *same* transaction as the state change (no
/// dual-write), then delivered by the outbox processor (ADR-0006).
/// </summary>
public abstract class AggregateRoot : IHasIntegrationEvents
{
    private readonly List<IIntegrationEvent> _integrationEvents = [];

    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public IReadOnlyList<IIntegrationEvent> IntegrationEvents => _integrationEvents.AsReadOnly();

    protected void Raise(IIntegrationEvent @event) => _integrationEvents.Add(@event);

    public void ClearIntegrationEvents() => _integrationEvents.Clear();
}
