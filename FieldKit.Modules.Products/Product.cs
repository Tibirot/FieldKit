using System.Text.Json;
using FieldKit.BuildingBlocks;
using FieldKit.Infrastructure;
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
public sealed class Product : AggregateRoot, ITenantOwned, IAuditable, ISyncTracked
{
    /// <summary>Set by the row-version interceptor, never here (ADR-0013). W8 slice 8c.</summary>
    public long RowVersion { get; set; }

    public Guid Id { get; private set; }
    public string Sku { get; private set; } = null!;
    public string Name { get; private set; } = null!;

    /// <summary>The brand this is sold under, if the tenant classifies by brand.</summary>
    public Guid? BrandId { get; private set; }

    /// <summary>Where this sits in the classification tree, if anywhere.</summary>
    public Guid? CategoryId { get; private set; }

    /// <summary>How this is taxed. See the note above about what null means at order time.</summary>
    public Guid? TaxClassId { get; private set; }

    /// <summary>
    /// What one of these is — <c>EA</c>, <c>CS</c>, <c>KG</c>, <c>L</c>.
    /// </summary>
    /// <remarks>
    /// A plain string rather than reference data, for the reason Outlets gives for keeping segment
    /// and banner as strings: nothing branches on it. Pricing is per unit, orders are in units, and
    /// no rule in this module matches on the measure itself — it labels a quantity rather than
    /// deciding anything. The moment something does key off it, it becomes a vocabulary the way
    /// <see cref="Brand"/> is, and that is an additive migration plus a backfill.
    /// </remarks>
    public string? UnitOfMeasure { get; private set; }

    /// <summary>How many selling units are in one of these, when that is a meaningful number.</summary>
    /// <remarks>
    /// Null for anything sold loose or by weight, where "how many are in it" has no answer. When
    /// present it must be positive — a pack of zero is not a small pack, it is a typo, and the
    /// endpoint refuses it.
    /// </remarks>
    public int? PackSize { get; private set; }

    public ProductStatus Status { get; private set; }

    private Dictionary<string, JsonElement> _customFields = [];

    /// <summary>
    /// What this tenant additionally records about a product (<c>CFG-02</c>, ADR-0009).
    /// </summary>
    /// <remarks>
    /// Raw JSON, not a typed shape: what is in here is the tenant's business, described by the
    /// Configuration catalogue rather than by this model. The aggregate stores what it is given —
    /// the endpoint has already checked it against the definitions, which is the only place that
    /// knows what they are.
    /// </remarks>
    public IReadOnlyDictionary<string, JsonElement> CustomFields => _customFields;

    // Set by infrastructure interceptors (tenant / audit).
    public TenantId TenantId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset? ModifiedAtUtc { get; set; }
    public string? ModifiedBy { get; set; }

    private Product() { } // EF

    public static Product Create(
        string sku,
        string name,
        ProductClassification classification,
        ProductAttributes attributes,
        IReadOnlyDictionary<string, JsonElement>? customFields,
        IClock clock)
    {
        var product = new Product { Id = Guid.CreateVersion7(), Sku = sku, Name = name };
        product.Classify(classification);
        product.Describe(attributes);
        product.SetCustomFields(customFields);
        product.Raise(new ProductCreated(Guid.CreateVersion7(), clock.UtcNow, product.Id, sku, name));
        return product;
    }

    /// <summary>Renames, reclassifies and re-describes in one call, the way a form saves.</summary>
    public void Update(
        string name,
        ProductClassification classification,
        ProductAttributes attributes,
        IReadOnlyDictionary<string, JsonElement>? customFields,
        IClock clock)
    {
        Name = name;
        Classify(classification);
        Describe(attributes);
        SetCustomFields(customFields);
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

    /// <remarks>
    /// Values are cloned on the way in. A <see cref="JsonElement"/> borrowed from the request's
    /// <see cref="JsonDocument"/> is only valid while that document lives, and the request is
    /// disposed long before the row is written — storing the borrowed element gives an aggregate
    /// whose contents evaporate.
    /// </remarks>
    private void SetCustomFields(IReadOnlyDictionary<string, JsonElement>? customFields) =>
        _customFields = customFields is null
            ? []
            : customFields.ToDictionary(entry => entry.Key, entry => entry.Value.Clone(), StringComparer.Ordinal);

    private void Describe(ProductAttributes attributes)
    {
        UnitOfMeasure = string.IsNullOrWhiteSpace(attributes.UnitOfMeasure)
            ? null
            : attributes.UnitOfMeasure.Trim();
        PackSize = attributes.PackSize;
        Status = attributes.Status;
    }
}

/// <summary>Whether a product is still sold.</summary>
/// <remarks>
/// <b>Discontinued is not terminal</b>, and that is the difference from <c>OutletStatus.Closed</c>.
/// A shop that has shut down does not reopen, so closing one is a one-way door worth enforcing. A
/// product comes back: seasonal lines return every year, a supplier resumes, a range is reinstated.
/// Making this terminal would mean re-creating the SKU to sell it again — a new id that every
/// historical order line fails to point at, to model something that is genuinely reversible.
/// </remarks>
public enum ProductStatus
{
    Active = 0,
    Discontinued = 1,
}

/// <summary>What a product is, as opposed to how it is classified.</summary>
/// <remarks>
/// Grouped for a weaker reason than <see cref="ProductClassification"/>: these three have different
/// types, so no caller can transpose them the way three consecutive <c>Guid?</c>s invite. This is
/// about keeping <see cref="Product.Create"/> from taking seven positional parameters — worth doing,
/// but do not read it as the same safety argument.
/// </remarks>
public sealed record ProductAttributes(string? UnitOfMeasure, int? PackSize, ProductStatus Status);

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
