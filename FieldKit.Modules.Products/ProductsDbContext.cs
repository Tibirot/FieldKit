using System.Text.Json;
using FieldKit.BuildingBlocks;
using FieldKit.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace FieldKit.Modules.Products;

/// <summary>The Products module's context — owns the <c>products</c> schema (schema-per-module).</summary>
public sealed class ProductsDbContext(DbContextOptions<ProductsDbContext> options, ITenantContext tenantContext)
    : ModuleDbContext(options, tenantContext)
{
    public const string SchemaName = "products";

    protected override string Schema => SchemaName;

    public DbSet<Product> Products => Set<Product>();

    public DbSet<Category> Categories => Set<Category>();

    public DbSet<Brand> Brands => Set<Brand>();

    public DbSet<TaxClass> TaxClasses => Set<TaxClass>();

    public DbSet<AssortmentItem> AssortmentItems => Set<AssortmentItem>();

    public DbSet<OutletAssortmentOverride> AssortmentOverrides => Set<OutletAssortmentOverride>();

    public DbSet<PriceList> PriceLists => Set<PriceList>();

    public DbSet<PriceListLine> PriceListLines => Set<PriceListLine>();

    public DbSet<PriceListAssignment> PriceListAssignments => Set<PriceListAssignment>();

    public DbSet<Promotion> Promotions => Set<Promotion>();

    public DbSet<PromotionTarget> PromotionTargets => Set<PromotionTarget>();

    public DbSet<PromotionTier> PromotionTiers => Set<PromotionTier>();

    public DbSet<PromotionAssignment> PromotionAssignments => Set<PromotionAssignment>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Product>(product =>
        {
            product.ToTable("product");
            product.HasKey(p => p.Id);
            product.Property(p => p.Sku).HasMaxLength(64).IsRequired();
            product.Property(p => p.Name).HasMaxLength(200).IsRequired();
            product.Property(p => p.UnitOfMeasure).HasMaxLength(16);

            // Stored as its integer value, not its name: a string column would turn renaming an enum
            // member into a data migration. The wire is the other way round — `ProductResponse`
            // carries a `JsonStringEnumConverter`, so clients see "Active" rather than 0 and never
            // depend on the ordinal this column keeps.
            //
            // Which means the ordinals are now storage, and members must be *appended* rather than
            // inserted. Adding a status between Active and Discontinued would renumber every stored
            // row's meaning without touching a single one of them.
            product.Property(p => p.Status).HasConversion<int>();

            // jsonb, and stored as a dictionary of raw JSON elements — what is inside is the
            // tenant's business, described by the Configuration catalogue rather than by this model.
            // The same shape Outlets uses, deliberately: two entities carrying tenant-defined fields
            // should store them the same way, or the sync engine has two problems instead of one.
            product.Property(p => p.CustomFields)
                .HasColumnName("custom_fields")
                .HasColumnType("jsonb")
                .HasConversion(
                    fields => JsonSerializer.Serialize(fields, (JsonSerializerOptions?)null),
                    json => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json, (JsonSerializerOptions?)null)!,
                    new ValueComparer<IReadOnlyDictionary<string, JsonElement>>(
                        // Compared by serialized form: JsonElement has no value equality, so without
                        // this EF would compare references and miss every edit to a custom field.
                        (left, right) => JsonSerializer.Serialize(left, (JsonSerializerOptions?)null)
                            == JsonSerializer.Serialize(right, (JsonSerializerOptions?)null),
                        fields => JsonSerializer.Serialize(fields, (JsonSerializerOptions?)null).GetHashCode(),
                        fields => fields));
            product.HasIndex(p => new { p.TenantId, p.Sku }).IsUnique(); // SKU unique within a tenant

            // The three classification pointers, each keyed on the tenant as well as the id — the
            // pattern established for Category's parent and since applied to OrgUnit. A plain
            // `BrandId -> Id` key is tenant-agnostic and would accept another tenant's brand; with
            // the tenant in the key the rule is in the table rather than only in the endpoint.
            //
            // Restrict, so a vocabulary entry cannot be deleted out from under the products using
            // it. The endpoints refuse first with a count and a code; this is what catches anything
            // that reaches the table another way.
            //
            // Postgres MATCH SIMPLE means a composite key with any NULL column is not checked, so an
            // unclassified product — all three null — skips all three constraints. That is the
            // behaviour the optional classification needs, and it falls out rather than being
            // arranged.
            product.HasOne<Brand>()
                .WithMany()
                .HasForeignKey(p => new { p.TenantId, p.BrandId })
                .HasPrincipalKey(b => new { b.TenantId, b.Id })
                .OnDelete(DeleteBehavior.Restrict);

            product.HasOne<Category>()
                .WithMany()
                .HasForeignKey(p => new { p.TenantId, p.CategoryId })
                .HasPrincipalKey(c => new { c.TenantId, c.Id })
                .OnDelete(DeleteBehavior.Restrict);

            product.HasOne<TaxClass>()
                .WithMany()
                .HasForeignKey(p => new { p.TenantId, p.TaxClassId })
                .HasPrincipalKey(t => new { t.TenantId, t.Id })
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Category>(category =>
        {
            category.ToTable("category");
            category.HasKey(c => c.Id);
            category.Property(c => c.Name).HasMaxLength(120).IsRequired();

            // Unique among siblings, not tenant-wide. "Water" under Beverages and "Water" under
            // Cleaning are two different things and both are correct; a tenant-wide constraint would
            // refuse the second and force a naming convention on a tree that already disambiguates
            // by position. `TenantId` leads because the filter is always by tenant first.
            //
            // Postgres treats NULLs as distinct in a unique index, so this does NOT constrain roots
            // (ParentId is null there) — two roots may share a name. The endpoint checks that case
            // in code; see NameTakenProblem.
            category.HasIndex(c => new { c.TenantId, c.ParentId, c.Name }).IsUnique();

            // Self-referencing, restricted, and keyed on the tenant as well as the id.
            //
            // The endpoint already checks that a parent exists and that a category with children is
            // not deleted, so this looks redundant. It is not: those checks read and then write, and
            // between the two the world can change. Create a child under X while another request
            // deletes X and both pass their checks, leaving a category whose parent is gone — an
            // orphan invisible to any tree built from parent pointers, because its root points
            // nowhere. Only the database can close that window.
            //
            // **The tenant belongs in the key.** A plain `ParentId -> Id` foreign key is
            // tenant-agnostic: it is satisfied by *any* tenant's category, so it would happily
            // accept a parent belonging to someone else. The tenant-filtered check in the endpoint
            // is what refuses that today, which means the strongest isolation guarantee in the
            // module would rest entirely on application code. Keying the relationship
            // `(TenantId, ParentId) -> (TenantId, Id)` puts it in the table, where a bug in a future
            // code path cannot get around it. Organization keys org units on the id alone; this goes
            // one better, and the same is worth doing there.
            //
            // Postgres uses MATCH SIMPLE, so a composite foreign key with any NULL column is not
            // checked at all. `ParentId` is null exactly for roots and `TenantId` never is, so roots
            // skip the constraint — which is what should happen, since a root has no parent to
            // verify.
            //
            // No navigation property: the constraint is what is wanted, not a traversal. An
            // `ICollection<Category> Children` invites callers to walk the tree entity-by-entity and
            // makes the aggregate's boundary a suggestion.
            //
            // Restrict rather than Cascade, deliberately. A cascade would delete an entire branch —
            // and every product's grouping under it — because someone removed the node above it.
            // The endpoint refuses first and says how many children are in the way; this is the
            // backstop for when something reaches the table another way.
            category.HasOne<Category>()
                .WithMany()
                .HasForeignKey(c => new { c.TenantId, c.ParentId })
                .HasPrincipalKey(c => new { c.TenantId, c.Id })
                .OnDelete(DeleteBehavior.Restrict);

            // The list endpoint reads every category for a tenant, and the delete path asks whether
            // one has children. Both are parent lookups.
            category.HasIndex(c => new { c.TenantId, c.ParentId });
        });

        // Brand and TaxClass are flat named vocabularies with the same shape, so they are configured
        // the same way: unique on (TenantId, Name), which — unlike Category's sibling rule — has no
        // nullable column in it and therefore needs no in-code companion check. Postgres's
        // NULL-distinctness only bites when a key column can be null.
        modelBuilder.Entity<Brand>(brand =>
        {
            brand.ToTable("brand");
            brand.HasKey(b => b.Id);
            brand.Property(b => b.Name).HasMaxLength(120).IsRequired();
            brand.HasIndex(b => new { b.TenantId, b.Name }).IsUnique();
        });

        modelBuilder.Entity<TaxClass>(taxClass =>
        {
            taxClass.ToTable("tax_class");
            taxClass.HasKey(t => t.Id);
            taxClass.Property(t => t.Name).HasMaxLength(120).IsRequired();
            taxClass.HasIndex(t => new { t.TenantId, t.Name }).IsUnique();
        });

        modelBuilder.Entity<AssortmentItem>(item =>
        {
            item.ToTable("assortment_item");
            item.HasKey(i => i.Id);

            // A product appears at most once per channel. Without this, "add product X to channel Y"
            // run twice leaves two rows, and every question asked of the assortment — is X in it, how
            // many must-stock lines are there — starts returning duplicates.
            item.HasIndex(i => new { i.TenantId, i.ChannelId, i.ProductId }).IsUnique();

            // Tenant-keyed to the product, the pattern established for Category's parent. The
            // channel gets no key at all: it lives in Outlets, and a foreign key across a module
            // boundary is precisely the coupling schema-per-module exists to prevent. The endpoint
            // checks it through IOutletClassification instead.
            item.HasOne<Product>()
                .WithMany()
                .HasForeignKey(i => new { i.TenantId, i.ProductId })
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .OnDelete(DeleteBehavior.Restrict);

            // The read this table exists for: everything in a channel, must-stock first so a
            // suggested-order list does not have to sort it again.
            item.HasIndex(i => new { i.TenantId, i.ChannelId, i.IsMustStock });
        });

        modelBuilder.Entity<OutletAssortmentOverride>(exception =>
        {
            exception.ToTable("outlet_assortment_override");
            exception.HasKey(o => o.Id);
            exception.Property(o => o.Kind).HasConversion<int>();

            // One override per (outlet, product). Two would be a shop where the same product is both
            // added and removed — a state with no answer, where every read has to pick one.
            exception.HasIndex(o => new { o.TenantId, o.OutletId, o.ProductId }).IsUnique();

            // Tenant-keyed to the product, as everywhere else. OutletId gets no key: it lives in
            // Outlets, and a constraint across a module boundary is the coupling schema-per-module
            // exists to prevent — the endpoint checks it through IOutletClassification instead.
            exception.HasOne<Product>()
                .WithMany()
                .HasForeignKey(o => new { o.TenantId, o.ProductId })
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .OnDelete(DeleteBehavior.Restrict);

            // The read this exists for: every override for one outlet, fetched alongside its
            // channel's assortment.
            exception.HasIndex(o => new { o.TenantId, o.OutletId });
        });

        modelBuilder.Entity<PriceList>(list =>
        {
            list.ToTable("price_list");
            list.HasKey(l => l.Id);
            list.Property(l => l.Name).HasMaxLength(120).IsRequired();

            // Exactly three characters, and stored as such. A varchar(3) is the schema saying what
            // the type says — ISO-4217 alphabetic, nothing else — rather than leaving room for
            // "Euro" to be stored and refused later.
            list.Property(l => l.Currency).HasMaxLength(3).IsRequired().IsFixedLength();

            list.HasIndex(l => new { l.TenantId, l.Name }).IsUnique();

            // Resolution reads by date, and every read is tenant-first.
            list.HasIndex(l => new { l.TenantId, l.EffectiveFrom });
        });

        modelBuilder.Entity<PriceListLine>(line =>
        {
            line.ToTable("price_list_line");
            line.HasKey(l => l.Id);

            // numeric(18,4), not float. The whole decimal-parity regime (BR-PRD-8/9) is worthless if
            // the storage rounds before the engine ever sees the number. Four decimal places rather
            // than two because a unit price in FMCG is routinely sub-cent — a case of 24 at 11.99
            // divides to 0.4996 per unit, and truncating that at the column loses the money the
            // rounding policy exists to control.
            line.Property(l => l.Amount).HasPrecision(18, 4);

            // A product is priced at most once per list. Two lines would make "the price" a question
            // with two answers, and the resolver would have to break a tie that means nothing.
            line.HasIndex(l => new { l.TenantId, l.PriceListId, l.ProductId }).IsUnique();

            // Tenant-keyed to both parents, the pattern from #96. Restrict on the product so a
            // priced product cannot vanish from under a list; Cascade on the list, because a line
            // has no meaning without the list that gives it a currency — deleting the list and
            // keeping its lines would leave amounts with no units.
            line.HasOne<PriceList>()
                .WithMany()
                .HasForeignKey(l => new { l.TenantId, l.PriceListId })
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .OnDelete(DeleteBehavior.Cascade);

            line.HasOne<Product>()
                .WithMany()
                .HasForeignKey(l => new { l.TenantId, l.ProductId })
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<PriceListAssignment>(assignment =>
        {
            // Exactly one scope, enforced by the database rather than only by the endpoint. A row
            // with both a channel and an outlet is a rule with two scopes and no meaning; a row with
            // neither applies nowhere. Neither is a state a reader could sensibly handle, so they
            // are made unrepresentable instead of defensively skipped everywhere.
            assignment.ToTable("price_list_assignment", table => table.HasCheckConstraint(
                "ck_price_list_assignment_one_scope",
                """("channel_id" IS NULL) <> ("outlet_id" IS NULL)"""));

            assignment.HasKey(a => a.Id);
            assignment.Property(a => a.ChannelId).HasColumnName("channel_id");
            assignment.Property(a => a.OutletId).HasColumnName("outlet_id");

            // A channel is assigned a given list at most once, and so is an outlet. Postgres treats
            // NULLs as distinct, so each index only constrains the rows where its column is set —
            // which is exactly the half it is about, and why two partial-by-accident indexes are
            // correct here rather than one over both columns.
            assignment.HasIndex(a => new { a.TenantId, a.PriceListId, a.ChannelId }).IsUnique();
            assignment.HasIndex(a => new { a.TenantId, a.PriceListId, a.OutletId }).IsUnique();

            // Resolution asks "which lists apply to this outlet, and to its channel" — both are
            // lookups by scope rather than by list.
            assignment.HasIndex(a => new { a.TenantId, a.ChannelId });
            assignment.HasIndex(a => new { a.TenantId, a.OutletId });

            // Cascade: an assignment is a statement about a list, and it says nothing once the list
            // is gone. The price lines cascade for the same reason.
            assignment.HasOne<PriceList>()
                .WithMany()
                .HasForeignKey(a => new { a.TenantId, a.PriceListId })
                .HasPrincipalKey(l => new { l.TenantId, l.Id })
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Promotion>(promotion =>
        {
            // Each type says what it carries, and the constraint says which. Writing `value` for the
            // percent_off / amount_off / currency trio and `bundle` for buy_quantity / get_quantity /
            // get_percent_off:
            //     PercentOff     → percent_off set, no currency, no bundle
            //     FixedAmountOff → amount_off and currency set, no bundle
            //     VolumeTiered   → no value, no bundle; its discounts live on promotion_tier
            //     BuyXGetY       → no value; bundle set, because it gives rather than reduces
            //     anything else  → impossible
            //
            // `ELSE FALSE`, and that is a change worth noticing. The clause was `ELSE TRUE` while the
            // types arrived one slice at a time — deliberate room, so each new type was a new WHEN
            // rather than an ALTER reasoned about against rows already stored. It cost an
            // unconstrained escape for any unrecognised `type` string, flagged as the price of the
            // approach at the time. B1 names exactly four types and all four are now here, so the
            // room is no longer needed and the escape closes with it.
            //
            // Kept on one line on purpose. EF stores this string verbatim in the migration *and* the
            // model snapshot, then compares them to decide whether the model has changed — so a
            // multi-line literal bakes the authoring machine's line endings into the schema, and the
            // same source regenerated on Linux produces a different constraint and a phantom
            // migration. The readable version is the five lines above.
            promotion.ToTable("promotion", table => table.HasCheckConstraint(
                "ck_promotion_value_matches_type",
                """CASE "type" WHEN 'PercentOff' THEN "percent_off" IS NOT NULL AND "amount_off" IS NULL AND "currency" IS NULL AND "buy_quantity" IS NULL AND "get_quantity" IS NULL AND "get_percent_off" IS NULL WHEN 'FixedAmountOff' THEN "amount_off" IS NOT NULL AND "currency" IS NOT NULL AND "percent_off" IS NULL AND "buy_quantity" IS NULL AND "get_quantity" IS NULL AND "get_percent_off" IS NULL WHEN 'VolumeTiered' THEN "percent_off" IS NULL AND "amount_off" IS NULL AND "currency" IS NULL AND "buy_quantity" IS NULL AND "get_quantity" IS NULL AND "get_percent_off" IS NULL WHEN 'BuyXGetY' THEN "percent_off" IS NULL AND "amount_off" IS NULL AND "currency" IS NULL AND "buy_quantity" IS NOT NULL AND "get_quantity" IS NOT NULL AND "get_percent_off" IS NOT NULL ELSE FALSE END"""));

            promotion.HasKey(p => p.Id);
            promotion.Property(p => p.Name).HasMaxLength(120).IsRequired();

            // As a string, so the constraint above reads as the rule it is. See PromotionType.
            promotion.Property(p => p.Type)
                .HasColumnName("type").HasConversion<string>().HasMaxLength(20).IsRequired();

            // numeric(5,2): a percentage needs room for 100.00 and for the fractional points a trade
            // deal is actually written in ("12.5% off"), and nothing beyond. The narrower column is
            // the schema refusing a money amount typed into the wrong field.
            promotion.Property(p => p.PercentOff).HasColumnName("percent_off").HasPrecision(5, 2);

            // numeric(18,4), matching PriceListLine — a discount is money, and it is compared and
            // subtracted against amounts stored at that precision.
            promotion.Property(p => p.AmountOff).HasColumnName("amount_off").HasPrecision(18, 4);
            promotion.Property(p => p.Currency).HasColumnName("currency").HasMaxLength(3).IsFixedLength();

            promotion.Property(p => p.BuyQuantity).HasColumnName("buy_quantity");
            promotion.Property(p => p.GetQuantity).HasColumnName("get_quantity");
            promotion.Property(p => p.GetPercentOff).HasColumnName("get_percent_off").HasPrecision(5, 2);
            promotion.Property(p => p.GetProductId).HasColumnName("get_product_id");

            promotion.HasIndex(p => new { p.TenantId, p.Name }).IsUnique();

            // Resolution asks for the promotions live on a date, best priority first.
            promotion.HasIndex(p => new { p.TenantId, p.ValidFrom, p.Priority });

            // Restrict, like every other product reference in this module: a product cannot vanish
            // from under a promotion that promises to give it away. Tenant-keyed, so the FK cannot be
            // satisfied by another tenant's product — a single-column reference here would be the one
            // place a promotion could point across the boundary.
            promotion.HasOne<Product>()
                .WithMany()
                .HasForeignKey(p => new { p.TenantId, p.GetProductId })
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<PromotionTarget>(target =>
        {
            // Exactly one target, the same shape and the same argument as price_list_assignment.
            target.ToTable("promotion_target", table => table.HasCheckConstraint(
                "ck_promotion_target_one_subject",
                """("product_id" IS NULL) <> ("category_id" IS NULL)"""));

            target.HasKey(t => t.Id);
            target.Property(t => t.ProductId).HasColumnName("product_id");
            target.Property(t => t.CategoryId).HasColumnName("category_id");

            // A promotion names a given product at most once, and a given category at most once.
            // Postgres treats NULLs as distinct, so each index constrains only the rows where its
            // column is set — which is the half it is about.
            target.HasIndex(t => new { t.TenantId, t.PromotionId, t.ProductId }).IsUnique();
            target.HasIndex(t => new { t.TenantId, t.PromotionId, t.CategoryId }).IsUnique();

            // Resolution asks "which promotions target this product, or a category above it".
            target.HasIndex(t => new { t.TenantId, t.ProductId });
            target.HasIndex(t => new { t.TenantId, t.CategoryId });

            // Cascade from the promotion: a target says nothing once the rule is gone. Restrict on
            // both subjects, so a product or a category cannot vanish from under a live promotion —
            // the same split PriceListLine makes between its list and its product.
            target.HasOne<Promotion>()
                .WithMany()
                .HasForeignKey(t => new { t.TenantId, t.PromotionId })
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .OnDelete(DeleteBehavior.Cascade);

            target.HasOne<Product>()
                .WithMany()
                .HasForeignKey(t => new { t.TenantId, t.ProductId })
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .OnDelete(DeleteBehavior.Restrict);

            target.HasOne<Category>()
                .WithMany()
                .HasForeignKey(t => new { t.TenantId, t.CategoryId })
                .HasPrincipalKey(c => new { c.TenantId, c.Id })
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<PromotionTier>(tier =>
        {
            // Self-describing, under the same rule the flat promotion follows: exactly one kind of
            // discount, and a currency exactly when there is an amount to give units to. Expressed
            // over the columns rather than as a CASE because a tier has no type of its own — the two
            // shapes here are all there will ever be.
            tier.ToTable("promotion_tier", table => table.HasCheckConstraint(
                "ck_promotion_tier_value",
                """(("percent_off" IS NULL) <> ("amount_off" IS NULL)) AND (("amount_off" IS NULL) = ("currency" IS NULL))"""));

            tier.HasKey(t => t.Id);
            tier.Property(t => t.MinQuantity).HasColumnName("min_quantity");
            tier.Property(t => t.PercentOff).HasColumnName("percent_off").HasPrecision(5, 2);
            tier.Property(t => t.AmountOff).HasColumnName("amount_off").HasPrecision(18, 4);
            tier.Property(t => t.Currency).HasColumnName("currency").HasMaxLength(3).IsFixedLength();

            // One tier per threshold. Two rows at the same quantity would make "the discount at 24"
            // a question with two answers, and resolution would have to break a tie that means
            // nothing — the same reason a product is priced at most once per list.
            tier.HasIndex(t => new { t.TenantId, t.PromotionId, t.MinQuantity }).IsUnique();

            // Cascade: a tier is a statement about a promotion and says nothing once it is gone.
            tier.HasOne<Promotion>()
                .WithMany()
                .HasForeignKey(t => new { t.TenantId, t.PromotionId })
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PromotionAssignment>(assignment =>
        {
            // The same shape as price_list_assignment, down to the constraint name pattern — see
            // PromotionAssignment for why these are two tables rather than one shared one.
            assignment.ToTable("promotion_assignment", table => table.HasCheckConstraint(
                "ck_promotion_assignment_one_scope",
                """("channel_id" IS NULL) <> ("outlet_id" IS NULL)"""));

            assignment.HasKey(a => a.Id);
            assignment.Property(a => a.ChannelId).HasColumnName("channel_id");
            assignment.Property(a => a.OutletId).HasColumnName("outlet_id");

            // A channel gets a given promotion at most once, and so does an outlet. Postgres treats
            // NULLs as distinct, so each index constrains only the rows where its column is set.
            assignment.HasIndex(a => new { a.TenantId, a.PromotionId, a.ChannelId }).IsUnique();
            assignment.HasIndex(a => new { a.TenantId, a.PromotionId, a.OutletId }).IsUnique();

            // Resolution asks "which promotions reach this outlet, and its channel" — both are
            // lookups by scope rather than by promotion.
            assignment.HasIndex(a => new { a.TenantId, a.ChannelId });
            assignment.HasIndex(a => new { a.TenantId, a.OutletId });

            assignment.HasOne<Promotion>()
                .WithMany()
                .HasForeignKey(a => new { a.TenantId, a.PromotionId })
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
