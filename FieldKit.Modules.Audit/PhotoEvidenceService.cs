using FieldKit.Modules.Audit.Contracts;
using FieldKit.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace FieldKit.Modules.Audit;

/// <summary>
/// Records that a photograph reached storage (<c>OFF-08</c>, <c>B5</c>) — W11 slice 13a.
/// </summary>
/// <remarks>
/// <para>
/// <b>Keys, not audit ids.</b> The device confirms what it uploaded, one object at a time, and it
/// uploads across audits in whatever order the queue gives it. Asking it to group by audit would make
/// it hold state it has no reason to keep, and would make a confirmation for a two-photograph audit
/// wrong whenever only one of them had gone.
/// </para>
/// <para>
/// <b>Unknown keys are counted, never refused.</b> The push and the upload are independent transports
/// and either can win (<c>B5</c>), so a key naming an audit that has not landed yet is the ordinary
/// case this design exists for — and a key naming nothing at all costs a row that stays
/// <see cref="PhotoEvidenceState.Expected"/> and eventually reads as missing. Refusing the batch would
/// punish the honest photographs in it.
/// </para>
/// <para>
/// <b>The tenant filter is not written here.</b> Every query in this module runs under the global
/// filter on <see cref="ITenantOwned"/>, so a key belonging to another tenant simply matches nothing —
/// the same reason the endpoint can take keys from a device without checking whose they are.
/// </para>
/// </remarks>
internal sealed class PhotoEvidenceService(
    AuditDbContext db, IClock clock, PhotoMetrics metrics) : IPhotoEvidence
{
    /// <summary>
    /// How many keys one call may name.
    /// </summary>
    /// <remarks>
    /// The device uploads serially and confirms what it has just sent, so a real batch is one or two.
    /// The cap is here because the parameter list of the <c>IN</c> this becomes is the thing a caller
    /// could make unbounded, and a query plan is not the place to discover that.
    /// </remarks>
    public const int MaximumKeys = 100;

    public async Task<PhotoConfirmation> ConfirmUploadedAsync(
        IReadOnlyCollection<string> objectKeys, CancellationToken cancellationToken = default)
    {
        var keys = objectKeys
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Select(key => key.Trim())
            .Distinct()
            .Take(MaximumKeys)
            .ToList();

        if (keys.Count == 0) return new PhotoConfirmation(0, 0);

        var entries = await db.Photos
            .Where(photo => keys.Contains(photo.ObjectKey))
            .ToListAsync(cancellationToken);

        // `Confirm` answers false for a reference already confirmed, so a device that lost the answer
        // to its first call and asked again is told what is true — nothing changed — and the stored
        // time keeps meaning the upload rather than the retry.
        var confirmed = entries.Count(entry => entry.Confirm(clock.UtcNow));

        await db.SaveChangesAsync(cancellationToken);

        /*
         * Unknown counts keys, not entries.
         *
         * Two references can carry the same key only if the audit was refused for it (`AUD-05` treats
         * a duplicate as malformed), so the difference here is keys that matched nothing — which is
         * what the device wants to hear about, because it is the one case where confirming again
         * later is worth doing.
         */
        var matched = entries.Select(entry => entry.ObjectKey).ToHashSet(StringComparer.Ordinal);

        // The level only moves when a photograph is referenced or confirmed, so recording it at both
        // is exact rather than sampled — and a loop could not do it at all, because `PhotoEntry` is
        // tenant-owned and a background service has no principal to read a tenant from (W13 slice 4,
        // see `PhotoMetrics`).
        await metrics.ReportPendingAsync(db, cancellationToken);

        return new PhotoConfirmation(confirmed, keys.Count(key => !matched.Contains(key)));
    }

}
