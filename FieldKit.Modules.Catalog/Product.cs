using FieldKit.BuildingBlocks;
using FieldKit.SharedKernel;

namespace FieldKit.Modules.Catalog;

/// <summary>A product in the catalog. The first real aggregate — minimal, to prove the stack end to end.</summary>
public sealed class Product : AggregateRoot, ITenantOwned, IAuditable
{
    public Guid Id { get; private set; }
    public string Sku { get; private set; } = null!;
    public string Name { get; private set; } = null!;

    // Set by infrastructure interceptors (tenant / audit).
    public TenantId TenantId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset? ModifiedAtUtc { get; set; }
    public string? ModifiedBy { get; set; }

    private Product() { } // EF

    public static Product Create(string sku, string name, IClock clock)
    {
        var product = new Product { Id = Guid.CreateVersion7(), Sku = sku, Name = name };
        product.Raise(new ProductCreated(Guid.CreateVersion7(), clock.UtcNow, product.Id, sku, name));
        return product;
    }
}

/// <summary>Integration event published when a product is created (delivered via the outbox).</summary>
public sealed record ProductCreated(Guid Id, DateTimeOffset OccurredOn, Guid ProductId, string Sku, string Name)
    : IIntegrationEvent;
