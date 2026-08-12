namespace FieldKit.Modules.Products.Contracts;

/// <summary>
/// What one outlet may be sold, asked by the module that has to refuse the rest (<c>PRD-02</c>,
/// <c>BR-ORD-1</c>) — W11 slice 4b.
/// </summary>
/// <remarks>
/// <para>
/// <b>Named in the product spec since W6 and built only now</b>, which is this codebase's rule about
/// contracts rather than an oversight: an interface is a promise to a caller, and until Order needed
/// to refuse an off-assortment line there was nobody to promise it to. Its first caller and its first
/// version arrive together.
/// </para>
/// <para>
/// <b>It answers about a set, not a product.</b> An order is tens of lines and a rep is waiting for
/// the push; asking per line would be tens of round trips against a rule that can be settled in one.
/// The same call <see cref="IPricingService"/> made for the same reason.
/// </para>
/// <para>
/// <b>"Assorted" already means the effective assortment</b> — the channel's list with the outlet's
/// own overrides applied (<c>PRD-02</c>, W6 slice 4). A caller never has to know that an outlet can
/// add a line its channel does not carry, or drop one it does; that is exactly the knowledge this
/// contract exists to keep inside Products.
/// </para>
/// </remarks>
public interface IAssortmentService
{
    /// <summary>
    /// Which of <paramref name="productIds"/> this outlet may order. Products it may not are simply
    /// absent from the result.
    /// </summary>
    /// <remarks>
    /// <b>The positive form, deliberately.</b> "Which of these are allowed" is a statement about the
    /// assortment; "which are forbidden" would be a statement about the caller's list, and a caller
    /// asking about a product that does not exist at all would get the same answer as one asking
    /// about a delisted product. The subtraction is the caller's, and Order does it in line order so
    /// the rejection names the <i>first</i> offending line rather than an arbitrary one.
    /// </remarks>
    Task<IReadOnlySet<Guid>> AssortedAsync(
        Guid outletId,
        IReadOnlyCollection<Guid> productIds,
        CancellationToken cancellationToken = default);
}
