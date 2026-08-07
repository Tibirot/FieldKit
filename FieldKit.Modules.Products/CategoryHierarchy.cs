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

    /// <summary>
    /// <paramref name="categoryId"/> and every category above it, nearest first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// What a promotion targeting a category needs (<c>PRD-06</c>): a deal on <i>Beverages</i> covers
    /// a product filed under <i>Beverages / Water / Still</i>, so matching walks <b>up</b> from the
    /// product rather than expanding the category downward. Walking up is also what keeps the target
    /// honest over time — a product moved into Still next week is covered by the Beverages deal
    /// without anything being re-expanded, which is the whole reason authoring stores the category
    /// rather than its members.
    /// </para>
    /// <para>
    /// Self-inclusive, because a promotion targeting exactly the product's own category is the
    /// ordinary case and a caller should not have to remember to add it.
    /// </para>
    /// <para>
    /// Stops on a repeat rather than looping. <see cref="WouldCreateCycle"/> makes a cycle
    /// unreachable through the API, but this runs on every resolution and a hang is a far worse
    /// failure than a short answer — the same defensive stance that function takes for the same
    /// reason.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<Guid> SelfAndAncestors(
        Guid categoryId, IReadOnlyDictionary<Guid, Guid?> parentOf)
    {
        var chain = new List<Guid>();
        var seen = new HashSet<Guid>();

        for (var current = (Guid?)categoryId; current is { } id && seen.Add(id);)
        {
            chain.Add(id);
            current = parentOf.TryGetValue(id, out var next) ? next : null;
        }

        return chain;
    }
}
