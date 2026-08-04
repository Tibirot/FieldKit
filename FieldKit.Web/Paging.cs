namespace FieldKit.Web;

/// <summary>
/// One page of a list, and enough to render a pager around it.
/// </summary>
/// <remarks>
/// <para>
/// An envelope rather than a bare array, because a page of rows is not self-describing: without
/// <paramref name="Total"/> a client cannot say "page 3 of 97", and without the echoed
/// <paramref name="Page"/> and <paramref name="PageSize"/> it cannot tell a clamped request from the
/// one it made. Both are the difference between a pager and a "next" button.
/// </para>
/// <para>
/// <b>Offset, not keyset.</b> A back office browsing an outlet base wants a total and the ability to
/// jump; a device replicating one wants stability under concurrent writes and constant cost at depth
/// — different problems, and the sync engine keeps its own cursor-based feed for the second
/// (<c>rowVersion &gt; cursor</c>). Forcing one mechanism onto both would be the mistake, not having
/// two. At outlet-base scale offset's weakness never bites: skipping 4,800 rows is a scan Postgres
/// does in under a millisecond.
/// </para>
/// <para>
/// Named <c>PagedList</c> rather than <c>Page</c> so its properties can be <c>Page</c> and
/// <c>PageSize</c> — the same two words the query string uses. A request and its response describing
/// the same thing differently is a small cost every client pays forever.
/// </para>
/// </remarks>
public sealed record PagedList<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize);

/// <summary>
/// How a list request asks for one page.
/// </summary>
public static class Paging
{
    /// <summary>The page size when a caller does not ask for one — a screenful of a desktop table.</summary>
    public const int DefaultSize = 50;

    /// <summary>
    /// The most rows one page may carry.
    /// </summary>
    /// <remarks>
    /// A cap rather than a promise to serve whatever is asked for: <c>pageSize=100000</c> is either a
    /// mistake or an export, and an export is a different feature with different limits. Clamped
    /// rather than refused, because the response echoes the size actually used — so a caller can see
    /// what happened without having to parse an error.
    /// </remarks>
    public const int MaxSize = 200;

    /// <summary>
    /// Reads page and size from a request, forgiving nonsense rather than arguing about it.
    /// </summary>
    /// <remarks>
    /// Page numbers start at 1, because that is what a person reading a pager expects and what the
    /// URL will say. Zero and negatives clamp to the first page instead of returning a 400: nobody
    /// types <c>?page=-2</c> on purpose, and answering an obvious typo with an error page is worse
    /// than answering it with the first page.
    /// </remarks>
    public static (int Page, int Size, int Skip) Resolve(int? page, int? pageSize)
    {
        var resolvedPage = Math.Max(page ?? 1, 1);
        var resolvedSize = Math.Clamp(pageSize ?? DefaultSize, 1, MaxSize);

        return (resolvedPage, resolvedSize, (resolvedPage - 1) * resolvedSize);
    }

    // No `ToPageAsync` helper here. Counting and taking a page is three lines of EF at the call
    // site, and wrapping them would drag `Microsoft.EntityFrameworkCore` into the assembly every
    // module's *endpoints* reference — coupling the HTTP layer to the ORM to save two lines, for one
    // caller. It lands as a shared helper when a second module needs it, the same rule the module
    // registry applies to contracts.

    /// <summary>
    /// Escapes a user's search text so its punctuation is text and not syntax.
    /// </summary>
    /// <remarks>
    /// <c>%</c> and <c>_</c> are wildcards in <c>LIKE</c>. Left alone, searching for <c>50%</c>
    /// matches every outlet whose name starts with 50 — and, worse, a lone <c>%</c> matches
    /// everything while looking like a search that found nothing wrong. The backslash escape is
    /// declared explicitly at the call site, because Postgres' default only applies without
    /// <c>standard_conforming_strings</c>.
    /// </remarks>
    public static string EscapeLike(string term) =>
        term.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
