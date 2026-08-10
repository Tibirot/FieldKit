import "fake-indexeddb/auto";

import Dexie from "dexie";
import { afterEach, describe, expect, it } from "vitest";

import { closeDatabase, FieldKitDatabase } from "./db";
import { enqueue, markRejected, pending, pendingCount, statusOf } from "./outbox";
import { applyOutletChanges, OUTLETS, outlets, watermark } from "./reference";

/**
 * An app update must not strand a pending outbox (`OFF-13`, W8 slice 11).
 *
 * <b>The requirement is about the one store the device cannot re-fetch.</b> Every `ref_*` table is a
 * copy of something the server still has: lose it and the next sync rebuilds it. The outbox is the
 * opposite — it is the only record that a rep did the work, and a migration that drops it turns "no
 * lost work, ever" into a slogan.
 *
 * So these tests do the thing an app update does: write a **version 1** database, then open it with
 * the code that knows about version 2, and check that the work is still there and still sendable.
 */

/**
 * The schema exactly as W8 slice 6 shipped it.
 *
 * Duplicated here rather than imported, deliberately. The point of the test is that *yesterday's*
 * database survives today's code, and a shared constant would drift forward with the schema —
 * leaving a test that opens v2, upgrades v2 to v2, and proves nothing. This copy is a fixture of a
 * past release, and it should only ever be edited to correct what v1 actually was.
 */
const VERSION_1_STORES = {
  ref_outlets: "id, name, channelId",
  ref_planned_visits: "id, date, outletId",
  ref_visit_workflows: "id, &channelId",
  ref_products: "id, sku, name, status",
  ref_assortment: "id, channelId",
  ref_assortment_overrides: "id, outletId",
  ref_price_lists: "id",
  ref_price_lines: "id, [priceListId+productId]",
  ref_price_assignments: "id, outletId, channelId",
  ref_promotions: "id",
  ref_promotion_assignments: "id, outletId, channelId",
  outbox: "mutationId, status, createdAt, subjectId",
  meta: "key",
  watermarks: "entity",
};

/** Opens a database at version 1 only — the app as it was before this slice. */
async function openVersionOne(name: string): Promise<Dexie> {
  const legacy = new Dexie(name);
  legacy.version(1).stores(VERSION_1_STORES);
  await legacy.open();

  return legacy;
}

function outletRow(id: string, rowVersion: number) {
  return {
    id,
    name: `Shop ${id}`,
    channelId: "11111111-1111-4111-8111-111111111111",
    segment: null,
    status: "Active",
    latitude: null,
    longitude: null,
    rowVersion,
  };
}

afterEach(() => {
  closeDatabase();
});

describe("upgrading a device that has unsent work", () => {
  it("keeps the outbox, its order, and everything about each entry", async () => {
    const name = `migration:${crypto.randomUUID()}`;

    // Yesterday's app. Three mutations captured offline, one of them already refused.
    const legacy = await openVersionOne(name);
    await legacy.table("outbox").bulkAdd([
      {
        mutationId: "m-1",
        type: "CapturedVisit",
        subjectId: "visit-1",
        payload: { visitId: "visit-1" },
        status: "pending",
        createdAt: 1_000,
        attempts: 2,
      },
      {
        mutationId: "m-2",
        type: "CapturedVisit",
        subjectId: "visit-2",
        payload: { visitId: "visit-2" },
        status: "failed",
        createdAt: 2_000,
        attempts: 1,
        errorCode: "visit.ingest.outletUnknown",
        errorDetail: "No such outlet.",
      },
      {
        mutationId: "m-3",
        type: "CapturedVisit",
        subjectId: "visit-3",
        payload: { visitId: "visit-3" },
        status: "pending",
        createdAt: 3_000,
        attempts: 0,
      },
    ]);
    legacy.close();

    // Today's app opens the same database.
    const upgraded = new FieldKitDatabase(name);
    await upgraded.open();

    // It really upgraded rather than finding a fresh one: version 2, holding version 1's rows.
    // Without this the whole file could be passing against databases that never existed at v1.
    expect(upgraded.verno).toBe(2);
    expect(await upgraded.outbox.count()).toBe(3);

    // Still in capture order, which is what the drain depends on — and now read through the new
    // compound index rather than sorted in memory, so this is also the index's first real exercise.
    expect((await pending(upgraded)).map((entry) => entry.mutationId)).toEqual(["m-1", "m-3"]);

    // The failed one keeps its reason. A migration that rebuilt rows rather than reindexing them
    // would plausibly lose the optional fields, and the rep would be told a visit failed with no
    // way to know why.
    expect(await upgraded.outbox.get("m-2")).toMatchObject({
      status: "failed",
      attempts: 1,
      errorCode: "visit.ingest.outletUnknown",
      errorDetail: "No such outlet.",
    });

    // And the payload — the actual work — survives byte for byte.
    expect((await upgraded.outbox.get("m-1"))?.payload).toEqual({ visitId: "visit-1" });
    expect(await pendingCount(upgraded)).toBe(2);
    expect(await statusOf(upgraded, "visit-2")).toBe("failed");

    upgraded.close();
  });

  it("keeps the reference data and the watermarks, so the device does not re-snapshot", async () => {
    // Losing these is survivable — the next pull would rebuild them — but only by re-downloading a
    // tenant's catalogue over a connection the rep may not have. A migration that silently reset
    // every watermark would look like a slow morning rather than a bug.
    const name = `migration:${crypto.randomUUID()}`;

    const legacy = await openVersionOne(name);
    await legacy.table("ref_outlets").bulkAdd([outletRow("outlet-1", 4), outletRow("outlet-2", 7)]);
    await legacy.table("watermarks").put({ entity: "outlets", cursor: 7 });
    await legacy.table("meta").put({ key: "deviceId", value: "device-1" });
    legacy.close();

    const upgraded = new FieldKitDatabase(name);

    expect((await outlets(upgraded)).map((row) => row.id)).toEqual(["outlet-1", "outlet-2"]);
    expect(await watermark(upgraded, OUTLETS)).toBe(7);
    expect(await upgraded.meta.get("deviceId")).toEqual({ key: "deviceId", value: "device-1" });

    upgraded.close();
  });

  it("can still enqueue and drain after the upgrade", async () => {
    // The migration is only worth anything if the store works afterwards. This is the half a
    // "preserved the rows" assertion cannot reach: the new index has to be usable, not just present.
    const name = `migration:${crypto.randomUUID()}`;

    const legacy = await openVersionOne(name);
    await legacy.table("outbox").add({
      mutationId: "m-old",
      type: "CapturedVisit",
      subjectId: "visit-old",
      payload: {},
      status: "pending",
      createdAt: 1_000,
      attempts: 0,
    });
    legacy.close();

    const upgraded = new FieldKitDatabase(name);

    await enqueue(upgraded, { type: "CapturedVisit", subjectId: "visit-new", payload: {} });

    // Old work first: the index orders by capture, and a rep's day reaches the back office in the
    // order it happened whichever side of the upgrade it was captured on.
    const queued = await pending(upgraded);

    expect(queued).toHaveLength(2);
    expect(queued[0].mutationId).toBe("m-old");

    // And the limit is honoured off the index rather than by slicing a fully-materialised list.
    expect((await pending(upgraded, 1)).map((entry) => entry.mutationId)).toEqual(["m-old"]);

    upgraded.close();
  });

  it("upgrades an empty database without inventing anything", async () => {
    // A device that installed the app and never synced. Cheap to get wrong in the other direction —
    // an `upgrade()` callback that assumed rows exist would throw here and brick the app on launch.
    const name = `migration:${crypto.randomUUID()}`;

    const legacy = await openVersionOne(name);
    legacy.close();

    const upgraded = new FieldKitDatabase(name);

    expect(await upgraded.outbox.count()).toBe(0);
    expect(await pending(upgraded)).toEqual([]);
    expect(await watermark(upgraded, OUTLETS)).toBe(0);

    upgraded.close();
  });

  it("preserves work captured before the upgrade even when a later pull arrives", async () => {
    // The end-to-end shape of OFF-13: yesterday's unsent visit is still queued after the app
    // updated *and* synced. Reference data moving is what a device does every day; the outbox
    // surviving that is the guarantee.
    const name = `migration:${crypto.randomUUID()}`;

    const legacy = await openVersionOne(name);
    await legacy.table("outbox").add({
      mutationId: "m-1",
      type: "CapturedVisit",
      subjectId: "visit-1",
      payload: { visitId: "visit-1" },
      status: "pending",
      createdAt: 1_000,
      attempts: 0,
    });
    legacy.close();

    const upgraded = new FieldKitDatabase(name);

    await applyOutletChanges(
      upgraded,
      { upserts: [outletRow("outlet-1", 9)], tombstones: [], cursor: 9 },
      "outlets#9",
    );

    expect(await watermark(upgraded, OUTLETS)).toBe(9);
    expect((await pending(upgraded)).map((entry) => entry.mutationId)).toEqual(["m-1"]);

    upgraded.close();
  });

  it("marks a pre-upgrade mutation rejected without losing it", async () => {
    // A mutation captured on v1 and refused on v2 has to end up in the state a rep can act on,
    // through the same code path as one captured today.
    const name = `migration:${crypto.randomUUID()}`;

    const legacy = await openVersionOne(name);
    await legacy.table("outbox").add({
      mutationId: "m-1",
      type: "CapturedVisit",
      subjectId: "visit-1",
      payload: {},
      status: "pending",
      createdAt: 1_000,
      attempts: 0,
    });
    legacy.close();

    const upgraded = new FieldKitDatabase(name);

    await markRejected(upgraded, "m-1", "visit.ingest.outletUnknown", "No such outlet.");

    expect(await pending(upgraded)).toEqual([]);
    expect(await upgraded.outbox.get("m-1")).toMatchObject({
      status: "failed",
      errorCode: "visit.ingest.outletUnknown",
    });

    upgraded.close();
  });
});

describe("the schema itself", () => {
  it("declares every version, so a device that skipped one still arrives", async () => {
    // Dexie replays versions in order to bring an existing database forward. Deleting version 1
    // once nothing installs it fresh would not simplify the file — it would strand every device
    // that has not opened the app since, which on a field app is the ones that most need to sync.
    const name = `migration:${crypto.randomUUID()}`;
    const db = new FieldKitDatabase(name);

    await db.open();

    expect(db.verno).toBe(2);

    // The outbox is still keyed by the mutation id, which is the property the server's ledger
    // depends on: a re-send has to arrive under the id it was captured with.
    expect(db.outbox.schema.primKey.keyPath).toBe("mutationId");

    db.close();
  });
});
