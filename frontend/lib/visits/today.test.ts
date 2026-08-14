import "fake-indexeddb/auto";

import { afterEach, beforeEach, describe, expect, it } from "vitest";

import {
  closeDatabase,
  FieldKitDatabase,
  type LocalVisit,
  type ReferenceOutlet,
  type ReferencePlannedVisit,
} from "@/lib/sync/db";
import { markNotVisited } from "@/lib/visits/not-visited";
import { today, todayOn } from "@/lib/visits/today";

/**
 * A rep's day, assembled from three stores (`JRN-05`, `OFF-01`) — W9 slice 5.
 *
 * What is worth testing here is the *joining*, not the querying: which visit answers which call,
 * what a stop says when the two disagree, and what happens to a call whose shop this device does not
 * hold. Each of those is a rule somebody could reasonably have written the other way.
 */
const TODAY = "2026-03-17";

function outlet(id: string, name: string, code: string): ReferenceOutlet {
  return {
    id,
    code,
    name,
    channelId: "channel-1",
    segment: "A",
    status: "Active",
    latitude: 44.4638,
    longitude: 26.0946,
    countryCode: "RO",
    timeZoneId: "Europe/Bucharest",
    radiusMetres: 150,
    rowVersion: 4,
  };
}

function call(
  id: string,
  outletId: string,
  overrides: Partial<ReferencePlannedVisit> = {},
): ReferencePlannedVisit {
  return {
    id,
    outletId,
    date: TODAY,
    status: "Planned",
    source: "Generated",
    notVisitedReason: null,
    rowVersion: 7,
    movableFrom: null,
    movableTo: null,
    ...overrides,
  };
}

function visit(id: string, outletId: string, overrides: Partial<LocalVisit> = {}): LocalVisit {
  return {
    id,
    outletId,
    plannedVisitId: null,
    status: "checkedOut",
    checkedInAtUtc: "2026-03-17T09:00:00.000Z",
    checkInLatitude: null,
    checkInLongitude: null,
    checkInDistanceMetres: null,
    wasInsideGeofence: true,
    overrideReason: null,
    steps: [],
    checkedOutAtUtc: "2026-03-17T09:25:00.000Z",
    checkOutLatitude: null,
    checkOutLongitude: null,
    outcome: "Productive",
    outcomeReason: null,
    ...overrides,
  };
}

let db: FieldKitDatabase;

beforeEach(() => {
  db = new FieldKitDatabase(`today:${crypto.randomUUID()}`);
});

afterEach(async () => {
  await db.delete();
  closeDatabase();
});

describe("the day", () => {
  it("is empty when nothing is planned, and says nothing else", async () => {
    expect(await today(db, TODAY)).toEqual([]);
  });

  it("carries only the calls dated today", async () => {
    await db.outlets.add(outlet("outlet-1", "Mega Image", "RO-001"));
    await db.plannedVisits.bulkAdd([
      call("call-1", "outlet-1"),
      call("call-2", "outlet-1", { date: "2026-03-18" }),
    ]);

    expect((await today(db, TODAY)).map((stop) => stop.plannedVisitId)).toEqual(["call-1"]);
  });

  it("orders by shop name, then by code when a chain repeats one", async () => {
    // The device's choice, not a field the server dropped: a plan assigns calls to *days*, and
    // nothing in it sequences a day. Two shops of one name is the ordinary case a chain produces.
    await db.outlets.bulkAdd([
      outlet("outlet-1", "Profi Titan", "RO-003"),
      outlet("outlet-2", "Mega Image", "RO-009"),
      outlet("outlet-3", "Mega Image", "RO-002"),
    ]);
    await db.plannedVisits.bulkAdd([
      call("call-1", "outlet-1"),
      call("call-2", "outlet-2"),
      call("call-3", "outlet-3"),
    ]);

    expect((await today(db, TODAY)).map((stop) => stop.outlet?.code)).toEqual([
      "RO-002",
      "RO-009",
      "RO-003",
    ]);
  });

  it("keeps a call whose shop this device does not hold, and sorts it last", async () => {
    // The journey feed scopes a round by the rep the *plan* names, not by today's territory — so a
    // call at a shop that has since moved arrives with no outlet. Dropping it would hide exactly the
    // call a supervisor would ask about.
    await db.outlets.add(outlet("outlet-1", "Mega Image", "RO-001"));
    await db.plannedVisits.bulkAdd([call("call-1", "gone"), call("call-2", "outlet-1")]);

    const stops = await today(db, TODAY);

    expect(stops.map((stop) => stop.plannedVisitId)).toEqual(["call-2", "call-1"]);
    expect(stops[1].outlet).toBeUndefined();
    expect(stops[1].outletId).toBe("gone");
  });
});

describe("what a stop says the rep has done", () => {
  beforeEach(async () => {
    await db.outlets.add(outlet("outlet-1", "Mega Image", "RO-001"));
  });

  it("is 'todo' with nothing against it", async () => {
    await db.plannedVisits.add(call("call-1", "outlet-1"));

    expect((await today(db, TODAY))[0].progress).toBe("todo");
  });

  it("is 'working' while the rep is in the shop", async () => {
    await db.plannedVisits.add(call("call-1", "outlet-1"));
    await db.visits.add(visit("visit-1", "outlet-1", { status: "inProgress" }));

    const stop = (await today(db, TODAY))[0];

    expect(stop.progress).toBe("working");
    expect(stop.visit?.id).toBe("visit-1");
  });

  it("is 'notVisited' with the rep's own sentence", async () => {
    await db.plannedVisits.add(
      call("call-1", "outlet-1", { status: "NotVisited", notVisitedReason: "Closed for works" }),
    );

    const stop = (await today(db, TODAY))[0];

    expect(stop.progress).toBe("notVisited");
    expect(stop.notVisitedReason).toBe("Closed for works");
  });

  it("lets the device's own work outrank a stale annotation", async () => {
    // The two can disagree honestly: marked not-visited in the morning, the shop opens after all,
    // and the rep works it — the annotation is still on the plan until the next pull. Showing "not
    // visited" over a completed visit would tell a rep their own work had been ignored.
    await db.plannedVisits.add(
      call("call-1", "outlet-1", { status: "NotVisited", notVisitedReason: "Closed for works" }),
    );
    await db.visits.add(visit("visit-1", "outlet-1"));

    expect((await today(db, TODAY))[0].progress).toBe("worked");
  });

  it("prefers the visit that names the call over one merely at the shop", async () => {
    await db.plannedVisits.add(call("call-1", "outlet-1"));
    await db.visits.bulkAdd([
      visit("unplanned", "outlet-1"),
      visit("planned", "outlet-1", { plannedVisitId: "call-1" }),
    ]);

    expect((await today(db, TODAY))[0].visit?.id).toBe("planned");
  });

  it("prefers an open visit over a finished one, whichever names the call", async () => {
    // What the row is for is telling a rep what to do next, and "you are in this shop" is the most
    // actionable thing it can say.
    await db.plannedVisits.add(call("call-1", "outlet-1"));
    await db.visits.bulkAdd([
      visit("finished", "outlet-1", { plannedVisitId: "call-1" }),
      visit("open", "outlet-1", { status: "inProgress" }),
    ]);

    const stop = (await today(db, TODAY))[0];

    expect(stop.visit?.id).toBe("open");
    expect(stop.progress).toBe("working");
  });

  it("ignores a visit at another shop entirely", async () => {
    await db.outlets.add(outlet("outlet-2", "Profi", "RO-002"));
    await db.plannedVisits.add(call("call-1", "outlet-1"));
    await db.visits.add(visit("elsewhere", "outlet-2"));

    const stop = (await today(db, TODAY))[0];

    expect(stop.visit).toBeUndefined();
    expect(stop.progress).toBe("todo");
  });
});

describe("which day it is", () => {
  it("is the device's own date, not UTC's", () => {
    // `toISOString().slice(0, 10)` would answer for a rep in Bucharest at half past midnight with
    // *yesterday*, and for one in Auckland with tomorrow. A planned call is dated to a business day,
    // which starts at a different instant in every place.
    const justAfterMidnightLocal = new Date(2026, 2, 17, 0, 30);

    expect(todayOn(justAfterMidnightLocal)).toBe("2026-03-17");
  });

  it("pads a single-digit month and day", () => {
    expect(todayOn(new Date(2026, 0, 5, 12))).toBe("2026-01-05");
  });
});

describe("a call this device has reported but the server has not heard about", () => {
  beforeEach(async () => {
    await db.outlets.add(outlet("outlet-1", "Mega Image", "RO-001"));
  });

  it("reads as not visited the moment it is queued, not when it is sent", async () => {
    // The whole of `OFF-01` for an annotation: a rep who marked a shop shut in a car park with no
    // signal must not see the call sitting as *to do* for the rest of the day.
    await db.plannedVisits.add(call("call-1", "outlet-1"));
    await markNotVisited(db, "call-1", "Shutters down at nine");

    const stop = (await today(db, TODAY))[0];

    expect(stop.progress).toBe("notVisited");
    expect(stop.notVisitedReason).toBe("Shutters down at nine");
    expect(stop.reportedHere).toBe(true);
  });

  it("prefers the round's own copy once the server has agreed", async () => {
    // The two carry the same sentence, and preferring the round keeps one source of truth once the
    // pull has brought the annotation back.
    await db.plannedVisits.add(
      call("call-1", "outlet-1", { status: "NotVisited", notVisitedReason: "Closed for works" }),
    );
    await markNotVisited(db, "call-1", "Shutters down at nine");

    expect((await today(db, TODAY))[0].notVisitedReason).toBe("Closed for works");
  });

  it("flags a report the server refused, because that one needs a person", async () => {
    await db.plannedVisits.add(call("call-1", "outlet-1"));
    await markNotVisited(db, "call-1", "Shutters down at nine");

    const entry = (await db.outbox.toArray())[0];
    await db.outbox.update(entry.mutationId, { status: "failed" });

    const stop = (await today(db, TODAY))[0];

    expect(stop.reportFailed).toBe(true);
    expect(stop.progress).toBe("notVisited");
  });

  it("lets a visit worked afterwards outrank the rep's own earlier report", async () => {
    // The same rule the plan's annotation already loses to: a rep who reported a shop shut and then
    // got in has done the work, and showing "not visited" over it would ignore them twice.
    await db.plannedVisits.add(call("call-1", "outlet-1"));
    await markNotVisited(db, "call-1", "Shutters down at nine");
    await db.visits.add(visit("visit-1", "outlet-1"));

    expect((await today(db, TODAY))[0].progress).toBe("worked");
  });

  it("does not read a queued visit as a report", async () => {
    await db.plannedVisits.add(call("call-1", "outlet-1"));
    await db.outbox.add({
      mutationId: crypto.randomUUID(),
      type: "CapturedVisit",
      subjectId: "call-1",
      payload: {},
      status: "pending",
      createdAt: Date.now(),
      attempts: 0,
    });

    const stop = (await today(db, TODAY))[0];

    expect(stop.reportedHere).toBe(false);
    expect(stop.progress).toBe("todo");
  });
});
