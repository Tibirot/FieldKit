using System.Text.Json.Serialization;
using FieldKit.BuildingBlocks;
using FieldKit.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace FieldKit.Modules.Sync;

/// <summary>What happened to a mutation the first time it was seen.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<MutationStatus>))]
public enum MutationStatus
{
    Accepted = 1,

    /// <summary>
    /// Refused on its merits — a closed outlet, a discontinued SKU. A rejection is a *result*, not a
    /// failure to record: replaying a rejected mutation must be told the same no, or a device with a
    /// flaky connection retries a bad batch forever.
    /// </summary>
    Rejected = 2,
}

/// <param name="ReasonCode">An ADR-0012 code when rejected, so a device can act on it rather than parse prose.</param>
public sealed record MutationOutcome(MutationStatus Status, string? ReasonCode = null, string? Detail = null);

/// <summary>
/// One mutation this device has already had answered (<c>OFF-04</c>, sync engine §4).
/// </summary>
/// <remarks>
/// Keyed on tenant, device and mutation id. Per device rather than per tenant because the id is
/// minted on the device: two devices are two independent sequences, and colliding them would let one
/// rep's retry be answered with another's result.
/// </remarks>
public sealed class MutationLedgerEntry : ITenantOwned
{
    public TenantId TenantId { get; set; }
    public Guid DeviceId { get; set; }
    public Guid MutationId { get; set; }

    public MutationStatus Status { get; set; }
    public string? ReasonCode { get; set; }
    public string? Detail { get; set; }

    /// <summary>
    /// When it was first answered. The prune horizon reads this — the ledger is kept at least as
    /// long as the maximum offline-plus-retry window, because a very late retry must still dedupe.
    /// </summary>
    public DateTimeOffset RecordedAtUtc { get; set; }
}

/// <summary>
/// The idempotency ledger: exactly-once <em>effect</em> over at-least-once <em>delivery</em>.
/// </summary>
/// <remarks>
/// <para>
/// A device pushes over a connection that is bad by assumption. It retries what it cannot confirm,
/// so the same mutation arrives more than once, and "apply it twice" means two visits at one shop or
/// two orders on one account. The ledger is what makes the second arrival a no-op that still answers
/// correctly.
/// </para>
/// <para>
/// <b>It returns the prior result rather than a "duplicate" status.</b> That is the whole point: the
/// device asked what happened to this mutation, and the true answer is whatever happened the first
/// time. Telling it "duplicate" would leave it knowing less than before it asked.
/// </para>
/// <para>
/// <b>It dedupes transport, not intent.</b> A client that mints two ids for one action gets two
/// mutations and deserves them — that is a different problem, solved where it arises with a business
/// key (sync engine §4).
/// </para>
/// </remarks>
public interface IMutationLedger
{
    /// <summary>The result this mutation already got, or null if it has never been seen.</summary>
    Task<MutationOutcome?> FindAsync(Guid deviceId, Guid mutationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records the outcome <b>without saving</b>, leaving the caller to commit a whole batch's
    /// entries at once.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One <c>SaveChanges</c> per batch rather than per mutation, which is what makes a two-hundred
    /// mutation drain one round trip to Postgres instead of two hundred.
    /// </para>
    /// <para>
    /// <b>What it deliberately does not claim is a transaction spanning the work itself.</b> The work
    /// lives in another module's schema and therefore another <c>DbContext</c>
    /// (<a href="../docs/architecture/adr/0005-persistence-postgres-schema-per-module.md">ADR-0005</a>),
    /// so the visit commits before this row does. Ordering it that way is the safe half: a crash
    /// between the two loses the *record* that the work was answered, never the work — and the
    /// device-minted entity id lets the retry recognise itself (sync engine §4).
    /// </para>
    /// </remarks>
    void Record(Guid deviceId, Guid mutationId, MutationOutcome outcome);
}

internal sealed class MutationLedger(SyncDbContext db, ITenantContext tenant, IClock clock) : IMutationLedger
{
    public async Task<MutationOutcome?> FindAsync(
        Guid deviceId, Guid mutationId, CancellationToken cancellationToken = default)
    {
        var entry = await db.MutationLedger
            .AsNoTracking()
            .SingleOrDefaultAsync(
                candidate => candidate.DeviceId == deviceId && candidate.MutationId == mutationId,
                cancellationToken);

        return entry is null ? null : new MutationOutcome(entry.Status, entry.ReasonCode, entry.Detail);
    }

    public void Record(Guid deviceId, Guid mutationId, MutationOutcome outcome) =>
        db.MutationLedger.Add(new MutationLedgerEntry
        {
            TenantId = tenant.TenantId,
            DeviceId = deviceId,
            MutationId = mutationId,
            Status = outcome.Status,
            ReasonCode = outcome.ReasonCode,
            Detail = outcome.Detail,
            RecordedAtUtc = clock.UtcNow,
        });
}
