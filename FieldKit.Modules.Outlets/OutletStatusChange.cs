using FieldKit.BuildingBlocks;
using FieldKit.SharedKernel;

namespace FieldKit.Modules.Outlets;

/// <summary>
/// One transition in an outlet's life — appended, never changed (<c>OUT-04</c>).
/// </summary>
/// <remarks>
/// <para>
/// The outlet's own <see cref="Outlet.Status"/> answers "what is it now". This answers everything
/// else: when it was closed, who closed it, why, and whether it had been deactivated twice before
/// that. Neither status deletes anything — but without this table the *evidence* of a transition was
/// being lost anyway, because the audit stamps on the outlet are overwritten by the next ordinary
/// edit. An outlet closed in March and renamed in April looked, by April, as though it had never
/// been closed by anyone in particular.
/// </para>
/// <para>
/// <b>Append-only, and enforced by having nothing that could change it:</b> no setters beyond
/// construction, no update or delete endpoint, and the module never loads one to modify it. That is
/// what makes BR-OUT-4's "retains history" literally true rather than a description of intent.
/// </para>
/// <para>
/// <see cref="IAuditable"/> supplies when and who — the same stamping interceptor as everywhere else,
/// so the actor is the authenticated user rather than something this module invents.
/// <see cref="IAuditable.ModifiedAtUtc"/> stays null forever; a row here with a modified stamp means
/// something has gone wrong.
/// </para>
/// </remarks>
public sealed class OutletStatusChange : AggregateRoot, ITenantOwned, IAuditable
{
    public Guid Id { get; private set; }

    public Guid OutletId { get; private set; }

    /// <summary>
    /// The status before this transition, or <c>null</c> for the entry recorded when the outlet was
    /// created — so the trail is complete from birth rather than starting at the first edit.
    /// </summary>
    public OutletStatus? From { get; private set; }

    public OutletStatus To { get; private set; }

    /// <summary>
    /// Why. Required when closing, optional otherwise — see <see cref="OutletEndpoints"/> for why
    /// the irreversible act is the one that has to justify itself.
    /// </summary>
    public string? Reason { get; private set; }

    public TenantId TenantId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset? ModifiedAtUtc { get; set; }
    public string? ModifiedBy { get; set; }

    private OutletStatusChange() { } // EF

    /// <summary>Records a transition. The only way one of these comes into existence.</summary>
    public static OutletStatusChange Record(Guid outletId, OutletStatus? from, OutletStatus to, string? reason) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            OutletId = outletId,
            From = from,
            To = to,
            Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(),
        };
}
