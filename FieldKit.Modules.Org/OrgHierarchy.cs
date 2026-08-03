namespace FieldKit.Modules.Org;

/// <summary>
/// Tree shaping over a flat set of units — the part of the hierarchy that is pure logic.
/// </summary>
/// <remarks>
/// Separate from the endpoints and from EF because it is the only part of <c>ORG-01</c> with rules
/// worth testing in isolation: everything else is a query, a save, and a status code.
/// </remarks>
internal static class OrgHierarchy
{
    /// <summary>
    /// Whether reparenting <paramref name="unitId"/> under <paramref name="newParentId"/> would put
    /// it inside its own subtree.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The check that keeps a hierarchy a hierarchy. Without it, moving a unit under its own child
    /// detaches that whole branch from every root and makes it invisible to any traversal — the rows
    /// are still there, referentially intact, and no query returns them. A foreign key cannot catch
    /// this: every parent still exists.
    /// </para>
    /// <para>
    /// Walks from the proposed parent up to a root, so it costs the depth of the tree, not its size.
    /// Guarded against a pre-existing cycle rather than trusting the invariant it is enforcing —
    /// if bad data ever reaches the table, this should refuse the move, not hang.
    /// </para>
    /// </remarks>
    public static bool WouldCreateCycle(
        Guid unitId, Guid? newParentId, IReadOnlyDictionary<Guid, Guid?> parentOf)
    {
        var seen = new HashSet<Guid>();

        for (var ancestor = newParentId; ancestor is { } id; )
        {
            if (id == unitId) return true;

            // A cycle that already exists in the data. Refusing is right: the move cannot be shown
            // to be safe, and looping forever to find that out helps nobody.
            if (!seen.Add(id)) return true;

            ancestor = parentOf.TryGetValue(id, out var next) ? next : null;
        }

        return false;
    }
}
