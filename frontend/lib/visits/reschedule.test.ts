import "fake-indexeddb/auto";

import { afterEach, beforeEach, describe, expect, it } from "vitest";

import { closeDatabase, FieldKitDatabase, type ReferencePlannedVisit } from "@/lib/sync/db";
import { markRejected, pending } from "@/lib/sync/outbox";
import { movable, queuedReschedule, reschedule } from "@/lib/visits/reschedule";

/**
 * Moving a call to another day inside its cycle (`JRN-06`, `BR-JRN-4`) — W12 F2b.
 *
 * The **third and last** of `JRN-06`'s rep-side annotations to get a device writer, and the one that
 * needed a contract change first: `BR-JRN-4`'s window could not be evaluated from anything the round
 * carried (regression F2). F2a sends it; nothing here re-derives it.
 */
let db: FieldKitDatabase;

const MONDAY = "2026-04-06";
const SUNDAY = "2026-04-12";

function call(overrides: Partial<ReferencePlannedVisit> = {}): ReferencePlannedVisit {
  return {
    id: "call-1",
    outletId: "outlet-1",
    date: MONDAY,
    status: "Planned",
    source: "Generated",
    notVisitedReason: null,
    rowVersion: 7,
    movableFrom: MONDAY,
    movableTo: SUNDAY,
    ...overrides,
  };
}

beforeEach(() => {
  db = new FieldKitDatabase(`reschedule:${crypto.randomUUID()}`);
});

afterEach(async () => {
  await db.delete();
  closeDatabase();
});

describe("moving a call to another day", () => {
  it("queues it under the call it annotates, so the badge on that row can find it", async () => {
    const result = await reschedule(db, call(), "2026-04-08");

    expect(result.ok).toBe(true);

    const queued = await pending(db);

    expect(queued).toHaveLength(1);
    expect(queued[0]).toMatchObject({ type: "RescheduledCall", subjectId: "call-1" });

    // The shape `RescheduledCall(PlannedVisitId, Date)` binds to on the server. `subjectId` is the
    // outbox's own key and the payload is what crosses the wire; they agree here and are not the
    // same field, which is what `slotOf` in the sync manager exists to keep straight.
    expect(queued[0].payload).toEqual({ plannedVisitId: "call-1", date: "2026-04-08" });
  });

  it("writes nothing into the round, because a refusal could never correct it", async () => {
    /*
     * The rule every rep-side annotation follows, stated once more because it is the one a reader
     * will be tempted to break: moving `date` on the held row would look right until the server
     * refused the mutation — and a refused annotation changes no row version, so the next delta
     * sends nothing back. The row would be wrong for the life of the install.
     */
    await db.plannedVisits.add(call());

    await reschedule(db, call(), "2026-04-08");

    const held = await db.plannedVisits.get("call-1");

    expect(held?.date).toBe(MONDAY);
    expect(held?.movableFrom).toBe(MONDAY);
  });

  it("refuses a day outside the window it was sent, because offline nobody else will", async () => {
    // The Monday after — inside the plan, outside the cycle, and the exact case `BR-JRN-4` reserves
    // for a supervisor. `<input type="date">` narrows the picker to the same range; this is the
    // guard for a rep who types, which some browsers still allow.
    expect(await reschedule(db, call(), "2026-04-13")).toEqual({
      ok: false,
      refusal: "journey.visit.outsideCycle",
    });

    // And the day before it opens, which a `min` alone would also let through by typing.
    expect(await reschedule(db, call(), "2026-04-05")).toEqual({
      ok: false,
      refusal: "journey.visit.outsideCycle",
    });

    expect(await pending(db)).toEqual([]);
  });

  it("offers both ends of the window, since a range that excluded them would be a different rule", async () => {
    // The boundaries are the days an off-by-one costs, and the only ones a reader can get wrong
    // without the tests above noticing.
    expect((await reschedule(db, call(), MONDAY)).ok).toBe(true);
    expect((await reschedule(db, call({ id: "call-2" }), SUNDAY)).ok).toBe(true);
  });

  it("refuses a call with no window at all, which is every unplanned one", async () => {
    /*
     * An unplanned call belongs to no cycle — `BR-JRN-4` is about moving a call within the cycle its
     * *frequency* put it in — so the server sends null and refuses any move. The screen renders no
     * button for it; this is the writer refusing to be called anyway, because a null window and a
     * window of zero days are not the same thing and only one of them is a bug.
     */
    const unplanned = call({ source: "Unplanned", movableFrom: null, movableTo: null });

    expect(await reschedule(db, unplanned, "2026-04-08")).toEqual({
      ok: false,
      refusal: "journey.visit.outsideCycle",
    });

    expect(movable(unplanned)).toBeUndefined();
    expect(await pending(db)).toEqual([]);
  });

  it("refuses a second move, because the rep would be moving from a day they never saw", async () => {
    /*
     * <b>The server would accept this one.</b> A second reschedule moves the call again, from
     * wherever the first put it — and that is the problem rather than the reason it is safe: the two
     * are pushed in order, and the rep chose the second day against the window of the *original*
     * date. Refusing here keeps the offer honest.
     */
    await reschedule(db, call(), "2026-04-08");

    expect(await reschedule(db, call(), "2026-04-09")).toEqual({
      ok: false,
      refusal: "journey.visit.alreadyReported",
    });

    expect(await pending(db)).toHaveLength(1);
  });
});

describe("what this device has already said about a call", () => {
  it("answers nothing for a call it has not moved", async () => {
    await reschedule(db, call(), "2026-04-08");

    expect(await queuedReschedule(db, "call-2")).toBeUndefined();
  });

  it("does not mistake a not-visited report for a move", async () => {
    // Both annotations are queued under the call's id, so the *type* is the only thing separating
    // them — and a reader that filtered on `subjectId` alone would show a rep a move they never made.
    await db.outbox.add({
      mutationId: crypto.randomUUID(),
      type: "NotVisitedCall",
      subjectId: "call-1",
      payload: { plannedVisitId: "call-1", reason: "Closed" },
      status: "pending",
      attempts: 0,
      createdAt: Date.now(),
      errorCode: "",
      errorDetail: "",
    });

    expect(await queuedReschedule(db, "call-1")).toBeUndefined();
  });

  it("says when the server refused it, because that is the one case a rep must act on", async () => {
    const result = await reschedule(db, call(), "2026-04-08");

    expect(result.ok).toBe(true);
    if (!result.ok) return;

    await markRejected(db, result.mutationId, "journey.visit.unknown", "No such call.");

    expect(await queuedReschedule(db, "call-1")).toEqual({
      date: "2026-04-08",
      failed: true,
    });
  });
});
