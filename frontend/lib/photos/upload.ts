import { presignPhoto } from "@/lib/api/sync";
import { type FieldKitDatabase, type LocalPhotoBlob, WAITING } from "@/lib/sync/db";

/**
 * The second transport: photographs to object storage, on their own schedule (`OFF-08`, `B5`) —
 * W11 slice 12b.
 *
 * <b>Independent of the JSON push, and that is the requirement rather than an optimisation.</b> A
 * downscaled JPEG is twenty times a visit's JSON, and a rep in a back room with one bar must not
 * have their day's work held behind a picture. So the push goes first and finishes; this runs after,
 * fails on its own, and retries on its own.
 *
 * <b>Which means an audit routinely lands before its photographs.</b> The server says so at length on
 * `CapturedPhoto`: a reader shows a gap, never an error.
 */

/** How many failures before a photograph stops being retried on every run. */
const GIVE_UP_AFTER = 8;

/** What one upload pass did, for the indicator and for a test to assert on. */
export type UploadResult = {
  uploaded: number;
  failed: number;
  /** Photographs skipped because they have failed too often — see {@link GIVE_UP_AFTER}. */
  abandoned: number;
};

/**
 * Every photograph still waiting, oldest first.
 *
 * <b>An index seek, not a scan.</b> Dexie hands back whole records, so filtering in JavaScript would
 * pull every image a rep has taken this week into memory to answer a question about one field.
 */
export function waiting(db: FieldKitDatabase): Promise<LocalPhotoBlob[]> {
  return db.blobs.where("uploadedAtUtc").equals(WAITING).sortBy("capturedAtUtc");
}

/**
 * Uploads what is waiting, one photograph at a time.
 *
 * <b>Serially, deliberately.</b> The connection this runs on is the one the rep is short of, and
 * three parallel uploads on a bad signal finish later than three sequential ones and are likelier to
 * time out together. It also keeps the failure story simple: one picture fails, the next still tries.
 *
 * <b>Only for audits that have been sealed.</b> A draft's photographs can still be removed by the
 * rep, and uploading one would spend a rep's data on an image that may be deleted a minute later —
 * and leave an object in storage that no audit will ever name.
 *
 * <b>It never throws.</b> Losing signal mid-round is the ordinary case; the result says how far it
 * got and the next run carries on.
 */
export async function uploadPhotos(
  db: FieldKitDatabase,
  accessToken: string,
  now: Date,
  signal?: AbortSignal,
): Promise<UploadResult> {
  const result: UploadResult = { uploaded: 0, failed: 0, abandoned: 0 };

  for (const photo of await waiting(db)) {
    if (signal?.aborted) return result;

    if (photo.attempts >= GIVE_UP_AFTER) {
      /*
       * Left in place rather than deleted, and counted rather than ignored.
       *
       * A photograph that has failed eight times is failing for a reason retrying will not fix — a
       * key the server refuses, an image the device cannot read — and a rep who is offline all week
       * should not spend the first seconds of every reconnect on it. Deleting it would throw away the
       * only copy of something a supervisor might still want; slice 13 is where a rep gets told.
       */
      result.abandoned += 1;
      continue;
    }

    const audit = await db.audits.get(photo.auditId);

    // A draft's pictures are still the rep's to remove. Not a failure — nothing has gone wrong — so
    // it is not counted and not retried; the next run after the seal picks it up.
    if (!audit || audit.status === "draft") continue;

    try {
      // The key without the tenant: the device does not know its tenant id, and the server writes
      // the prefix from the token (W11 slice 12a).
      const presigned = await presignPhoto(accessToken, photo.objectKey, signal);

      const response = await fetch(presigned.url, {
        method: "PUT",
        // Required by the Blob REST API for a block blob, and the whole of what the SAS authorises.
        headers: { "x-ms-blob-type": "BlockBlob", "Content-Type": photo.image.type },
        body: photo.image,
        signal,
      });

      if (!response.ok) throw new Error(`Upload refused with ${response.status}.`);

      await db.blobs.update(photo.objectKey, {
        uploadedAtUtc: now.toISOString(),
        attempts: 0,
        lastFailure: "",
      });
      result.uploaded += 1;
    } catch (error) {
      /*
       * Counted, *explained*, and left waiting. The failure is usually the connection, and the only
       * useful response is to try again later — which the count makes bounded.
       *
       * <b>The reason is kept, and that is this slice's second finding.</b> Swallowing it made a
       * Content Security Policy refusing every `PUT` indistinguishable from a bad signal, and the
       * retry made it look like a bad signal forever. A message on the row is what lets slice 13 tell
       * a rep something true, and what would have made this obvious a slice earlier.
       *
       * `attempts` is incremented rather than the row rewritten, because a rep may be photographing
       * another shelf on the same device while this runs: a whole-record `put` would carry a stale
       * image back over a newer one.
       */
      await db.blobs.update(photo.objectKey, {
        attempts: photo.attempts + 1,
        lastFailure: error instanceof Error ? error.message : String(error),
      });
      result.failed += 1;
    }
  }

  return result;
}

/**
 * Whether every photograph one audit took has reached storage.
 *
 * The question slice 13 asks to tell *synced* from *uploaded* — an audit whose JSON the back office
 * has, whose pictures it does not, is a real and ordinary state rather than a half-failure.
 */
export async function fullyUploaded(db: FieldKitDatabase, auditId: string): Promise<boolean> {
  const photos = await db.blobs.where("auditId").equals(auditId).toArray();

  return photos.every((photo) => photo.uploadedAtUtc !== WAITING);
}
