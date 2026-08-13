using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FieldKit.Modules.Sync;

/// <summary>
/// Teaches object storage to accept an upload from a browser (<c>OFF-08</c>, <c>B5</c>) — W11 12c.
/// </summary>
/// <remarks>
/// <para>
/// <b>A presigned <c>PUT</c> from a page is a cross-origin request, and a preflighted one.</b> The
/// upload carries <c>x-ms-blob-type</c>, which makes it non-simple, so the browser sends
/// <c>OPTIONS</c> first and storage answers it only if a CORS rule names the calling origin. Without
/// the rule the upload fails <i>after</i> the Content Security Policy allows it — the second of two
/// invisible walls between a photograph and the place it is meant to go.
/// </para>
/// <para>
/// <b>Applied by the API at startup rather than by an operator.</b> A deployment that forgets this
/// looks, on every device, exactly like a network that never works: the presign succeeds, the upload
/// fails, the retry hides it. Storage configuration is not the sort of thing that should live only in
/// a runbook when the code that depends on it can assert it in a second.
/// </para>
/// <para>
/// <b>It never stops the host.</b> A storage account that cannot be configured is a degraded
/// deployment, not a dead one: audits, orders and visits do not touch it, and refusing to start would
/// take the whole API down over photographs.
/// </para>
/// </remarks>
public sealed class PhotoStorageCors(
    BlobServiceClient blobs,
    IConfiguration configuration,
    ILogger<PhotoStorageCors> logger) : IHostedService
{
    /// <summary>How long a browser may cache the preflight answer.</summary>
    /// <remarks>
    /// An hour. The rule changes when a deployment's front-end origin does, which is a deploy — and
    /// paying a preflight round trip per upload on a shop's connection is the cost of a shorter one.
    /// </remarks>
    private const int PreflightCacheSeconds = 3600;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var origin = configuration["FIELDKIT_WEB_ORIGIN"];

        if (string.IsNullOrWhiteSpace(origin))
        {
            // Nothing to allow. Said out loud, because the symptom otherwise is every photograph on
            // every device silently failing to upload.
            logger.LogWarning(
                "No FIELDKIT_WEB_ORIGIN is configured, so object storage will refuse browser uploads.");

            return;
        }

        try
        {
            var properties = await blobs.GetPropertiesAsync(cancellationToken);

            /*
             * Replaced rather than appended, and that is deliberate.
             *
             * Appending would grow the rule list by one on every restart until the account's limit
             * refused the write — and would leave a previous deployment's origin allowed long after
             * it stopped existing. One deployment, one origin, set every time it starts.
             */
            properties.Value.Cors.Clear();
            properties.Value.Cors.Add(new BlobCorsRule
            {
                AllowedOrigins = origin,
                // `PUT` for the upload and `OPTIONS` for the preflight that precedes it. Not `GET`:
                // a rule that let a browser read would undo the presigned URL being write-only.
                AllowedMethods = "PUT,OPTIONS",
                // What the uploader actually sends. `x-ms-blob-type` is required by the Blob REST API
                // for a block blob, and a rule that omits it fails the preflight on the one header
                // the request cannot go without.
                AllowedHeaders = "x-ms-blob-type,Content-Type",
                ExposedHeaders = "",
                MaxAgeInSeconds = PreflightCacheSeconds,
            });

            await blobs.SetPropertiesAsync(properties.Value, cancellationToken);

            logger.LogInformation("Object storage now accepts photo uploads from {Origin}.", origin);
        }
        catch (Exception error)
        {
            logger.LogError(
                error,
                "Could not configure object storage for browser uploads from {Origin}; photographs will not upload.",
                origin);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
