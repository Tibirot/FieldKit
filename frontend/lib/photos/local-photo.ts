import {
  type FieldKitDatabase,
  type LocalAudit,
  type LocalAuditSection,
  type LocalPhotoBlob,
  WAITING,
} from "@/lib/sync/db";

/**
 * Photographs on the device, and the keys the audit refers to them by (`AUD-05`, `OFF-08`, `B5`) —
 * W11 slice 11.
 *
 * <b>Two rows for one photograph, always written together.</b> The audit carries a reference and the
 * `blobs` table carries the image, and either alone is a defect: a reference with no blob is a
 * picture that can never be uploaded, and a blob with no reference is a picture nothing will ever
 * ask for. So every function here is a transaction over both.
 *
 * <b>The blob is the one thing on this device the server cannot re-send.</b> Reference data is a
 * copy; a photograph is an original until it is uploaded, which is why the image is stored before the
 * screen confirms anything (`OFF-01b`).
 */

/**
 * Where an image will live in object storage.
 *
 * <b>Minted on the device</b>, like the audit's own id: the reference and the upload have to agree
 * without a round trip, and the rep is usually offline when the shutter goes.
 *
 * <b>The audit id is in the path</b>, so everything one audit produced is one prefix — which is what
 * makes a rejected audit's images findable, and what a reader needs to fetch them. The extension is
 * `.jpg` because `downscale` always encodes JPEG; a key claiming a format the object is not would
 * mislead every consumer that trusts it.
 */
export function objectKeyFor(auditId: string, photoId: string): string {
  return `audits/${auditId}/${photoId}.jpg`;
}

/**
 * Stores a downscaled photograph and points the audit at it.
 *
 * <b>Takes the blob rather than the camera file</b>, so the caller has already applied `B5`'s size
 * policy. This module cannot check that — a blob carries no dimensions — and pretending to would be
 * a guard that passes whatever it is given.
 *
 * Refuses a sealed audit, as every other write on this store does: `BR-AUD-6` is append-only after
 * sync, and a photograph added afterwards would reference an object no audit record mentions.
 */
export async function attachPhoto(
  db: FieldKitDatabase,
  request: {
    auditId: string;
    section: LocalAuditSection;
    image: Blob;
    photoId: string;
    now: Date;
  },
): Promise<LocalAudit | undefined> {
  const objectKey = objectKeyFor(request.auditId, request.photoId);

  return db.transaction("rw", db.audits, db.blobs, async () => {
    const current = await db.audits.get(request.auditId);
    if (!current || current.status !== "draft") return undefined;

    const blob: LocalPhotoBlob = {
      objectKey,
      auditId: request.auditId,
      section: request.section,
      image: request.image,
      bytes: request.image.size,
      capturedAtUtc: request.now.toISOString(),
      // Waiting, and never tried — the uploader's starting state (`OFF-08`, W11 slice 12b).
      uploadedAtUtc: WAITING,
      attempts: 0,
    };

    await db.blobs.put(blob);

    const updated: LocalAudit = {
      ...current,
      photos: [...current.photos, { section: request.section, objectKey }],
      updatedAtUtc: request.now.toISOString(),
    };

    await db.audits.put(updated);

    return updated;
  });
}

/**
 * Removes a photograph the rep does not want kept.
 *
 * <b>The image goes with the reference.</b> Leaving the blob behind would upload a picture no audit
 * mentions and no reader can reach — bytes the rep is paying to send and a supervisor will never see.
 * The server refuses a duplicate object key in one audit, so nothing here depends on the key being
 * reusable afterwards; it is a fresh id each time regardless.
 */
export async function removePhoto(
  db: FieldKitDatabase,
  auditId: string,
  objectKey: string,
  now: Date,
): Promise<LocalAudit | undefined> {
  return db.transaction("rw", db.audits, db.blobs, async () => {
    const current = await db.audits.get(auditId);
    if (!current || current.status !== "draft") return undefined;

    await db.blobs.delete(objectKey);

    const updated: LocalAudit = {
      ...current,
      photos: current.photos.filter((photo) => photo.objectKey !== objectKey),
      updatedAtUtc: now.toISOString(),
    };

    await db.audits.put(updated);

    return updated;
  });
}

/** Every image one audit is holding, oldest first — the order the rep took them. */
export function photosFor(db: FieldKitDatabase, auditId: string): Promise<LocalPhotoBlob[]> {
  return db.blobs
    .where("auditId")
    .equals(auditId)
    .sortBy("capturedAtUtc");
}
