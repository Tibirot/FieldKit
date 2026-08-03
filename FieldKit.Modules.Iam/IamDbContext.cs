using FieldKit.BuildingBlocks;
using FieldKit.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FieldKit.Modules.Iam;

/// <summary>The IAM module's context — owns the <c>iam</c> schema (schema-per-module, ADR-0005).</summary>
public sealed class IamDbContext(DbContextOptions<IamDbContext> options, ITenantContext tenantContext)
    : ModuleDbContext(options, tenantContext)
{
    public const string SchemaName = "iam";

    protected override string Schema => SchemaName;

    /// <summary>
    /// Not tenant-owned, so no query filter applies — see <see cref="Tenant"/> for why the tenant
    /// list is the one thing that must be readable before a tenant is known.
    /// </summary>
    public DbSet<Tenant> Tenants => Set<Tenant>();

    public DbSet<User> Users => Set<User>();

    public DbSet<Role> Roles => Set<Role>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Tenant>(tenant =>
        {
            tenant.ToTable("tenant");
            tenant.HasKey(t => t.Id);
            tenant.Property(t => t.Name).HasMaxLength(200).IsRequired();
            tenant.Property(t => t.Realm).HasMaxLength(100).IsRequired();
            // Platform-wide, not per-tenant: two tenants sharing a realm would share an identity
            // provider, and a token from one would validate for the other.
            tenant.HasIndex(t => t.Realm).IsUnique();
        });

        modelBuilder.Entity<User>(user =>
        {
            user.ToTable("user");
            user.HasKey(u => u.Id);
            user.Property(u => u.SubjectId).HasMaxLength(64).IsRequired();
            user.Property(u => u.Email).HasMaxLength(320).IsRequired(); // RFC 5321 maximum
            user.Property(u => u.DisplayName).HasMaxLength(200).IsRequired();
            user.Property(u => u.Locale).HasMaxLength(35).IsRequired();   // BCP-47
            user.Property(u => u.TimeZone).HasMaxLength(64).IsRequired(); // IANA

            // Scoped to the tenant, not global: the same person may hold accounts in two tenants,
            // and those are genuinely different users with different roles.
            user.HasIndex(u => new { u.TenantId, u.SubjectId }).IsUnique();
            user.HasIndex(u => new { u.TenantId, u.Email }).IsUnique();

            user.OwnsMany(u => u.Roles, role =>
            {
                role.ToTable("user_role");
                role.WithOwner().HasForeignKey(r => r.UserId);
                role.HasKey(r => new { r.UserId, r.RoleId });
            });
        });

        modelBuilder.Entity<Role>(role =>
        {
            role.ToTable("role");
            role.HasKey(r => r.Id);
            role.Property(r => r.Name).HasMaxLength(100).IsRequired();
            role.HasIndex(r => new { r.TenantId, r.Name }).IsUnique();

            // Permissions are a value list, not an entity: the catalogue of what exists is
            // contributed by code, so a table would be a second copy free to drift from it.
            role.PrimitiveCollection(r => r.Permissions).HasColumnName("permissions").IsRequired();
        });
    }
}
