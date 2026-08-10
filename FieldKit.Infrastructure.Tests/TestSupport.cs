using FieldKit.BuildingBlocks;
using FieldKit.Infrastructure;
using FieldKit.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace FieldKit.Infrastructure.Tests;

/// <summary>A throwaway tenant-owned, auditable aggregate to exercise the persistence + outbox base.</summary>
public class Widget : AggregateRoot, ITenantOwned, IAuditable, ISyncTracked
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public string Name { get; set; } = string.Empty;

    public TenantId TenantId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public string? CreatedBy { get; set; }
    public long RowVersion { get; set; }
    public DateTimeOffset? ModifiedAtUtc { get; set; }
    public string? ModifiedBy { get; set; }

    /// <summary>Raises the integration event that should reach the outbox on save.</summary>
    public void MarkCreated(DateTimeOffset at) =>
        Raise(new WidgetCreated(Guid.CreateVersion7(), at, Id, Name));
}

public sealed record WidgetCreated(Guid Id, DateTimeOffset OccurredOn, Guid WidgetId, string Name)
    : IIntegrationEvent;

/// <summary>Records which events were delivered, so a test can assert exactly-once effect.</summary>
public sealed class EventRecorder
{
    public List<Guid> Handled { get; } = [];
}

public sealed class WidgetCreatedHandler(EventRecorder recorder) : IIntegrationEventHandler<WidgetCreated>
{
    public Task HandleAsync(WidgetCreated @event, CancellationToken cancellationToken = default)
    {
        recorder.Handled.Add(@event.WidgetId);
        return Task.CompletedTask;
    }
}

/// <summary>A minimal module context in schema "test" — stands in for a real module's context.</summary>
public class TestDbContext(DbContextOptions<TestDbContext> options, ITenantContext tenantContext)
    : ModuleDbContext(options, tenantContext)
{
    protected override string Schema => "test";

    /// <summary>Widget is <c>ISyncTracked</c>, so this context needs the counter table.</summary>
    protected override bool TracksSyncChanges => true;

    public DbSet<Widget> Widgets => Set<Widget>();
}

public sealed class FakeTenantContext(TenantId tenantId, string userId) : ITenantContext
{
    public TenantId TenantId => tenantId;
    public string UserId => userId;
    public IReadOnlySet<string> Permissions { get; } = new HashSet<string>();
    public bool Has(string permission) => Permissions.Contains(permission);
}

public sealed class FixedClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset UtcNow => now;
}
