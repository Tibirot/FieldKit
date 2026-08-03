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

    /// <summary>
    /// The chain of units above <paramref name="unitId"/>, nearest parent first, up to the root.
    /// </summary>
    /// <remarks>
    /// The management line itself (<c>ORG-02</c>): who this person reports up through. Excludes the
    /// unit they occupy — that is where they are, not who is above them.
    /// </remarks>
    public static IReadOnlyList<Guid> AncestorsOf(Guid unitId, IReadOnlyDictionary<Guid, Guid?> parentOf)
    {
        var line = new List<Guid>();
        var seen = new HashSet<Guid> { unitId };

        var current = parentOf.TryGetValue(unitId, out var parent) ? parent : null;

        while (current is { } id && seen.Add(id))
        {
            line.Add(id);
            current = parentOf.TryGetValue(id, out var next) ? next : null;
        }

        return line;
    }

    /// <summary>
    /// Every unit at or below <paramref name="roots"/> — the visibility scope for whoever occupies
    /// them (BR-ORG-4).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Downward, where the management line is upward, and the two answer different questions: a
    /// supervisor <i>reports through</i> their ancestors and <i>sees</i> their descendants.
    /// </para>
    /// <para>
    /// Breadth-first over the whole set rather than per root, so a user holding two positions in the
    /// same branch does not pay for the overlap twice — and the result is a set, so it cannot
    /// double-count a unit reachable from both.
    /// </para>
    /// </remarks>
    public static IReadOnlySet<Guid> ScopeOf(
        IReadOnlyCollection<Guid> roots, IReadOnlyDictionary<Guid, Guid?> parentOf)
    {
        var childrenOf = parentOf
            .Where(entry => entry.Value is not null)
            .GroupBy(entry => entry.Value!.Value, entry => entry.Key)
            .ToDictionary(group => group.Key, group => group.ToList());

        var scope = new HashSet<Guid>(roots);
        var queue = new Queue<Guid>(roots);

        while (queue.Count > 0)
        {
            if (!childrenOf.TryGetValue(queue.Dequeue(), out var children)) continue;

            foreach (var child in children.Where(scope.Add))
            {
                queue.Enqueue(child);
            }
        }

        return scope;
    }
}
