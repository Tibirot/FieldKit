using FieldKit.BuildingBlocks;
using FieldKit.SharedKernel;

namespace FieldKit.Modules.Iam;

/// <summary>
/// A named bundle of permissions, scoped to one tenant.
/// </summary>
/// <remarks>
/// <para>
/// Permissions are held as <c>resource:action</c> <b>strings</b>, not rows in a permission table.
/// The catalogue of what permissions exist is contributed by the modules that own them (IAM spec §8)
/// — it is code, not data. A `permission` table would be a second copy of that list, free to drift
/// from the code that actually checks it, and the drift would present as an admin granting a
/// permission nothing enforces.
/// </para>
/// <para>
/// Roles are tenant-scoped so an admin can rename or recompose "Supervisor" for their own tenant
/// without touching anyone else's. That is exactly why module code checks permissions and never role
/// names (BR-IAM-2): the role is the customizable part, the permission is the contract.
/// </para>
/// </remarks>
public sealed class Role : AggregateRoot, ITenantOwned, IAuditable
{
    private readonly List<string> _permissions = [];

    public Guid Id { get; private set; }

    /// <summary>Unique within the tenant, e.g. "Field Rep".</summary>
    public string Name { get; private set; } = null!;

    /// <summary>
    /// True for roles seeded from a system template (IAM-06). Templates may be recomposed by an
    /// admin but not deleted, so a tenant cannot end up with no way back to a working set of roles.
    /// </summary>
    public bool IsSystemTemplate { get; private set; }

    public IReadOnlyList<string> Permissions => _permissions;

    public TenantId TenantId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset? ModifiedAtUtc { get; set; }
    public string? ModifiedBy { get; set; }

    private Role() { } // EF

    public static Role Create(string name, IEnumerable<string> permissions, bool isSystemTemplate = false)
    {
        var role = new Role { Id = Guid.CreateVersion7(), Name = name, IsSystemTemplate = isSystemTemplate };
        role.SetPermissions(permissions);
        return role;
    }

    /// <summary>
    /// Renames the role. A system template may be renamed — only deleting it is refused, since the
    /// template is the way back to a working set of roles, not a fixed label.
    /// </summary>
    public void Rename(string name, IClock clock)
    {
        Name = name;
        ModifiedAtUtc = clock.UtcNow;
    }

    /// <summary>
    /// Replaces the permission set wholesale. Add/remove deltas would need the caller to know the
    /// current state, and two admins editing the same role would silently interleave.
    /// </summary>
    public void SetPermissions(IEnumerable<string> permissions)
    {
        _permissions.Clear();
        // Ordinal-distinct: permissions are identifiers, so `Order:Submit` and `order:submit` are
        // different strings and exactly one of them is enforced by anything.
        _permissions.AddRange(permissions.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
    }
}
