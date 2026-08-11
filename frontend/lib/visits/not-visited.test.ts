import "fake-indexeddb/auto";

import { afterEach, beforeEach, describe, expect, it } from "vitest";

import { closeDatabase, FieldKitDatabase } from "@/lib/sync/db";
import { pending } from "@/lib/sync/outbox";
import { markNotVisited, queuedNotVisited } from "@/lib/visits/not-visited";

/**
 * A call the rep could not make, reported with no signal (`VIS-07`, `OFF-04`) — W9 slice 9.
 *
 * The **second** mutation type this outbox has ever carried, which is the whole point of the slice:
 * up to now `type` was a field with one legal value.
 */
let db: FieldKitDatabase;

beforeEach(() => {
  db = new FieldKitDatabase(`notvisited:${crypto.randomUUID()}`);
});

afterEach(async () => {
  await db.delete();
  closeDatabase();
});

describe("reporting a call as not visited", () => {
  it("queues it under the call it annotates, so the badge on that row can find it", async () => {
    const result = await markNotVisited(db, "call-1", "Closed for refurbishment");

    expect(result.ok).toBe(true);

    const queued = await pending(db);

    expect(queued).toHaveLength(1);
    expect(queued[0]).toMatchObject({ type: "NotVisitedCall", subjectId: "call-1" });
    expect(queued[0].payload).toEqual({
      plannedVisitId: "call-1",
      reason: "Closed for refurbishment",
    });
  });

  it("refuses a report with nothing written, because offline nobody else will", async () => {
    // `BR-JRN-2` on the device. A rep who typed nothing has to be told at the shop; being told at
    // reconnect is being told too late, and it is the same argument the geofence makes.
    expect(await markNotVisited(db, "call-1", "   ")).toEqual({
      ok: false,
      refusal: "journey.visit.reasonRequired",
    });

    expect(await pending(db)).toEqual([]);
  });

  it("trims what the rep typed rather than storing their whitespace", async () => {
    await markNotVisited(db, "call-1", "  Shutters down, nobody there  ");

    expect((await queuedNotVisited(db, "call-1"))?.reason).toBe("Shutters down, nobody there");
  });

  it("will not queue a second report for one call", async () => {
    // The server would answer the second `alreadyNotVisited` — accepted, harmlessly — but the rep
    // would have watched themselves change a reason the server is never going to replace.
    await markNotVisited(db, "call-1", "Closed on arrival");

    expect(await markNotVisited(db, "call-1", "Actually, refurbishment")).toEqual({
      ok: false,
      refusal: "journey.visit.alreadyReported",
    });

    expect(await pending(db)).toHaveLength(1);
    expect((await queuedNotVisited(db, "call-1"))?.reason).toBe("Closed on arrival");
  });

  it("keeps reports for different calls apart", async () => {
    await markNotVisited(db, "call-1", "Closed on arrival");
    await markNotVisited(db, "call-2", "Roadworks, could not get near it");

    expect((await queuedNotVisited(db, "call-1"))?.reason).toBe("Closed on arrival");
    expect((await queuedNotVisited(db, "call-2"))?.reason).toBe("Roadworks, could not get near it");
    expect(await queuedNotVisited(db, "call-3")).toBeUndefined();
  });

  it("says when the server refused it, because that is the one case a rep must act on", async () => {
    await markNotVisited(db, "call-1", "Closed on arrival");

    const entry = (await pending(db))[0];
    await db.outbox.update(entry.mutationId, { status: "failed" });

    expect(await queuedNotVisited(db, "call-1")).toEqual({
      reason: "Closed on arrival",
      failed: true,
    });
  });

  it("ignores a visit queued against the same shop", async () => {
    // Both live in one outbox and both are keyed by a subject id. A reader that matched on the
    // subject alone would show a checked-out visit as a not-visited report.
    await db.outbox.add({
      mutationId: crypto.randomUUID(),
      type: "CapturedVisit",
      subjectId: "call-1",
      payload: {},
      status: "pending",
      createdAt: Date.now(),
      attempts: 0,
    });

    expect(await queuedNotVisited(db, "call-1")).toBeUndefined();
  });
});
