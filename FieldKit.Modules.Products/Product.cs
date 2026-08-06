using FieldKit.BuildingBlocks;
using FieldKit.SharedKernel;

namespace FieldKit.Modules.Products;

/// <summary>A product, and how a tenant classifies it (<c>PRD-01</c>).</summary>
/// <remarks>
/// <para>
/// <b>All three classifications are optional</b>, and that is a decision rather than laziness. They
/// point at tenant-authored vocabularies, so requiring them would mean a tenant cannot create its
/// first product until it has built a brand list, a category tree and a set of tax classes. The
/// product is the thing people arrive wanting to enter; the vocabulary is what they grow around it.
/// </para>
/// <para>
/// What an unclassified product costs is bounded and knowable: a promotion scoped to a category will
/// not match it, and an assortment keyed to a brand will not contain it — both correct, and both
/// visible on the screens that author those rules. The one place it is not obviously fine is tax:
/// <c>BR-PRD-5</c> computes tax from the tax class at order time, so a product with none has no rate
/// to apply. **Slice 9 has to decide what that means** — refuse to order it, or treat it as
/// zero-rated — and this comment is here so that decision is made rather than discovered.
/// </para>
/// <para>
/// Unit of measure and pack size are not here yet. They are attributes rather than classification,
/// nothing keys rules off them, and this change is already the size it should be.
/// </para>
/// </remarks>
public sealed class Product : AggregateRoot, ITenantOwned, IAuditable
{
    public Guid Id { get; private set; }
    public string Sku { get; private set; } = null!;
    public string Name { get; private set; } = null!;

    /// <summary>The brand this is sold under, if the tenant classifies by brand.</summary>
    public Guid? BrandId { get; private set; }

    /// <summary>Where this sits in the classification tree, if anywhere.</summary>
    public Guid? CategoryId { get; private set; }

    /// <summary>How this is taxed. See the note above about what null means at order time.</summary>
    public Guid? TaxClassId { get; private set; }

    // Set by infrastructure interceptors (tenant / audit).
    public TenantId TenantId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset? ModifiedAtUtc { get; set; }
    public string? ModifiedBy { get; set; }

    private Product() { } // EF

    public static Product Create(
        string sku, string name, ProductClassification classification, IClock clock)
    {
        var product = new Product { Id = Guid.CreateVersion7(), Sku = sku, Name = name };
        product.Classify(classification);
        product.Raise(new ProductCreated(Guid.CreateVersion7(), clock.UtcNow, product.Id, sku, name));
        return product;
    }

    /// <summary>Renames and reclassifies in one call, the way a form saves.</summary>
    public void Update(string name, ProductClassification classification, IClock clock)
    {
        Name = name;
        Classify(classification);
        ModifiedAtUtc = clock.UtcNow;
    }

    /// <remarks>
    /// The entity does not check that these ids exist — it cannot see the other tables. The endpoint
    /// checks, tenant-filtered, and the database enforces it as a last resort; see
    /// <see cref="ProductsDbContext"/>.
    /// </remarks>
    private void Classify(ProductClassification classification)
    {
        BrandId = classification.BrandId;
        CategoryId = classification.CategoryId;
        TaxClassId = classification.TaxClassId;
    }
}

/// <summary>How a product is classified — the three optional pointers, passed together.</summary>
/// <remarks>
/// Grouped into one type rather than passed as three loose <c>Guid?</c> parameters, which is exactly
/// the signature where two of them get swapped and nothing complains: they are the same type, all
/// nullable, and a caller that transposes brand and category produces a product that saves fine and
/// is wrong everywhere afterwards.
/// </remarks>
public sealed record ProductClassification(Guid? BrandId, Guid? CategoryId, Guid? TaxClassId);

/// <summary>Integration event published when a product is created (delivered via the outbox).</summary>
public sealed record ProductCreated(Guid Id, DateTimeOffset OccurredOn, Guid ProductId, string Sku, string Name)
    : IIntegrationEvent;
