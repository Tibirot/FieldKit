using FieldKit.BuildingBlocks;
using FieldKit.SharedKernel;

namespace FieldKit.Modules.Sync;

/// <summary>Why a device stopped being the rep's active one — and whether it may still drain.</summary>
public enum DeactivationReason
{
    /// <summary>
    /// The rep bound a newer device. The old one is no longer allowed to pull, and is still allowed
    /// **one final push** of work it captured before the swap (sync engine §7, A8) — a rep can lose
    /// signal for a day and be re-bound before reconnecting, and that day's visits are not lost work.
    /// </summary>
    Swapped = 1,

    /// <summary>
    /// Lost or stolen. Blocks the drain as well: a suspect device must not push fabricated visits
    /// (security §5). An administrator chooses this; nothing infers it.
    /// </summary>
    Compromised = 2,
}

/// <summary>
/// A device a rep syncs from. One is active per user at a time (<c>OFF-12</c>, sync engine §7).
/// </summary>
/// <remarks>
/// <para>
/// The registry exists to answer one question before any other: <b>may this device pull?</b> Asking
/// it first is deliberate. Scoping ("which outlets does this rep cover") is an expensive question,
/// and a deactivated device must not be able to ask it at all — otherwise a stolen phone still
/// learns a territory's shape from how long a refusal takes.
/// </para>
/// <para>
/// <b>Exclusivity is on pull and bind, never on push.</b> That asymmetry is the whole of A8: a
/// device that is no longer the rep's may still hold captured work, and refusing to accept it would
/// be losing it. Because transactional records are device-owned, append-only and idempotent by
/// mutation id, an old device draining cannot create a competing writer.
/// </para>
/// </remarks>
public sealed class Device : AggregateRoot, ITenantOwned, IAuditable
{
    private Device() { }

    public Guid Id { get; private set; }

    /// <summary>The Keycloak subject this device belongs to.</summary>
    public string UserId { get; private set; } = null!;

    /// <summary>What the rep sees in a device list. Free text from the client; never trusted.</summary>
    public string Name { get; private set; } = null!;

    public DateTimeOffset BoundAtUtc { get; private set; }

    public bool IsActive { get; private set; }

    public DeactivationReason? DeactivatedBecause { get; private set; }

    public DateTimeOffset? DeactivatedAtUtc { get; private set; }

    public TenantId TenantId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset? ModifiedAtUtc { get; set; }
    public string? ModifiedBy { get; set; }

    public static Device Bind(string userId, string name, DateTimeOffset at) => new()
    {
        Id = Guid.CreateVersion7(),
        UserId = userId,
        Name = name,
        BoundAtUtc = at,
        IsActive = true,
    };

    /// <summary>
    /// Stops this device being the active one. Idempotent: deactivating twice keeps the first
    /// reason and the first timestamp, because the second is not new information and the drain
    /// window is measured from when the device actually stopped being trusted.
    /// </summary>
    public void Deactivate(DeactivationReason reason, DateTimeOffset at)
    {
        if (!IsActive) return;

        IsActive = false;
        DeactivatedBecause = reason;
        DeactivatedAtUtc = at;
    }
}
