import type {
  FieldKitDatabase,
  LocalVisit,
  OutboxEntry,
  ReferenceOutlet,
  ReferencePlannedVisit,
} from "@/lib/sync/db";
import { plannedVisits } from "@/lib/sync/reference";

/**
 * A rep's day, assembled from the three stores that know about it (`JRN-05`, `OFF-01`) — W9 slice 5.
 *
 * <b>The join happens here rather than in the screen.</b> A stop is a planned call, the outlet it
 * names, and whatever this device has done about it — three stores, and a component that read all
 * three would re-derive the relationship on every render and have nowhere to put the rules below.
 */

/** What a rep has done about one planned call, on this device. */
export type StopProgress =
  /** Nothing yet. */
  | "todo"
  /** Checked in and still inside the shop. */
  | "working"
  /** Checked out. Whether the back office has it is the outbox's question, not this one's. */
  | "worked"
  /** The rep said why they could not make it (`JRN-06`, `VIS-07`). */
  | "notVisited";

/** One line of the day. */
export type Stop = {
  plannedVisitId: string;
  outletId: string;
  /**
   * The shop, or `undefined` when this device does not hold it.
   *
   * <b>A real state, not a bug.</b> A plan names a call at a shop that has since left the rep's
   * territory — the outlet arrives as a tombstone while the call stays, because
   * [the journey feed](../../../FieldKit.Modules.Sync/PullEndpoints.cs) scopes rounds by the rep the
   * *plan* names rather than by today's territory. Dropping the stop would hide exactly the call a
   * supervisor would ask about.
   */
  outlet: ReferenceOutlet | undefined;
  progress: StopProgress;
  notVisitedReason: string | null;
  /** The device's visit, when there is one — what a sync badge points at. */
  visit: LocalVisit | undefined;
  /**
   * This device has queued "could not make it" for this call and the server has not agreed yet
   * (`VIS-07`, W9 slice 9).
   *
   * Separate from `progress` on purpose: the *status* is the same either way — a rep who reported a
   * shop shut has dealt with the call — but whether the back office knows is a different question,
   * and it is the one that decides whether a sync badge belongs on the row.
   */
  reportedHere: boolean;
  /** The queued report was refused and re-sending will not change that (`OFF-09`). */
  reportFailed: boolean;
  /**
   * The day this device has queued a move to, or null (`JRN-06`, `BR-JRN-4`) — W12 F2b.
   *
   * <b>The stop stays on today's round while this is set</b>, because the device never rewrites
   * `ref_planned_visits` — moving the date locally would look right until the server refused the
   * mutation, and a refused annotation changes no row version, so the next pull would send nothing
   * to correct it.
   *
   * It is not part of `progress`, for the reason `reportedHere` is not: the call is still *to do*,
   * just not today, and a rep who is told otherwise would leave a shop uncalled if the move is
   * refused. What this changes is what the row **says**, not what it counts as.
   */
  movedTo: string | null;
};

/**
 * The rep's day, in the order they should work it.
 *
 * <b>Ordered by shop name, and that is the device's choice rather than a field the server dropped.</b>
 * A plan assigns calls to *days* (`JRN-03`); nothing in it sequences a day, because the order a rep
 * drives a round in is theirs — it depends on traffic, on which shop opens late, on where they slept.
 * Alphabetical is stable, matches what the rep reads, and does not pretend to be a route. When
 * routing arrives it will be a decision with a spec, not a `sort` quietly changing meaning.
 *
 * Stops whose outlet this device has never held sort last, by code, so a gap never displaces a shop
 * the rep can actually work.
 */
export async function today(db: FieldKitDatabase, date: string): Promise<Stop[]> {
  const planned = await plannedVisits(db, date);
  if (planned.length === 0) return [];

  const outletIds = [...new Set(planned.map((call) => call.outletId))];

  // `bulkGet` rather than a get per stop: one round trip through IndexedDB for a day's worth of
  // shops, and it returns `undefined` in place for the ones this device does not hold.
  const outlets = await db.outlets.bulkGet(outletIds);
  const byId = new Map<string, ReferenceOutlet | undefined>(
    outletIds.map((id, index) => [id, outlets[index]]),
  );

  // Every visit this device has worked at any of today's shops, read once. A rep works a handful of
  // shops a day, so this is a small query — and doing it per stop would be a query per row.
  const visits = await db.visits.where("outletId").anyOf(outletIds).toArray();

  /*
   * What this device has *said* about today's calls, which is not the same as what the round says
   * (W9 slice 9). A not-visited report queued offline lives in the outbox — writing it into
   * `ref_planned_visits` would be a lie the next pull could not correct, because a refused mutation
   * changes no row version and therefore sends nothing back.
   */
  const reported = await db.outbox
    .where("subjectId")
    .anyOf(planned.map((call) => call.id))
    .toArray();

  return planned
    .map((call) => stop(call, byId.get(call.outletId), visits, reported))
    .sort(compare);
}

function stop(
  call: ReferencePlannedVisit,
  outlet: ReferenceOutlet | undefined,
  visits: LocalVisit[],
  reported: OutboxEntry[],
): Stop {
  /*
   * The visit that belongs to this call, preferring the one that names it.
   *
   * `plannedVisitId` is set when the rep opened the stop from their journey, and absent when they
   * checked in at the shop directly — an unplanned call is ordinary (`JRN-06`). Matching on the
   * planned id first and falling back to the outlet means a rep who did both today sees the one
   * that answers *this* line, and a rep who did neither still sees their work against the shop.
   */
  const forCall = visits.filter(
    (candidate) => candidate.plannedVisitId === call.id || candidate.outletId === call.outletId,
  );

  const named = forCall.find((candidate) => candidate.plannedVisitId === call.id);
  const open = forCall.find((candidate) => candidate.status === "inProgress");

  // A visit still open outranks a finished one: what the rep needs from this row is what to do next,
  // and "you are in this shop" is the most actionable thing it can say.
  const visit = open ?? named ?? forCall.at(-1);

  // A row still in the outbox *is* the "still on its way" signal: an accepted mutation is deleted
  // rather than marked, so there is no sent state to filter out. Once the server has it, the round
  // carries it back on the next pull and `call.status` says the same thing.
  const report = reported.find(
    (entry) => entry.subjectId === call.id && entry.type === "NotVisitedCall",
  );

  // The same read, a different mutation type. Both are annotations this device has queued against
  // the call; neither is allowed to move the row they annotate.
  const moved = reported.find(
    (entry) => entry.subjectId === call.id && entry.type === "RescheduledCall",
  );

  return {
    plannedVisitId: call.id,
    outletId: call.outletId,
    outlet,
    progress: progressOf(call, visit, report !== undefined),
    // The server's copy wins once it has one — they carry the same sentence, and preferring the
    // round keeps a single source once the two agree.
    notVisitedReason: call.notVisitedReason ?? reasonOf(report),
    visit,
    reportedHere: report !== undefined,
    reportFailed: report?.status === "failed",
    movedTo: dateOf(moved),
  };
}

function reasonOf(entry: OutboxEntry | undefined): string | null {
  const payload = entry?.payload as { reason?: unknown } | undefined;

  return typeof payload?.reason === "string" ? payload.reason : null;
}

function dateOf(entry: OutboxEntry | undefined): string | null {
  const payload = entry?.payload as { date?: unknown } | undefined;

  return typeof payload?.date === "string" ? payload.date : null;
}

/**
 * <b>The device's own work outranks the plan's status.</b>
 *
 * The two can disagree honestly: a rep marks a call not-visited in the morning, the shop opens after
 * all, and they work it in the afternoon — the annotation is still on the plan until the next pull,
 * and the visit is the thing that happened. Showing "not visited" over a completed visit would tell
 * a rep their own work had been ignored.
 */
function progressOf(
  call: ReferencePlannedVisit,
  visit: LocalVisit | undefined,
  reportedHere: boolean,
): StopProgress {
  if (visit?.status === "inProgress") return "working";
  if (visit?.status === "checkedOut") return "worked";

  // The device's own report counts the moment it is written, not when the server hears about it —
  // which is the whole of `OFF-01`. A rep who marked a shop shut in a car park with no signal must
  // not see the call sitting as *to do* for the rest of the day.
  if (call.status === "NotVisited" || reportedHere) return "notVisited";

  return "todo";
}

function compare(left: Stop, right: Stop): number {
  // A shop this device does not hold has no name to sort by, and sorting it under an empty string
  // would float it to the top of the rep's day.
  if (!left.outlet !== !right.outlet) return left.outlet ? -1 : 1;

  if (left.outlet && right.outlet) {
    const byName = left.outlet.name.localeCompare(right.outlet.name);
    if (byName !== 0) return byName;

    // Two shops of the same name is the ordinary case a chain produces, and the code is what tells
    // them apart — the same reason it travels to the device at all.
    return left.outlet.code.localeCompare(right.outlet.code);
  }

  return left.outletId.localeCompare(right.outletId);
}

/**
 * Today, as the *device* reckons it (`yyyy-mm-dd`).
 *
 * <b>Local parts, never `toISOString()`.</b> That formats in UTC, so a rep in Bucharest opening the
 * app at half past midnight would be shown yesterday's round — and one in Auckland would get
 * tomorrow's. A planned call is dated to a business day, which starts at a different instant in
 * every place (`BR-PRD-6`), and the only clock that knows which day a rep is standing in is theirs.
 */
export function todayOn(now: Date): string {
  const month = `${now.getMonth() + 1}`.padStart(2, "0");
  const day = `${now.getDate()}`.padStart(2, "0");

  return `${now.getFullYear()}-${month}-${day}`;
}
