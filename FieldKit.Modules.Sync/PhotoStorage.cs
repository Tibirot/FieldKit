using Azure.Storage.Blobs;
using Azure.Storage.Sas;
using FieldKit.SharedKernel;

namespace FieldKit.Modules.Sync;

/// <summary>
/// A short-lived, write-only URL for one photograph (<c>OFF-08</c>, <c>B5</c>) — W11 slice 12a.
/// </summary>
/// <param name="Url">Where to <c>PUT</c> the bytes. Carries its own authorisation and nothing else.</param>
/// <param name="ObjectKey">
/// The full path the object will occupy, tenant prefix included — what a reader will fetch it by,
/// and what the audit's reference resolves to.
/// </param>
/// <param name="ExpiresAtUtc">
/// When the URL stops working. Returned rather than left implicit because the device decides whether
/// it is worth starting an upload: a rep with one bar and a 22 KB JPEG has time, and one who has just
/// walked into a chiller aisle may not.
/// </param>
public sealed record PresignedUpload(Uri Url, string ObjectKey, DateTimeOffset ExpiresAtUtc);

/// <summary>
/// Where shelf photographs go, and who is allowed to put one there.
/// </summary>
/// <remarks>
/// <para>
/// <b>An interface because the endpoint's rules are worth testing without a storage account</b>, and
/// because the two runtimes differ in exactly one respect — how a URL is signed. What it deliberately
/// is *not* is a seam for a filesystem implementation: presigned URLs are a Blob feature, and a fake
/// that hands back a local path would leave the shipped path unexercised.
/// </para>
/// </remarks>
public interface IPhotoStorage
{
    /// <summary>
    /// Mints a URL that may write <paramref name="objectKey"/> and nothing else, for
    /// <paramref name="lifetime"/>.
    /// </summary>
    Task<PresignedUpload> PresignUploadAsync(
        string objectKey,
        TimeSpan lifetime,
        CancellationToken ct);
}

/// <summary>
/// Azure Blob Storage — Azurite in development, the real service when published.
/// </summary>
/// <remarks>
/// <para>
/// <b>A user delegation SAS where the client holds a token, an account-key SAS where it holds a
/// key.</b> Aspire wires the emulator with a connection string (key) and a published deployment with
/// a managed identity (token), and the two sign differently. Branching here rather than forcing one
/// mode keeps development from needing a secret and production from needing one *at all* — a signing
/// key in an environment variable is precisely the thing managed identity exists to remove.
/// </para>
/// <para>
/// <b>The permission is <c>Write</c> only, and never <c>Read</c> or <c>Delete</c>.</b> A device needs
/// to put one object; it has no business fetching another rep's photograph or removing evidence. And
/// <b>the SAS names one blob</b>, not the container: a container-scoped URL would let a device that
/// obtained one write anywhere under the tenant, including over an audit already filed.
/// </para>
/// </remarks>
public sealed class BlobPhotoStorage(BlobServiceClient blobs, IClock clock) : IPhotoStorage
{
    /// <summary>The container the AppHost creates. One, with the tenant in the object's path.</summary>
    public const string ContainerName = "photos";

    public async Task<PresignedUpload> PresignUploadAsync(
        string objectKey,
        TimeSpan lifetime,
        CancellationToken ct)
    {
        var container = blobs.GetBlobContainerClient(ContainerName);

        // Created on demand rather than assumed: the emulator starts empty, and a deployment whose
        // container was removed should heal rather than fail every upload until somebody notices.
        await container.CreateIfNotExistsAsync(cancellationToken: ct);

        var blob = container.GetBlobClient(objectKey);
        // The injected clock, never static time — the architecture gate banned this line's first
        // draft, and it was right to: an expiry is a decision about *when*, and a test that cannot
        // move the clock cannot check that a lapsed URL stops working.
        var expiresAt = clock.UtcNow.Add(lifetime);

        var permissions = new BlobSasBuilder(BlobSasPermissions.Write, expiresAt)
        {
            BlobContainerName = ContainerName,
            BlobName = objectKey,
            Resource = "b",
        };

        if (blob.CanGenerateSasUri)
        {
            // The account key is in the connection string — development, and any deployment that
            // still uses one.
            return new PresignedUpload(blob.GenerateSasUri(permissions), objectKey, expiresAt);
        }

        /*
         * No key to sign with, so the *service* signs on our behalf against the identity the client
         * already holds. The delegation key is itself short-lived and is requested per call rather
         * than cached: caching one would mean holding a credential in memory for its whole lifetime
         * to save a round trip on an operation that is already talking to storage.
         */
        var delegation = await blobs.GetUserDelegationKeyAsync(
            clock.UtcNow.AddMinutes(-5),
            expiresAt,
            ct);

        var signed = permissions.ToSasQueryParameters(delegation.Value, blobs.AccountName);

        return new PresignedUpload(
            new UriBuilder(blob.Uri) { Query = signed.ToString() }.Uri,
            objectKey,
            expiresAt);
    }
}
