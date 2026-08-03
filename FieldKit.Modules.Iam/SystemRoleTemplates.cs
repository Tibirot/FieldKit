using FieldKit.Web;

namespace FieldKit.Modules.Iam;

/// <summary>A role a new tenant starts with, as a name and the permissions it bundles.</summary>
public sealed record RoleTemplate(string Name, IReadOnlyList<string> Permissions);

/// <summary>
/// The roles every new tenant starts with (<c>IAM-06</c>).
/// </summary>
/// <remarks>
/// <para>
/// A tenant seeded without these has permissions defined and nobody who can hold them: no role to
/// assign, and — because role administration is itself permission-guarded — no way to create the
/// first one from inside the product. The templates are what make a fresh tenant reachable.
/// </para>
/// <para>
/// They are <b>code, not data</b>, for the same reason the permission catalogue is: a table of
/// templates is a second copy of a decision the product makes, free to drift from the permissions
/// that actually exist. Being code also means <see cref="Validate"/> can fail at startup rather than
/// at the moment an admin discovers a role grants nothing.
/// </para>
/// <para>
/// They are <b>starting points, not policy</b>. An admin may rename or recompose any of them — the
/// only thing refused is deleting one (<c>IAM-04</c>), so a tenant always has a way back to a
/// working set of roles.
/// </para>
/// <para>
/// Deliberately not hierarchical. There is no template that holds everything, and Tenant Admin holds
/// no product permissions at all: administering who may sell is a different capability from selling.
/// The same reasoning the dev realm's fixture users demonstrate (see
/// <c>FieldKit.AppHost/realms/README.md</c>), applied to the roles a real tenant starts with.
/// </para>
/// </remarks>
public static class SystemRoleTemplates
{
    /// <summary>
    /// The template set, in the order a new tenant sees them.
    /// </summary>
    /// <remarks>
    /// This list names permissions owned by other modules, which is the point: what a "Field Rep"
    /// may do is a product decision spanning modules, and no single module can make it.
    ///
    /// Other modules' permissions appear as <b>literal strings</b>, not constants. That is forced —
    /// a module may reference only another module's <c>Contracts</c> (AT-1), and Catalog has none —
    /// but it is also the honest shape: a role template is a product decision that happens to name
    /// capabilities, not a compile-time dependency on the code that enforces them.
    /// <see cref="Validate"/> is what keeps that from becoming a typo nobody notices.
    ///
    /// A module landing new permissions adds them here in the same change, or the templates quietly
    /// fall behind what the product can do.
    /// </remarks>
    public static readonly IReadOnlyList<RoleTemplate> All =
    [
        new("Field Rep", ["product:read"]),

        // Reads the hierarchy because a supervisor's job is defined by their branch of it, and reads
        // positions because that is who is in it. Cannot redraw either — both are back-office acts.
        new("Supervisor", ["product:read", "orgunit:read", "position:read", IamPermissions.UserRead]),

        // Staffs the organization without redrawing it: sales ops decides who covers what, org
        // design decides what there is to cover.
        new("Sales Ops", ["product:read", "product:write", "orgunit:read", "position:read", "position:write"]),

        // No product permissions, on purpose. An admin who can grant capabilities does not thereby
        // hold them — that is what makes this a permission model rather than a tier list.
        new("Tenant Admin",
        [
            "orgunit:read",
            "orgunit:write",
            "position:read",
            "position:write",
            IamPermissions.RoleRead,
            IamPermissions.RoleWrite,
            IamPermissions.UserRead,
            IamPermissions.UserWrite,
        ]),
    ];

    /// <summary>The templates as <see cref="Role"/> entities, ready to be added for one tenant.</summary>
    public static IEnumerable<Role> Materialize() =>
        All.Select(template => Role.Create(template.Name, template.Permissions, isSystemTemplate: true));

    /// <summary>
    /// Fails if any template names a permission the running system does not enforce.
    /// </summary>
    /// <remarks>
    /// The failure this prevents is quiet: a role listing <c>prodcut:read</c> saves fine, displays
    /// fine, and grants nothing — and it is the tenant's *starting* role, so the first person to
    /// notice is a rep who cannot do their job. Checked at startup because that is the last moment
    /// it is still cheap.
    /// </remarks>
    public static void Validate(IPermissionCatalog catalog)
    {
        var unknown = All
            .SelectMany(template => template.Permissions.Select(permission => (template, permission)))
            .Where(entry => !catalog.Contains(entry.permission))
            .Select(entry => $"{entry.permission} (in '{entry.template.Name}')")
            .ToList();

        if (unknown.Count > 0)
        {
            throw new InvalidOperationException(
                "System role templates name permissions no module enforces: " + string.Join(", ", unknown));
        }
    }
}
