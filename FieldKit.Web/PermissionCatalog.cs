namespace FieldKit.Web;

/// <summary>
/// A permission a module owns, as a <c>resource:action</c> string plus what it lets someone do.
/// </summary>
/// <param name="Name">The string checked at runtime, e.g. <c>product:write</c>.</param>
/// <param name="Description">
/// Shown to a tenant admin composing a role. It is the only thing standing between "grant everything
/// that sounds plausible" and an informed choice, so it should describe the capability, not restate
/// the name.
/// </param>
public sealed record PermissionDefinition(string Name, string Description);

/// <summary>
/// Every permission the running system understands, contributed by the modules that own them
/// (IAM spec §8).
/// </summary>
/// <remarks>
/// <para>
/// The catalogue is <b>code, not data</b>. A `permission` table would be a second copy of this list,
/// free to drift from the code that actually enforces it — and the drift presents as an admin
/// granting a permission nothing checks, which looks like a working grant right up until someone
/// relies on it.
/// </para>
/// <para>
/// It exists so role administration can *validate*: a role naming <c>prodcut:read</c> is a typo that
/// silently grants nothing, and without a catalogue there is no moment at which anything could
/// notice.
/// </para>
/// </remarks>
public interface IPermissionCatalog
{
    /// <summary>All known permissions, ordered by name.</summary>
    IReadOnlyList<PermissionDefinition> All { get; }

    /// <summary>
    /// Whether <paramref name="permission"/> is one the system enforces. Ordinal and case-sensitive:
    /// permissions are identifiers, so accepting <c>Product:Read</c> for <c>product:read</c> would
    /// let a typo through as a working grant.
    /// </summary>
    bool Contains(string permission);
}

internal sealed class PermissionCatalog : IPermissionCatalog
{
    private readonly HashSet<string> _names;

    public PermissionCatalog(IReadOnlyList<IModule> modules)
    {
        var duplicates = modules
            .SelectMany(module => module.Permissions.Select(permission => (module, permission)))
            .GroupBy(entry => entry.permission.Name, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => $"{group.Key} (declared by {string.Join(", ", group.Select(e => e.module.Name))})")
            .ToList();

        // Two modules owning one permission means neither owns it: a tenant admin granting it cannot
        // know what they are granting, and the descriptions will diverge. Fail at startup rather than
        // pick a winner silently.
        if (duplicates.Count > 0)
        {
            throw new InvalidOperationException(
                "Permissions must be owned by exactly one module. Duplicates: " + string.Join("; ", duplicates));
        }

        All = [.. modules
            .SelectMany(module => module.Permissions)
            .OrderBy(permission => permission.Name, StringComparer.Ordinal)];

        _names = [.. All.Select(permission => permission.Name)];
    }

    public IReadOnlyList<PermissionDefinition> All { get; }

    public bool Contains(string permission) => _names.Contains(permission);
}
