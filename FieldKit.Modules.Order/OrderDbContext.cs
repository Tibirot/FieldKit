using FieldKit.BuildingBlocks;
using FieldKit.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FieldKit.Modules.Order;

/// <summary>The Order module's context — owns the <c>ordering</c> schema (ADR-0005).</summary>
/// <remarks>
/// <b>Not <c>order</c>.</b> `ORDER` is a reserved word in SQL, and while Postgres will accept a
/// quoted schema of that name, every hand-written query, every `psql` session and every migration
/// script would need the quotes forever — and the one that forgets fails somewhere unhelpful. The
/// module is Order; the schema is `ordering`, and this is the only place the two differ.
/// </remarks>
public sealed class OrderDbContext(DbContextOptions<OrderDbContext> options, ITenantContext tenantContext)
    : ModuleDbContext(options, tenantContext)
{
    public const string SchemaName = "ordering";

    protected override string Schema => SchemaName;

    /// <summary>
    /// A row-version counter, arriving exactly when the debt above said it would (ADR-0013).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Orders are the <b>one transactional record that flows back down</b> (order spec F4): a
    /// rejected order returns to the rep's device so the work is never stranded. W11 deferred the
    /// counter on the argument that "a store with no writer is a schema version spent on nothing",
    /// and noted that unlike <c>Source</c> on <c>Visit</c> this one <i>could</i> arrive later
    /// without loss — a device that has never pulled an order has no watermark to be wrong about,
    /// so it takes everything on its first pull whatever the versions say.
    /// </para>
    /// <para>
    /// <b>That is what happened, and the deferral held.</b> W12 F5a turns it on with
    /// <see cref="Contracts.IOrderVerdictFeed"/> to read it — the reader arriving with the counter
    /// rather than a fortnight after it. Existing orders back-fill to <c>0</c>, which is below every
    /// cursor and therefore invisible to a delta; they become visible the moment something rejects
    /// one, which is the only event this feed is about.
    /// </para>
    /// </remarks>
    protected override bool TracksSyncChanges => true;

    public DbSet<Order> Orders => Set<Order>();

    public DbSet<OrderLine> OrderLines => Set<OrderLine>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Order>(order =>
        {
            order.HasKey(o => o.Id);

            order.Property(o => o.UserId).HasMaxLength(64).IsRequired();
            order.Property(o => o.CurrencyCode).HasMaxLength(3).IsFixedLength().IsRequired();

            // The money convention across every module: numeric(18,4). Four places rather than two
            // because a unit price can legitimately carry them — BR-PRD-8 rounds at the line, not at
            // the price — and a column that truncated would round twice.
            order.Property(o => o.Total).HasPrecision(18, 4);
            order.Property(o => o.TaxTotal).HasPrecision(18, 4);

            // Nullable, and that is the schema saying "not looked at" rather than "agreed" — the
            // distinction `PriceAgreement` is built on (W11 slice 14).
            order.Property(o => o.ServerTotal).HasPrecision(18, 4);
            order.Property(o => o.ServerTaxTotal).HasPrecision(18, 4);

            /*
             * The device's pricing cursors, as an owned type (`ORD-08`).
             *
             * Owned rather than six loose columns because they are one fact — what the device had
             * pulled — and a reader that saw them separately would have to know they travel together.
             * `IsRequired(false)` on the whole thing keeps "the device did not say" representable:
             * every column is null for an order captured before this slice.
             */
            order.OwnsOne(o => o.CapturedAgainst, snapshot =>
            {
                snapshot.Property(s => s.PriceLists).HasColumnName("captured_against_price_lists");
                snapshot.Property(s => s.PriceLines).HasColumnName("captured_against_price_lines");
                snapshot.Property(s => s.PriceAssignments)
                    .HasColumnName("captured_against_price_assignments");
                snapshot.Property(s => s.Promotions).HasColumnName("captured_against_promotions");
                snapshot.Property(s => s.PromotionAssignments)
                    .HasColumnName("captured_against_promotion_assignments");
                snapshot.Property(s => s.TaxRates).HasColumnName("captured_against_tax_rates");
            });

            // One order per visit, in the schema as well as the aggregate. See Order's remarks for
            // why that is a decision rather than an obvious constraint.
            order.HasIndex(o => new { o.TenantId, o.VisitId }).IsUnique();

            // "What has this outlet been ordering" — the read this module exists to serve, and the
            // reason the outlet id is copied onto the order rather than reached through the visit.
            order.HasIndex(o => new { o.TenantId, o.OutletId, o.CapturedAtUtc });

            /*
             * "What has the back office decided about my orders since I last asked" — the pull feed
             * (`IOrderVerdictFeed`), W12 F5a.
             *
             * <b>The only index here that is not about a read somebody performs deliberately.</b>
             * The two above answer questions a person asked; this one answers a question every
             * device asks on every sync, which is the argument for it rather than against. Without
             * it the feed's `UserId = … AND RowVersion > cursor` finds no useful index — the ones
             * above lead with `VisitId` and `OutletId` — and reads every order the tenant has ever
             * taken, per device, per sync, forever. `orders` is the highest-volume table in this
             * schema by construction: one row per visit, and visits do not stop.
             *
             * `RowVersion` last because it is the range and the sort; the two equality columns lead.
             */
            order.HasIndex(o => new { o.TenantId, o.UserId, o.RowVersion });

            order.HasMany(o => o.Lines)
                .WithOne()
                .HasForeignKey(line => line.OrderId)
                .OnDelete(DeleteBehavior.Cascade);

            order.Navigation(o => o.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);

            order.HasMany(o => o.Submissions)
                .WithOne()
                .HasForeignKey(submission => submission.OrderId)
                .OnDelete(DeleteBehavior.Cascade);

            order.Navigation(o => o.Submissions).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<OrderSubmission>(submission =>
        {
            submission.HasKey(s => s.Id);

            /*
             * One order per mutation, tenant-wide — not per order.
             *
             * The narrower `(tenant, order, mutation)` would let two *different* orders claim the same
             * mutation id, and "has this push already been applied" is precisely the question that
             * must have one answer. It is the same id Sync's ledger keys on; disagreeing with it here
             * would make a replay land twice under two orders.
             */
            submission.HasIndex(s => new { s.TenantId, s.MutationId }).IsUnique();

            // "What happened to this order" reads oldest-first, and BR-ORD-9's terminal-id check
            // reads every submission of one order — both are this index.
            submission.HasIndex(s => new { s.TenantId, s.OrderId, s.SubmittedAtUtc });

            submission.Property(s => s.Note).HasMaxLength(OrderSubmission.MaximumNoteLength);
        });

        modelBuilder.Entity<OrderLine>(line =>
        {
            line.HasKey(l => l.Id);

            line.Property(l => l.UnitOfMeasure)
                .HasMaxLength(OrderLine.MaximumUnitOfMeasureLength)
                .IsRequired();

            line.Property(l => l.Quantity).HasPrecision(18, 4);
            line.Property(l => l.UnitPrice).HasPrecision(18, 4);
            line.Property(l => l.LineTotal).HasPrecision(18, 4);
            line.Property(l => l.TaxAmount).HasPrecision(18, 4);

            // One line per product, enforced where two of them would actually collide. The aggregate
            // refuses this too; the index is what holds if two pushes of the same order ever raced.
            line.HasIndex(l => new { l.TenantId, l.OrderId, l.ProductId }).IsUnique();
        });
    }
}
