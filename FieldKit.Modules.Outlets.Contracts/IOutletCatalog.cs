namespace FieldKit.Modules.Outlets.Contracts;

/// <summary>
/// Enough about an outlet to name it and decide whether it should be worked — the identity half of
/// an outlet, without its master data.
/// </summary>
/// <param name="OutletId">Stable for the life of the location.</param>
/// <param name="Code">The tenant's own identifier, which is what a back-office user recognises.</param>
/// <param name="Name">Rendered next to it on a territory or journey screen.</param>
/// <param name="IsOpen">
/// False once the outlet is closed. Callers should still resolve closed outlets: a territory that
/// contained one last quarter must still be able to say so, and blanking it would rewrite history
/// rather than protect anything. What "closed" should *stop* is a decision for the caller.
/// </param>
public sealed record OutletSummary(Guid OutletId, string Code, string Name, bool IsOpen);

/// <summary>
/// Resolves outlets for other modules (Outlets module contract).
/// </summary>
/// <remarks>
/// <para>
/// Consumed by Organization, which assigns outlets to territories and must know which ones exist
/// without reading <c>outlets.outlet</c>. Journey and Visit will want the same seam for the same
/// reason — this interface is what makes schema-per-module (ADR-0005) survivable once a feature
/// spans two modules.
/// </para>
/// <para>
/// Deliberately narrow. It exposes no address, no coordinates, no contacts and no channel: those are
/// master data the owning module curates, and a consumer that could read them would soon be making
/// decisions with a stale copy of them. A caller needing more should ask for a contract that says
/// what it needs, not for this one to grow.
/// </para>
/// <para>
/// All lookups are implicitly scoped to the current tenant by the global query filter — there is no
/// tenant parameter, because a caller able to pass one is a caller able to pass the wrong one.
/// </para>
/// </remarks>
public interface IOutletCatalog
{
    /// <summary>
    /// Resolves several outlets at once. Ids with no match are simply absent from the result rather
    /// than returned as nulls — callers are validating or labelling a set and want the ones that are
    /// real.
    /// </summary>
    Task<IReadOnlyList<OutletSummary>> FindManyAsync(
        IReadOnlyCollection<Guid> outletIds, CancellationToken cancellationToken = default);
}
