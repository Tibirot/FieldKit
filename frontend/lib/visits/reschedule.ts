import type { FieldKitDatabase, OutboxEntry, ReferencePlannedVisit } from "@/lib/sync/db";
import { enqueue } from "@/lib/sync/outbox";

/**
 * A call the rep moved to another day inside its cycle (`JRN-06`, `BR-JRN-4`) — W12 F2b.
 *
 * <b>The third clause of `JRN-06`, and the last to get a device.</b> W7 slice 5 named three rep-side
 * annotations — not-visited with a reason, unplanned visit, reschedule within cycle. The device had
 * one after W9, two after W11½ R4, and `RescheduleAsync`, the `rescheduled` wire slot, the sync
 * manager's `"RescheduledCall" → "rescheduled"` mapping and the back office's *Moved from* line all
 * existed with nothing on any phone that could produce one (regression F2).
 *
 * <b>It could not be built before F2a.</b> `BR-JRN-4` lets a call move inside the cycle its
 * frequency put it in, which needs the call's stored cycle length and the plan's first day — and the
 * round carried neither, so a device had no way to tell a rep which days would be accepted. F2a
 * sends the answer instead: `movableFrom`/`movableTo`, computed by the same function the server's
 * refusal reads. Nothing here re-derives the rule; it compares two dates against a range.
 *
 * <b>The annotation is queued and nothing local is rewritten.</b> The rule
 * [not-visited](./not-visited.ts) and [unplanned](./unplanned.ts) both follow, for the reason the
 * journey spec states: `ref_planned_visits` is a copy of the server's round, and moving the call's
 * date in it would look right until the server refused the mutation — after which the row would be
 * wrong forever, because a refused annotation changes no row version and the next delta therefore
 * sends nothing back to correct it.
 *
 * So the call stays on today's round with *moved to Thursday* against it, until the pull brings back
 * a round that agrees.
 */

/** Why the device refused, in the same shape the server's refusals take (ADR-0012). */
export type RescheduleRefusal =
  /**
   * `BR-JRN-4`: outside the days this call may move to.
   *
   * The server's own code, because it is the server's own rule — the device is checking the window
   * it was sent rather than reaching a second opinion about it.
   */
  | "journey.visit.outsideCycle"
  /** Already moved from this device, and the server has not taken it yet. */
  | "journey.visit.alreadyReported";

export type RescheduleResult =
  | { ok: true; mutationId: string }
  | { ok: false; refusal: RescheduleRefusal };

/**
 * The move this device has queued for a call, if it has queued one.
 *
 * Read from the outbox rather than from a store of its own — the outbox already answers "what has
 * this device said that the server may not have heard", and a second place to keep it would be a
 * second thing to keep in step.
 */
export async function queuedReschedule(
  db: FieldKitDatabase,
  plannedVisitId: string,
): Promise<{ date: string; failed: boolean } | undefined> {
  const entries = await db.outbox.where("subjectId").equals(plannedVisitId).toArray();

  const latest = entries
    // No status filter: an accepted mutation is *deleted* from the outbox rather than marked, so a
    // row still being here is exactly what "the server has not taken it yet" means (`OFF-04`).
    .filter((entry) => entry.type === "RescheduledCall")
    .sort((left, right) => left.createdAt - right.createdAt)
    .at(-1);

  if (!latest) return undefined;

  return {
    date: dateOf(latest) ?? "",

    // The one case a rep has to do something about: the server said no and re-sending will not
    // change its mind — a plan regenerated under them, most likely (`OFF-09`).
    failed: latest.status === "failed",
  };
}

function dateOf(entry: OutboxEntry): string | undefined {
  const payload = entry.payload as { date?: unknown } | null;

  return typeof payload?.date === "string" ? payload.date : undefined;
}

/**
 * Whether this call may be moved at all, and to which days.
 *
 * <b>Null is a real answer.</b> An **unplanned** call belongs to no cycle — `BR-JRN-4` is about
 * moving a call within the cycle its *frequency* put it in, and a call nobody planned was never in
 * one — so it can never be moved, and the server refuses it. A call held from before local store
 * version 21 answers the same until the next pull, which is the honest thing for a device that has
 * not been told the window to say.
 */
export function movable(
  call: ReferencePlannedVisit,
): { from: string; to: string } | undefined {
  return call.movableFrom !== null && call.movableTo !== null
    ? { from: call.movableFrom, to: call.movableTo }
    : undefined;
}

/**
 * Queues "I am calling here on a different day".
 *
 * <b>The window is checked here as well as on the server</b>, because offline there is nobody else
 * to check it — and a rep who picked a day outside the cycle should be told at the shop rather than
 * at reconnect, which is the same argument the geofence and the mandatory-step rules make. The check
 * is a comparison against the range the server sent, not a second implementation of `BR-JRN-4`;
 * `<input type="date">` takes `min` and `max` from the same two values, so this is the guard for a
 * rep who types rather than picks.
 */
export async function reschedule(
  db: FieldKitDatabase,
  call: ReferencePlannedVisit,
  date: string,
): Promise<RescheduleResult> {
  const window = movable(call);

  if (!window || date < window.from || date > window.to) {
    return { ok: false, refusal: "journey.visit.outsideCycle" };
  }

  /*
   * One move per call from this device.
   *
   * A second is not refused by the server — it would move the call again, from wherever the first
   * put it — and that is exactly the problem. The two mutations are pushed in order and the rep
   * would have queued a move from a day they never saw the call on, having only ever been offered
   * the window the *original* date sat in.
   */
  if (await queuedReschedule(db, call.id)) {
    return { ok: false, refusal: "journey.visit.alreadyReported" };
  }

  const entry = await enqueue(db, {
    type: "RescheduledCall",
    // The call, as `NotVisitedCall` does — this annotation changes a row the server already has,
    // so there is a server-side id to key on and a sync badge has something to point at.
    subjectId: call.id,
    payload: { plannedVisitId: call.id, date },
  });

  return { ok: true, mutationId: entry.mutationId };
}
