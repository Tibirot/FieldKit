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
        // Reads the outlet base and the vocabulary it is classified by, because that is the round
        // they walk. Changes neither.
        new("Field Rep",
        [
            "product:read", "outlet:read", "channel:read", "territory:read", "config:read",
            // Reads the plan they are meant to walk. Working it is JRN-05 and lands with the
            // offline app in W9; reading it is theirs from the moment there is one.
            "journey:read",
            // …and reports on it: a shop that was shut, a call nobody planned, a day swapped within
            // the cycle. Deliberately not journey:write — a rep reports on the round they walked,
            // they do not decide what the round is.
            "journey:annotate",
            // Checking in *is* the field job, which makes visit:write a rep's permission rather
            // than an administrator's — the only write in this system that works that way round.
            "visit:read",
            "visit:write",
        ]),

        // Reads the hierarchy because a supervisor's job is defined by their branch of it, and reads
        // positions because that is who is in it. Cannot redraw either — both are back-office acts.
        new("Supervisor",
        [
            "product:read",
            "orgunit:read",
            "position:read",
            "outlet:read",
            "channel:read",
            "territory:read",
            // Reads the catalogue because the outlet screens a supervisor looks at render from it.
            "config:read",
            // Reviews the plans their branch is working, and argues with them. Cannot regenerate
            // one: that changes what a rep is holding, which is sales ops' act rather than theirs.
            "journey:read",
            // Reads visits, including where a rep checked in from — oversight. Performing one is
            // not something a supervisor does on somebody's behalf, so there is no visit:write.
            "visit:read",
            IamPermissions.UserRead,
        ]),

        // Staffs the organization without redrawing it, and owns the outlet base: sales ops decides
        // who covers what, org design decides what there is to cover. It does not own the
        // classification vocabulary — renaming a channel changes what every assortment rule means.
        new("Sales Ops",
        [
            "product:read",
            "product:write",
            "orgunit:read",
            "position:read",
            "position:write",
            "outlet:read",
            "outlet:write",
            "channel:read",
            // Owns which outlets a rep covers: a territory's membership *is* that rep's offline data
            // scope (BR-ORG-3), which is squarely sales ops' job rather than org design's.
            "territory:read",
            "territory:write",
            // Sets call frequencies and generates plans, per the Journey spec's own role table. The
            // same reasoning as territory: how often a shop is called on is a sales-operations
            // decision, and it is the input the whole plan is derived from.
            "journey:read",
            "journey:write",
            "visit:read",
            // And can correct a round after the fact — sales ops fields the phone call when a rep
            // could not get in somewhere and the plan still says otherwise.
            "journey:annotate",
            "config:read",
        ]),

        // No product permissions, on purpose. An admin who can grant capabilities does not thereby
        // hold them — that is what makes this a permission model rather than a tier list.
        new("Tenant Admin",
        [
            "orgunit:read",
            "orgunit:write",
            "position:read",
            "position:write",
            // The classification vocabulary, which Sales Ops deliberately does not own: renaming a
            // channel changes what every assortment and pricing rule keyed to it means.
            "channel:read",
            "channel:write",
            // Authoring what a tenant may record about its own data is tenant administration, not
            // sales operations — the same reasoning that keeps channel renames here.
            "config:read",
            "config:write",
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
