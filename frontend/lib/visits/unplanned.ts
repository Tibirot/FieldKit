import type { FieldKitDatabase, OutboxEntry, ReferenceOutlet } from "@/lib/sync/db";
import { enqueue } from "@/lib/sync/outbox";
import { outlets as heldOutlets, plannedVisits } from "@/lib/sync/reference";

/**
 * A call the rep made that nobody planned (`JRN-06`, `BR-JRN-4`) — W11½ R4.
 *
 * <b>The device half of an annotation whose every other layer already existed.</b>
 * `JourneyIngestService.AddUnplannedAsync`, `JourneyPlan.TryAddUnplanned`, the `unplanned` wire slot,
 * the sync manager's `"UnplannedCall" → "unplanned"` mapping and the back office's *Unplanned* badge
 * were all built under W7 slice 5; the only mention of `UnplannedCall` in the front end was the slot
 * mapping — a route for a mutation the device could not produce (regression F7). `JRN-06` names it a
 * **Must**, so this was an unshipped requirement rather than a deferral.
 *
 * <b>The annotation is queued and nothing local is invented.</b> Exactly the rule
 * [not-visited](./not-visited.ts) follows, and for the same reason: `ref_planned_visits` is a copy of
 * the server's round, and writing a call into it would look right until the server refused the
 * mutation — after which the row would be wrong forever, because a refused annotation changes no row
 * version and the next delta therefore sends nothing back to correct it.
 *
 * <b>The visit does not wait for it.</b> A rep standing in an unplanned shop checks in, works the
 * call and checks out whether or not this annotation is ever accepted; the two reach the server as
 * separate mutations with no ordering between them, because `CapturedVisit` for an unplanned call
 * carries no `plannedVisitId` and so depends on nothing.
 */

/** Why the device refused, in the same shape the server's refusals take (ADR-0012). */
export type UnplannedRefusal =
  /** This device has already queued one for this shop on this day. */
  "journey.visit.alreadyReported";

export type UnplannedResult =
  | { ok: true; mutationId: string }
  | { ok: false; refusal: UnplannedRefusal };

/**
 * The unplanned call this device has queued for a shop on a day, if it has queued one.
 *
 * Read from the outbox rather than from a store of its own — the outbox already answers "what has
 * this device said that the server may not have heard", and the round cannot answer it because an
 * unplanned call does not exist server-side until the mutation lands.
 *
 * <b>Keyed by shop *and* day.</b> `subjectId` alone is the outlet, so a rep who called at a shop
 * unplanned on Monday would be refused a second unplanned call there on Tuesday — which is an
 * ordinary week, not a duplicate.
 */
export async function queuedUnplanned(
  db: FieldKitDatabase,
  outletId: string,
  date: string,
): Promise<{ failed: boolean } | undefined> {
  const entries = await db.outbox.where("subjectId").equals(outletId).toArray();

  const latest = entries
    // No status filter: an accepted mutation is *deleted* from the outbox rather than marked, so a
    // row still being here is exactly what "the server has not taken it yet" means (`OFF-04`).
    .filter((entry) => entry.type === "UnplannedCall" && dateOf(entry) === date)
    .sort((left, right) => left.createdAt - right.createdAt)
    .at(-1);

  if (!latest) return undefined;

  // The one case a rep has to do something about: the server said no and re-sending will not change
  // its mind — no published round covering the day, most likely (`OFF-09`).
  return { failed: latest.status === "failed" };
}

function dateOf(entry: OutboxEntry): string | undefined {
  const payload = entry.payload as { date?: unknown } | null;

  return typeof payload?.date === "string" ? payload.date : undefined;
}

/**
 * Queues "I called here, and it was not on my round".
 *
 * <b>One per shop per day, checked here as well as on the server.</b> This is the only annotation
 * that *creates* a row rather than changing one, so a duplicate would put the same shop on the same
 * day twice and overstate the rep's coverage — `AddUnplannedAsync` refuses the second for exactly
 * that reason. Refusing it on the device too means the rep is told at the shop rather than at
 * reconnect, which is the argument the geofence and the mandatory-step rules already make.
 *
 * The date is the rep's own business day, passed in rather than read from a clock here — the same
 * `todayOn` value the round is drawn from, so an unplanned call lands on the day the rep is standing
 * in rather than the one UTC is having.
 */
export async function addUnplanned(
  db: FieldKitDatabase,
  outletId: string,
  date: string,
): Promise<UnplannedResult> {
  if (await queuedUnplanned(db, outletId, date)) {
    return { ok: false, refusal: "journey.visit.alreadyReported" };
  }

  const entry = await enqueue(db, {
    type: "UnplannedCall",
    // The shop, because that is what the annotation is about and what a sync badge on the picker
    // would be pointing at. There is no server-side id to key on: the row this creates does not
    // exist until the mutation is accepted.
    subjectId: outletId,
    payload: { outletId, date },
  });

  return { ok: true, mutationId: entry.mutationId };
}

/**
 * The shops a rep can call at unplanned: everything this device holds, minus today's round.
 *
 * <b>Today's calls are left out because the round already offers them</b>, and a second list of the
 * same shops would be a second place to tap one — with the worse of the two outcomes, since a stop
 * opened from here would not carry the planned call and the supervisor's coverage figure would show
 * the call still outstanding.
 *
 * Sorted by the device's own rule rather than re-derived: `outlets` already returns them by name,
 * which is what the rep reads.
 */
export async function callableOutlets(
  db: FieldKitDatabase,
  date: string,
): Promise<ReferenceOutlet[]> {
  const [held, planned] = await Promise.all([heldOutlets(db), plannedVisits(db, date)]);
  const onTheRound = new Set(planned.map((call) => call.outletId));

  return held.filter((outlet) => !onTheRound.has(outlet.id));
}

/**
 * The shops matching what the rep has typed, by name or by code.
 *
 * <b>Code as well as name, and that is not a nicety.</b> A chain puts twenty shops called *Mega
 * Image* on one territory, and the code is the only thing that tells them apart — which is why it
 * travels to the device at all and why the picker prints it under every row.
 */
export function matching(outlets: readonly ReferenceOutlet[], search: string): ReferenceOutlet[] {
  const wanted = search.trim().toLocaleLowerCase();
  if (!wanted) return [...outlets];

  return outlets.filter(
    (outlet) =>
      outlet.name.toLocaleLowerCase().includes(wanted) ||
      outlet.code.toLocaleLowerCase().includes(wanted),
  );
}
