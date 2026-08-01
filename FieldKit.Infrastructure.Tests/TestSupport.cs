using FieldKit.BuildingBlocks;
using FieldKit.Infrastructure;
using FieldKit.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace FieldKit.Infrastructure.Tests;

/// <summary>A throwaway tenant-owned, auditable entity to exercise the persistence base.</summary>
public class Widget : ITenantOwned, IAuditable
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public string Name { get; set; } = string.Empty;

    public TenantId TenantId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset? ModifiedAtUtc { get; set; }
    public string? ModifiedBy { get; set; }
}

/// <summary>A minimal module context in schema "test" — stands in for a real module's context.</summary>
public class TestDbContext(DbContextOptions<TestDbContext> options, ITenantContext tenantContext)
    : ModuleDbContext(options, tenantContext)
{
    protected override string Schema => "test";

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
