using FieldKit.BuildingBlocks;
using FieldKit.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FieldKit.Modules.Sync;

/// <summary>The Sync module's context — owns the <c>sync</c> schema (schema-per-module).</summary>
public sealed class SyncDbContext(DbContextOptions<SyncDbContext> options, ITenantContext tenantContext)
    : ModuleDbContext(options, tenantContext)
{
    public const string SchemaName = "sync";

    protected override string Schema => SchemaName;

    public DbSet<Device> Devices => Set<Device>();

    public DbSet<DeviceScopeEntry> DeviceScope => Set<DeviceScopeEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Device>(device =>
        {
            device.ToTable("device");
            device.HasKey(d => d.Id);

            device.Property(d => d.UserId).HasMaxLength(64).IsRequired();
            device.Property(d => d.Name).HasMaxLength(120).IsRequired();

            // By name, never as an ordinal — a reason inserted in the middle of the enum would
            // silently re-interpret every deactivated device rather than breaking a build.
            device.Property(d => d.DeactivatedBecause).HasConversion<string>().HasMaxLength(32);

            // One active device per user, enforced by the database rather than by the code path
            // that happens to be looking. Two binds racing would otherwise both deactivate the old
            // device and both insert a new one, and the rep would have two actives — which the
            // pull path reads as "whichever I find first".
            device.HasIndex(d => new { d.TenantId, d.UserId })
                .IsUnique()
                .HasFilter("\"IsActive\"")
                .HasDatabaseName("UX_device_one_active_per_user");
        });

        modelBuilder.Entity<DeviceScopeEntry>(entry =>
        {
            entry.ToTable("device_scope");
            entry.HasKey(e => new { e.DeviceId, e.OutletId });

            // The read on every pull: "what was this device told it had". The composite key already
            // leads with DeviceId, so no second index earns its keep.
        });
    }
}
