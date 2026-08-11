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
    /// No row-version counter yet, and it is scheduled rather than declined.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Orders are the <b>one transactional record that flows back down</b> (order spec F4): a
    /// rejected order returns to the rep's device so the work is never stranded. That is exactly the
    /// question a change sequence answers, so this schema will need one.
    /// </para>
    /// <para>
    /// It is not here because nothing pulls orders until slice 4 builds the rejection path, and this
    /// codebase has been burned by the opposite: W8 slice 6 deliberately left the <c>blobs</c> store
    /// out because "a store with no writer is a schema version spent on nothing". A counter no feed
    /// reads is the same trade. Unlike <c>Source</c> on <c>Visit</c>, this one <i>can</i> arrive
    /// later without loss — a device that has never pulled an order has no watermark to be wrong
    /// about, so it takes everything on its first pull whatever the versions say.
    /// </para>
    /// </remarks>
    protected override bool TracksSyncChanges => false;

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

            // One order per visit, in the schema as well as the aggregate. See Order's remarks for
            // why that is a decision rather than an obvious constraint.
            order.HasIndex(o => new { o.TenantId, o.VisitId }).IsUnique();

            // "What has this outlet been ordering" — the read this module exists to serve, and the
            // reason the outlet id is copied onto the order rather than reached through the visit.
            order.HasIndex(o => new { o.TenantId, o.OutletId, o.CapturedAtUtc });

            order.HasMany(o => o.Lines)
                .WithOne()
                .HasForeignKey(line => line.OrderId)
                .OnDelete(DeleteBehavior.Cascade);

            order.Navigation(o => o.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);
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

            // One line per product, enforced where two of them would actually collide. The aggregate
            // refuses this too; the index is what holds if two pushes of the same order ever raced.
            line.HasIndex(l => new { l.TenantId, l.OrderId, l.ProductId }).IsUnique();
        });
    }
}
