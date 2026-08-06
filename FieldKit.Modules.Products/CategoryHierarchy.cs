namespace FieldKit.Modules.Products;

/// <summary>
/// Questions about the category tree that a single <see cref="Category"/> cannot answer.
/// </summary>
/// <remarks>
/// Organization has the same helper for org units, and this is deliberately a second copy rather
/// than a shared one. The algorithm is fifteen lines of pure logic over a dictionary; sharing it
/// would mean either a cross-module reference that <c>AT-1</c> forbids outright, or promoting
/// "walk a parent chain" into <c>BuildingBlocks</c> — a shared abstraction earning its keep on two
/// callers, which is how a kernel starts collecting things nobody owns. Duplicated, each module
/// keeps its own tree rules and can change them without asking anyone.
/// </remarks>
internal static class CategoryHierarchy
{
    /// <summary>
    /// Whether re-parenting <paramref name="categoryId"/> under <paramref name="newParentId"/> would
    /// put it inside its own subtree.
    /// </summary>
    /// <param name="parentOf">Every category's parent pointer, for the current tenant.</param>
    public static bool WouldCreateCycle(
        Guid categoryId, Guid? newParentId, IReadOnlyDictionary<Guid, Guid?> parentOf)
    {
        var seen = new HashSet<Guid>();

        for (var ancestor = newParentId; ancestor is { } id;)
        {
            if (id == categoryId) return true;

            // A cycle that already exists in the data. Refusing is right: the move cannot be shown
            // to be safe, and looping forever to find that out helps nobody.
            if (!seen.Add(id)) return true;

            ancestor = parentOf.TryGetValue(id, out var next) ? next : null;
        }

        return false;
    }
}
