import "fake-indexeddb/auto";

import { afterEach, beforeEach, describe, expect, it } from "vitest";

import { closeDatabase, FieldKitDatabase, type ReferenceOutlet } from "@/lib/sync/db";
import { pending } from "@/lib/sync/outbox";
import { addUnplanned, callableOutlets, matching, queuedUnplanned } from "@/lib/visits/unplanned";

/**
 * A call the rep made that nobody planned (`JRN-06`, `BR-JRN-4`) — W11½ R4.
 *
 * The device half of an annotation every other layer already had (regression F7). The rules below
 * are the ones `JourneyIngestService.AddUnplannedAsync` enforces server-side, checked here as well
 * because a rep with no signal has to be told at the shop.
 */
let db: FieldKitDatabase;

const TODAY = "2028-03-06";

function shop(id: string, code: string, name: string): ReferenceOutlet {
  return {
    id,
    code,
    name,
    channelId: "channel-1",
    segment: null,
    status: "Active",
    countryCode: "RO",
    latitude: null,
    longitude: null,
    timeZoneId: "Europe/Bucharest",
    radiusMetres: 100,
    rowVersion: 1,
  };
}

beforeEach(() => {
  db = new FieldKitDatabase(`unplanned:${crypto.randomUUID()}`);
});

afterEach(async () => {
  await db.delete();
  closeDatabase();
});

describe("queuing an unplanned call", () => {
  it("queues it under the shop, with the rep's own business day", async () => {
    const result = await addUnplanned(db, "outlet-1", TODAY);

    expect(result.ok).toBe(true);

    const queued = await pending(db);

    expect(queued).toHaveLength(1);
    expect(queued[0]).toMatchObject({ type: "UnplannedCall", subjectId: "outlet-1" });
    expect(queued[0].payload).toEqual({ outletId: "outlet-1", date: TODAY });
  });

  it("refuses a second one for the same shop on the same day", async () => {
    // The one annotation that *creates* a row rather than changing one, so a duplicate would put the
    // same shop on the same day twice and overstate the rep's coverage. The server refuses it for
    // exactly this reason; the device refuses it so the rep hears at the shop, not at reconnect.
    await addUnplanned(db, "outlet-1", TODAY);

    const second = await addUnplanned(db, "outlet-1", TODAY);

    expect(second).toEqual({ ok: false, refusal: "journey.visit.alreadyReported" });
    expect(await pending(db)).toHaveLength(1);
  });

  it("allows the same shop on another day", async () => {
    // `subjectId` alone is the outlet, so a reader keyed on it would refuse a rep who calls at one
    // shop unplanned on Monday a second unplanned call there on Tuesday — an ordinary week.
    await addUnplanned(db, "outlet-1", TODAY);

    const tuesday = await addUnplanned(db, "outlet-1", "2028-03-07");

    expect(tuesday.ok).toBe(true);
    expect(await pending(db)).toHaveLength(2);
  });

  it("does not confuse itself with another shop's call", async () => {
    await addUnplanned(db, "outlet-1", TODAY);

    expect(await queuedUnplanned(db, "outlet-2", TODAY)).toBeUndefined();
  });

  it("reports a refused call as needing a person, so the rep is told", async () => {
    // `OFF-09`: the server said no — no published round covering the day, most likely — and
    // re-sending will not change its mind.
    const queued = await addUnplanned(db, "outlet-1", TODAY);

    if (!queued.ok) throw new Error("expected the call to queue");

    await db.outbox.update(queued.mutationId, { status: "failed" });

    expect(await queuedUnplanned(db, "outlet-1", TODAY)).toEqual({ failed: true });
  });

  it("ignores a not-visited report queued against the same subject", async () => {
    // Both annotations are keyed by a subject and both live in one outbox. A reader that filtered by
    // subject alone would take a not-visited report as an unplanned call and refuse the rep a shop.
    await db.outbox.add({
      mutationId: crypto.randomUUID(),
      type: "NotVisitedCall",
      subjectId: "outlet-1",
      payload: { plannedVisitId: "outlet-1", reason: "Shutters down" },
      status: "pending",
      createdAt: Date.now(),
      attempts: 0,
    });

    expect(await queuedUnplanned(db, "outlet-1", TODAY)).toBeUndefined();
  });
});

describe("the shops a rep can call at", () => {
  beforeEach(async () => {
    await db.outlets.bulkAdd([
      shop("outlet-1", "RO-0001", "Corner Shop"),
      shop("outlet-2", "RO-0002", "Kiosk 1 Mai"),
      shop("outlet-3", "RO-0003", "Mega Image Dorobanți"),
    ]);
  });

  it("leaves out the shops already on today's round", async () => {
    // The round already offers them, and a stop opened from this list would not carry the planned
    // call — so the supervisor's coverage figure would show it still outstanding.
    await db.plannedVisits.add({
      id: "call-1",
      outletId: "outlet-2",
      date: TODAY,
      status: "Planned",
      source: "Generated",
      notVisitedReason: null,
      rowVersion: 1,
      movableFrom: null,
      movableTo: null,
    });

    const callable = await callableOutlets(db, TODAY);

    expect(callable.map((outlet) => outlet.id)).toEqual(["outlet-1", "outlet-3"]);
  });

  it("offers a shop planned for another day", async () => {
    await db.plannedVisits.add({
      id: "call-1",
      outletId: "outlet-2",
      date: "2028-03-07",
      status: "Planned",
      source: "Generated",
      notVisitedReason: null,
      rowVersion: 1,
      movableFrom: null,
      movableTo: null,
    });

    const callable = await callableOutlets(db, TODAY);

    expect(callable.map((outlet) => outlet.id)).toContain("outlet-2");
  });

  it("offers every shop when there is no round at all", async () => {
    // The case that matters most: a rep whose plan has not arrived had no way into the field app.
    const callable = await callableOutlets(db, TODAY);

    expect(callable).toHaveLength(3);
  });
});

describe("finding a shop in the list", () => {
  const shops = [
    shop("outlet-1", "RO-0001", "Corner Shop"),
    shop("outlet-2", "RO-0002", "Kiosk 1 Mai"),
    shop("outlet-3", "RO-0003", "Mega Image Dorobanți"),
  ];

  it("matches on the code, which is what tells two shops of a chain apart", () => {
    expect(matching(shops, "RO-0003").map((outlet) => outlet.name)).toEqual([
      "Mega Image Dorobanți",
    ]);
  });

  it("matches on part of the name, ignoring case", () => {
    expect(matching(shops, "kiosk").map((outlet) => outlet.id)).toEqual(["outlet-2"]);
  });

  it("returns everything for an empty search rather than nothing", () => {
    expect(matching(shops, "   ")).toHaveLength(3);
  });
});
