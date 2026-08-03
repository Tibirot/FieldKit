using FieldKit.BuildingBlocks;
using FieldKit.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FieldKit.Modules.Outlets;

/// <summary>The Outlets module's context — owns the <c>outlets</c> schema (schema-per-module, ADR-0005).</summary>
public sealed class OutletsDbContext(DbContextOptions<OutletsDbContext> options, ITenantContext tenantContext)
    : ModuleDbContext(options, tenantContext)
{
    public const string SchemaName = "outlets";

    protected override string Schema => SchemaName;

    public DbSet<Channel> Channels => Set<Channel>();

    public DbSet<Outlet> Outlets => Set<Outlet>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Channel>(channel =>
        {
            channel.ToTable("channel");
            channel.HasKey(c => c.Id);
            channel.Property(c => c.Name).HasMaxLength(100).IsRequired();
            channel.HasIndex(c => new { c.TenantId, c.Name }).IsUnique();
        });

        modelBuilder.Entity<Outlet>(outlet =>
        {
            outlet.ToTable("outlet");
            outlet.HasKey(o => o.Id);
            outlet.Property(o => o.Code).HasMaxLength(50).IsRequired();
            outlet.Property(o => o.Name).HasMaxLength(200).IsRequired();
            outlet.Property(o => o.Segment).HasMaxLength(50);
            outlet.Property(o => o.Banner).HasMaxLength(100);

            // Stored as the string, not the int. An enum's numeric value is a position in a source
            // file: reordering the members would silently reinterpret every row, and nobody reading
            // the table could tell 2 from "Closed" without the code in front of them.
            outlet.Property(o => o.Status).HasConversion<string>().HasMaxLength(20).IsRequired();

            // The tenant's own identifier, so unique per tenant and not globally — two tenants
            // numbering their stores from 1 is the ordinary case, not a collision.
            outlet.HasIndex(o => new { o.TenantId, o.Code }).IsUnique();

            // BR-OUT-1's half that a database can hold: every outlet has a channel, and the channel
            // cannot be deleted out from under it. The endpoint refuses first with a count, which is
            // the answer an admin can act on; this is what makes that a guarantee.
            outlet.HasOne<Channel>()
                .WithMany()
                .HasForeignKey(o => o.ChannelId)
                .OnDelete(DeleteBehavior.Restrict);

            // The two queries the outlet list actually makes: filter by channel, filter by status.
            outlet.HasIndex(o => new { o.TenantId, o.ChannelId });
            outlet.HasIndex(o => new { o.TenantId, o.Status });
        });
    }
}
