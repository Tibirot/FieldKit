namespace FieldKit.Modules.Audit.Contracts;

/// <summary>
/// What one confirmation was worth (<c>OFF-08</c>, <c>B5</c>) — W11 slice 13a.
/// </summary>
/// <param name="Confirmed">
/// How many references this call moved from *expected* to *arrived*. Zero is an ordinary answer, not
/// a failure: the device retries a confirmation it is unsure about, and the second one has nothing
/// left to do.
/// </param>
/// <param name="Unknown">
/// Keys naming no reference this tenant holds. Counted rather than refused — the push and the upload
/// are independent transports and either can win (<c>B5</c>), so a photograph whose audit has not
/// landed yet is the case the split exists to support, and the device will confirm it again.
/// </param>
public sealed record PhotoConfirmation(int Confirmed, int Unknown);

/// <summary>
/// Tells an audit that a photograph it references has actually arrived.
/// </summary>
/// <remarks>
/// <para>
/// <b>Write-only, and separate from <see cref="IAuditIngest"/>.</b> Ingest is how an audit comes into
/// existence and is refused when it is wrong; this is a fact about an object in storage, arriving on
/// its own transport, minutes or hours later. Folding it into ingest would make a consumer that only
/// wanted to say "the bytes are there" acquire the ability to file audits — the same read/write split
/// <see cref="IAuditQuery"/> and <see cref="IAuditIngest"/> already make.
/// </para>
/// <para>
/// <b>Sync calls this; the endpoint lives there.</b> The device knows one API, and the tenant and the
/// rep come from the token Sync is already holding — never from the payload.
/// </para>
/// </remarks>
public interface IPhotoEvidence
{
    /// <summary>
    /// Records that <paramref name="objectKeys"/> are now in storage.
    /// </summary>
    /// <param name="objectKeys">
    /// Full keys, tenant prefix included — what the presign call returned, not what the device minted.
    /// </param>
    /// <remarks>
    /// Idempotent by construction: it records *when* the first confirmation arrived and leaves an
    /// already-confirmed reference alone. A device that loses its answer and asks again gets the same
    /// outcome, and the timestamp keeps meaning the upload rather than the retry.
    /// </remarks>
    Task<PhotoConfirmation> ConfirmUploadedAsync(
        IReadOnlyCollection<string> objectKeys, CancellationToken cancellationToken = default);
}
