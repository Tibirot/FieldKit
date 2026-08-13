import { confirmPhoto, presignPhoto } from "@/lib/api/sync";
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
  /** Photographs the server acknowledged this run (`OFF-08`) — W11 slice 13b. */
  confirmed: number;
  /**
   * Photographs in storage the server has not acknowledged yet.
   *
   * Counted separately from {@link failed} because it is a different state and a different fix: the
   * bytes are safe, and what is outstanding is a sentence about them. Usually it means the audit has
   * not been pushed yet, which the next run resolves on its own.
   */
  awaitingConfirmation: number;
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
 * Every photograph in storage the server has not been told about (`OFF-08`) — W11 slice 13b.
 *
 * Indexed for the same reason {@link waiting} is: it is asked on every sync run.
 *
 * The `storedKey` filter is in JavaScript rather than the index because it excludes almost nothing —
 * a row uploaded before version 18, which no device that keeps working will hold for long — and a
 * compound index for it would be machinery for the empty case.
 */
export function awaitingConfirmation(db: FieldKitDatabase): Promise<LocalPhotoBlob[]> {
  return db.blobs
    .where("confirmedAtUtc")
    .equals(WAITING)
    .filter((photo) => photo.uploadedAtUtc !== WAITING && photo.storedKey !== "")
    .sortBy("capturedAtUtc");
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
  const result: UploadResult = {
    uploaded: 0,
    failed: 0,
    abandoned: 0,
    confirmed: 0,
    awaitingConfirmation: 0,
  };

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
        // The server's key, kept because confirming needs it and this device cannot rebuild one —
        // the tenant prefix is not ours to know (W11 slice 13b).
        storedKey: presigned.objectKey,
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

  /*
   * Then tell the server about everything in storage it has not acknowledged (`OFF-08`) — W11 13b.
   *
   * <b>A separate pass, not a step inside the upload.</b> Confirming can fail on its own — the `PUT`
   * went to storage and this goes to the API, and a rep can have signal for one and not the other —
   * so a photograph whose upload succeeded and whose confirmation did not has to be picked up by a
   * later run. Walking the unacknowledged rows covers that and the ones just uploaded with one loop.
   */
  for (const photo of await awaitingConfirmation(db)) {
    if (signal?.aborted) return result;

    try {
      const outcome = await confirmPhoto(accessToken, photo.storedKey, signal);

      /*
       * `unknown` means the audit has not landed yet, which is ordinary: the two transports are
       * independent and the upload can win. Left unconfirmed so the next run asks again, and *not*
       * counted as a failure — nothing is wrong and there is nothing for a rep to do.
       *
       * Zero confirmed with zero unknown is the server saying it already knew, which is the answer a
       * retry gets and is just as good as the first one.
       */
      if (outcome.unknown > 0) {
        result.awaitingConfirmation += 1;
        continue;
      }

      await db.blobs.update(photo.objectKey, { confirmedAtUtc: now.toISOString() });
      result.confirmed += 1;
    } catch (error) {
      // The reason goes on the row for the same purpose it does above: a confirmation that never
      // gets through is otherwise indistinguishable from one nobody tried.
      await db.blobs.update(photo.objectKey, {
        lastFailure: error instanceof Error ? error.message : String(error),
      });
      result.awaitingConfirmation += 1;
    }
  }

  return result;
}

/**
 * How many photographs the back office does not have (`OFF-05`, `OFF-08`) — W11 slice 13b.
 *
 * <b>Sealed audits only, and that is not a detail.</b> A draft's photographs are deliberately never
 * uploaded — the rep can still remove them — so counting one would leave the indicator saying
 * *photographs still to send* for as long as a rep has an audit open, which is most of their day.
 * The count has to mean work that is genuinely outstanding or a rep stops reading it.
 */
export async function outstandingPhotographs(db: FieldKitDatabase): Promise<number> {
  const unconfirmed = await db.blobs.where("confirmedAtUtc").equals(WAITING).toArray();

  if (unconfirmed.length === 0) return 0;

  // The drafts, not the sealed ones: an audit this device has never heard of cannot be a draft, and
  // reading the smaller set keeps a rep mid-round from paying for a scan of the whole table.
  const drafts = new Set(await db.audits.where("status").equals("draft").primaryKeys());

  return unconfirmed.filter((photo) => !drafts.has(photo.auditId)).length;
}

/**
 * Whether the back office has every photograph one audit took (`OFF-05`, `OFF-08`) — W11 slice 13b.
 *
 * <b>Confirmed, not merely uploaded, and the difference is the whole slice.</b> An audit whose JSON
 * the back office has and whose pictures it does not is a real and ordinary state rather than a
 * half-failure — but so is one whose bytes reached storage while the acknowledgement did not, and
 * from a supervisor's chair those two look the same. The rep is told the truth the *server* holds.
 *
 * An audit with no photographs is complete, which is what `every` on an empty list already says.
 */
export async function evidenceComplete(db: FieldKitDatabase, auditId: string): Promise<boolean> {
  const photos = await db.blobs.where("auditId").equals(auditId).toArray();

  return photos.every((photo) => photo.confirmedAtUtc !== WAITING);
}
