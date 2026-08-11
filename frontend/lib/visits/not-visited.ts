import type { FieldKitDatabase } from "@/lib/sync/db";
import { enqueue } from "@/lib/sync/outbox";

/**
 * A call the rep could not make, recorded with no signal (`VIS-07`, `OFF-04`) — W9 slice 9.
 *
 * <b>The annotation is queued, and nothing local is overwritten.</b> `ref_planned_visits` is a copy
 * of the server's round: writing "not visited" into it would look right until the server *refused*
 * the mutation, and then the row would be wrong forever — the next pull sends only rows whose version
 * changed, and a refused annotation changes none. So the outbox is the record of what this device has
 * said, and [today's journey](./today.ts) reads it as an overlay until the round comes back agreeing.
 *
 * <b>The second mutation type the outbox has ever carried.</b> Until now `type` was a field with one
 * value; on the server it is now the discriminator that decides which module's contract runs.
 */

/** Why the device refused, in the same shape the server's refusals take (ADR-0012). */
export type NotVisitedRefusal =
  /** `BR-JRN-2`: a skipped shop with no reason is a gap nobody can act on. */
  | "journey.visit.reasonRequired"
  /** Already reported, and this device has not sent it yet. */
  | "journey.visit.alreadyReported";

export type NotVisitedResult =
  | { ok: true; mutationId: string }
  | { ok: false; refusal: NotVisitedRefusal };

/**
 * The reason this device has queued for a call, if it has queued one.
 *
 * Read from the outbox rather than kept in a store of its own: the outbox already answers "what has
 * this device said that the server may not have heard", and a second place to keep it would be a
 * second thing to keep in step.
 */
export async function queuedNotVisited(
  db: FieldKitDatabase,
  plannedVisitId: string,
): Promise<{ reason: string; failed: boolean } | undefined> {
  const entries = await db.outbox.where("subjectId").equals(plannedVisitId).toArray();

  const mine = entries
    // No status filter, because there is no `sent` state to exclude: an accepted mutation is
    // *deleted* from the outbox rather than marked, so a row still being here is exactly what
    // "the server has not taken it yet" means (`OFF-04`).
    .filter((entry) => entry.type === "NotVisitedCall")
    .sort((left, right) => left.createdAt - right.createdAt);

  const latest = mine.at(-1);
  if (!latest) return undefined;

  const payload = latest.payload as { reason?: unknown };

  return {
    reason: typeof payload.reason === "string" ? payload.reason : "",

    // A refused annotation is the one case a rep has to do something about, and the round is where
    // they will see it. `failed` is the outbox's own word for "the server said no and re-sending
    // will not change its mind" (`OFF-09`).
    failed: latest.status === "failed",
  };
}

/**
 * Queues "I could not make this call", with the rep's reason.
 *
 * <b>The reason is checked here as well as on the server</b>, because offline there is nobody else to
 * check it — and a rep who typed nothing should be told at the shop rather than at reconnect, which
 * is the same argument the geofence and the mandatory-step rules make.
 */
export async function markNotVisited(
  db: FieldKitDatabase,
  plannedVisitId: string,
  reason: string,
): Promise<NotVisitedResult> {
  const written = reason.trim();
  if (!written) return { ok: false, refusal: "journey.visit.reasonRequired" };

  // One annotation per call from this device. A second would be a second mutation the server would
  // answer `alreadyNotVisited` to — accepted, harmlessly, but it would also let a rep believe they
  // had changed a reason the server will never replace.
  if (await queuedNotVisited(db, plannedVisitId)) {
    return { ok: false, refusal: "journey.visit.alreadyReported" };
  }

  const entry = await enqueue(db, {
    type: "NotVisitedCall",
    subjectId: plannedVisitId,
    payload: { plannedVisitId, reason: written },
  });

  return { ok: true, mutationId: entry.mutationId };
}
