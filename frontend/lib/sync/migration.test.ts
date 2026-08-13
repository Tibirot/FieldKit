import "fake-indexeddb/auto";

import Dexie from "dexie";
import { afterEach, describe, expect, it } from "vitest";

import { draftFor as auditDraftFor } from "@/lib/audits/local-audit";
import { draftFor } from "@/lib/orders/local-order";
import { closeDatabase, FieldKitDatabase, WAITING } from "./db";
import { enqueue, markRejected, pending, pendingCount, statusOf } from "./outbox";
import {
  applyOrderMinimumChanges,
  applyOutletChanges,
  applyTaxRateChanges,
  OUTLETS,
  outlets,
  PRICE_LINES,
  PRODUCTS,
  PROMOTIONS,
  SCORE_WEIGHTS,
  SURVEYS,
  TAX_RATES,
  taxRatesFor,
  watermark,
} from "./reference";

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

/** Opens a database at version 1 only — the app as it was before the outbox index. */
async function openVersionOne(name: string): Promise<Dexie> {
  const legacy = new Dexie(name);
  legacy.version(1).stores(VERSION_1_STORES);
  await legacy.open();

  return legacy;
}

/**
 * Opens a database at version 2 — the app as W8 shipped it, before the outlet code.
 *
 * A second fixture rather than a parameter on the one above, for the reason that comment gives: each
 * is a snapshot of a release, and a helper that walked forward with the schema would let a test open
 * v3 and upgrade v3 to v3.
 */
async function openVersionTwo(name: string): Promise<Dexie> {
  const legacy = new Dexie(name);
  legacy.version(1).stores(VERSION_1_STORES);
  legacy.version(2).stores({
    outbox: "mutationId, status, createdAt, subjectId, [status+createdAt]",
  });
  await legacy.open();

  return legacy;
}

/**
 * Opens a database at version 3 — after the outlet code, before the geofence radius.
 *
 * The third snapshot of a release, for the same reason as the second. This one is what makes the
 * next test mean something: a device already on 3 has run version 3's upgrade, so Dexie will not run
 * it again, and only version 4 can re-baseline it.
 */
async function openVersionThree(name: string): Promise<Dexie> {
  const legacy = await openVersionTwo(name);
  legacy.close();

  const third = new Dexie(name);
  third.version(1).stores(VERSION_1_STORES);
  third.version(2).stores({
    outbox: "mutationId, status, createdAt, subjectId, [status+createdAt]",
  });
  third.version(3).stores({}).upgrade((tx) => tx.table("watermarks").delete("outlets"));
  await third.open();

  return third;
}

/**
 * Opens a database at version 4 — everything the reference data needs, and no visits store.
 *
 * The fourth snapshot of a release. Versions 3 and 4 are declared here identically to the real
 * schema on purpose: a helper that shortcut them would let this test open a database that never
 * existed, and the property under test is that a *real* device carries its outbox across.
 */
async function openVersionFour(name: string): Promise<Dexie> {
  const legacy = await openVersionThree(name);
  legacy.close();

  const fourth = new Dexie(name);
  fourth.version(1).stores(VERSION_1_STORES);
  fourth.version(2).stores({
    outbox: "mutationId, status, createdAt, subjectId, [status+createdAt]",
  });
  fourth.version(3).stores({}).upgrade((tx) => tx.table("watermarks").delete("outlets"));
  fourth.version(4).stores({}).upgrade((tx) => tx.table("watermarks").delete("outlets"));
  await fourth.open();

  return fourth;
}

/**
 * The schema as W9 slice 4 shipped it — the last version before the audit's reference stores.
 *
 * Built by replaying the earlier versions for the reason `openVersionFour` gives: a helper that
 * shortcut them would open a database that never existed on a real device.
 */
async function openVersionFive(name: string): Promise<Dexie> {
  const legacy = await openVersionFour(name);
  legacy.close();

  const fifth = new Dexie(name);
  fifth.version(1).stores(VERSION_1_STORES);
  fifth.version(2).stores({
    outbox: "mutationId, status, createdAt, subjectId, [status+createdAt]",
  });
  fifth.version(3).stores({}).upgrade((tx) => tx.table("watermarks").delete("outlets"));
  fifth.version(4).stores({}).upgrade((tx) => tx.table("watermarks").delete("outlets"));
  fifth.version(5).stores({ visits: "id, status, outletId" });
  await fifth.open();

  return fifth;
}

/**
 * The schema as W11 slice 9a shipped it — the audit store, before it carried any numbers.
 *
 * Replays the real versions rather than shortcutting to 12, for the reason every helper above gives:
 * a device that skipped one has still run the ones before it, and a fixture that invents a database
 * nobody ever had proves nothing about a real upgrade.
 */
async function openVersionTwelve(name: string): Promise<Dexie> {
  const legacy = await openVersionFive(name);
  legacy.close();

  const twelfth = new Dexie(name);
  twelfth.version(1).stores(VERSION_1_STORES);
  twelfth.version(2).stores({
    outbox: "mutationId, status, createdAt, subjectId, [status+createdAt]",
  });
  twelfth.version(3).stores({}).upgrade((tx) => tx.table("watermarks").delete("outlets"));
  twelfth.version(4).stores({}).upgrade((tx) => tx.table("watermarks").delete("outlets"));
  twelfth.version(5).stores({ visits: "id, status, outletId" });
  twelfth.version(6).stores({ ref_surveys: "id, name" });
  twelfth.version(7).stores({ orders: "id, visitId, status" });
  twelfth.version(8).stores({}).upgrade(async (tx) => {
    await tx.table("watermarks").delete("priceLines");
    await tx.table("watermarks").delete("promotions");
  });
  twelfth.version(9).stores({ ref_tax_rates: "id, [countryCode+taxClassId]" });
  twelfth.version(10).stores({}).upgrade((tx) => tx.table("watermarks").delete("outlets"));
  twelfth.version(11).stores({ ref_order_minimums: "id, channelId, outletId" });
  twelfth.version(12).stores({ audits: "id, visitId, status" });
  await twelfth.open();

  return twelfth;
}

/**
 * The schema as W11 slice 9b shipped it — the audit carries its numbers, and no questionnaire.
 *
 * Replays the earlier versions for the reason every helper above gives, and replays **13's
 * `upgrade()` too**: that one back-fills the three number fields, and a fixture that declared version
 * 13 without it would hand version 14 rows no real device has.
 */
async function openVersionThirteen(name: string): Promise<Dexie> {
  const legacy = await openVersionTwelve(name);
  legacy.close();

  const thirteenth = new Dexie(name);
  thirteenth.version(1).stores(VERSION_1_STORES);
  thirteenth.version(2).stores({
    outbox: "mutationId, status, createdAt, subjectId, [status+createdAt]",
  });
  thirteenth.version(3).stores({}).upgrade((tx) => tx.table("watermarks").delete("outlets"));
  thirteenth.version(4).stores({}).upgrade((tx) => tx.table("watermarks").delete("outlets"));
  thirteenth.version(5).stores({ visits: "id, status, outletId" });
  thirteenth.version(6).stores({ ref_surveys: "id, name" });
  thirteenth.version(7).stores({ orders: "id, visitId, status" });
  thirteenth.version(8).stores({}).upgrade(async (tx) => {
    await tx.table("watermarks").delete("priceLines");
    await tx.table("watermarks").delete("promotions");
  });
  thirteenth.version(9).stores({ ref_tax_rates: "id, [countryCode+taxClassId]" });
  thirteenth.version(10).stores({}).upgrade((tx) => tx.table("watermarks").delete("outlets"));
  thirteenth.version(11).stores({ ref_order_minimums: "id, channelId, outletId" });
  thirteenth.version(12).stores({ audits: "id, visitId, status" });
  thirteenth.version(13).stores({}).upgrade(async (tx) => {
    await tx.table("audits").toCollection().modify((audit: Record<string, unknown>) => {
      audit.facings ??= [];
      audit.prices ??= [];
      audit.categoryFacings ??= null;
    });
  });
  await thirteenth.open();

  return thirteenth;
}

/**
 * The schema as W11 slice 9c shipped it — the audit carries its questionnaire, and no photographs.
 *
 * The last version before the `blobs` store exists, which is the state every device is in the first
 * time it opens a build that takes pictures.
 */
async function openVersionFourteen(name: string): Promise<Dexie> {
  const legacy = await openVersionThirteen(name);
  legacy.close();

  const fourteenth = new Dexie(name);
  fourteenth.version(1).stores(VERSION_1_STORES);
  fourteenth.version(2).stores({
    outbox: "mutationId, status, createdAt, subjectId, [status+createdAt]",
  });
  fourteenth.version(3).stores({}).upgrade((tx) => tx.table("watermarks").delete("outlets"));
  fourteenth.version(4).stores({}).upgrade((tx) => tx.table("watermarks").delete("outlets"));
  fourteenth.version(5).stores({ visits: "id, status, outletId" });
  fourteenth.version(6).stores({ ref_surveys: "id, name" });
  fourteenth.version(7).stores({ orders: "id, visitId, status" });
  fourteenth.version(8).stores({}).upgrade(async (tx) => {
    await tx.table("watermarks").delete("priceLines");
    await tx.table("watermarks").delete("promotions");
  });
  fourteenth.version(9).stores({ ref_tax_rates: "id, [countryCode+taxClassId]" });
  fourteenth.version(10).stores({}).upgrade((tx) => tx.table("watermarks").delete("outlets"));
  fourteenth.version(11).stores({ ref_order_minimums: "id, channelId, outletId" });
  fourteenth.version(12).stores({ audits: "id, visitId, status" });
  fourteenth.version(13).stores({}).upgrade(async (tx) => {
    await tx.table("audits").toCollection().modify((audit: Record<string, unknown>) => {
      audit.facings ??= [];
      audit.prices ??= [];
      audit.categoryFacings ??= null;
    });
  });
  fourteenth.version(14).stores({}).upgrade(async (tx) => {
    await tx.table("audits").toCollection().modify((audit: Record<string, unknown>) => {
      audit.answers ??= [];
      audit.surveyFormId ??= null;
    });
  });
  await fourteenth.open();

  return fourteenth;
}

/**
 * The schema as W11 slice 11 shipped it — photographs exist, and nothing uploads them.
 *
 * The state of every device that took a picture before the uploader existed, which is the population
 * version 16 has to carry forward.
 */
async function openVersionFifteen(name: string): Promise<Dexie> {
  const legacy = await openVersionFourteen(name);
  legacy.close();

  const fifteenth = new Dexie(name);
  fifteenth.version(1).stores(VERSION_1_STORES);
  fifteenth.version(2).stores({
    outbox: "mutationId, status, createdAt, subjectId, [status+createdAt]",
  });
  fifteenth.version(3).stores({}).upgrade((tx) => tx.table("watermarks").delete("outlets"));
  fifteenth.version(4).stores({}).upgrade((tx) => tx.table("watermarks").delete("outlets"));
  fifteenth.version(5).stores({ visits: "id, status, outletId" });
  fifteenth.version(6).stores({ ref_surveys: "id, name" });
  fifteenth.version(7).stores({ orders: "id, visitId, status" });
  fifteenth.version(8).stores({}).upgrade(async (tx) => {
    await tx.table("watermarks").delete("priceLines");
    await tx.table("watermarks").delete("promotions");
  });
  fifteenth.version(9).stores({ ref_tax_rates: "id, [countryCode+taxClassId]" });
  fifteenth.version(10).stores({}).upgrade((tx) => tx.table("watermarks").delete("outlets"));
  fifteenth.version(11).stores({ ref_order_minimums: "id, channelId, outletId" });
  fifteenth.version(12).stores({ audits: "id, visitId, status" });
  fifteenth.version(13).stores({}).upgrade(async (tx) => {
    await tx.table("audits").toCollection().modify((audit: Record<string, unknown>) => {
      audit.facings ??= [];
      audit.prices ??= [];
      audit.categoryFacings ??= null;
    });
  });
  fifteenth.version(14).stores({}).upgrade(async (tx) => {
    await tx.table("audits").toCollection().modify((audit: Record<string, unknown>) => {
      audit.answers ??= [];
      audit.surveyFormId ??= null;
    });
  });
  // Two indexes, not three: `uploadedAtUtc` is version 16's, and declaring it here would let the
  // test open a database no device ever had — and hide the index actually being added.
  fifteenth.version(15).stores({ blobs: "objectKey, auditId" }).upgrade(async (tx) => {
    await tx.table("audits").toCollection().modify((audit: Record<string, unknown>) => {
      audit.photos ??= [];
    });
  });
  await fifteenth.open();

  return fifteenth;
}

/**
 * The schema as W11 slice 12b shipped it — photographs know whether they were uploaded, and never
 * why they were not.
 *
 * Built on the fifteenth for the reason every helper here gives, and declaring 16 itself so the
 * `uploadedAtUtc` index is present exactly as a real device has it.
 */
async function openVersionSixteen(name: string): Promise<Dexie> {
  const legacy = await openVersionFifteen(name);
  legacy.close();

  const sixteenth = new Dexie(name);
  sixteenth.version(1).stores(VERSION_1_STORES);
  sixteenth.version(2).stores({
    outbox: "mutationId, status, createdAt, subjectId, [status+createdAt]",
  });
  sixteenth.version(3).stores({}).upgrade((tx) => tx.table("watermarks").delete("outlets"));
  sixteenth.version(4).stores({}).upgrade((tx) => tx.table("watermarks").delete("outlets"));
  sixteenth.version(5).stores({ visits: "id, status, outletId" });
  sixteenth.version(6).stores({ ref_surveys: "id, name" });
  sixteenth.version(7).stores({ orders: "id, visitId, status" });
  sixteenth.version(8).stores({}).upgrade(async (tx) => {
    await tx.table("watermarks").delete("priceLines");
    await tx.table("watermarks").delete("promotions");
  });
  sixteenth.version(9).stores({ ref_tax_rates: "id, [countryCode+taxClassId]" });
  sixteenth.version(10).stores({}).upgrade((tx) => tx.table("watermarks").delete("outlets"));
  sixteenth.version(11).stores({ ref_order_minimums: "id, channelId, outletId" });
  sixteenth.version(12).stores({ audits: "id, visitId, status" });
  sixteenth.version(13).stores({}).upgrade(async (tx) => {
    await tx.table("audits").toCollection().modify((audit: Record<string, unknown>) => {
      audit.facings ??= [];
      audit.prices ??= [];
      audit.categoryFacings ??= null;
    });
  });
  sixteenth.version(14).stores({}).upgrade(async (tx) => {
    await tx.table("audits").toCollection().modify((audit: Record<string, unknown>) => {
      audit.answers ??= [];
      audit.surveyFormId ??= null;
    });
  });
  sixteenth.version(15).stores({ blobs: "objectKey, auditId" }).upgrade(async (tx) => {
    await tx.table("audits").toCollection().modify((audit: Record<string, unknown>) => {
      audit.photos ??= [];
    });
  });
  sixteenth.version(16).stores({ blobs: "objectKey, auditId, uploadedAtUtc" }).upgrade(async (tx) => {
    await tx.table("blobs").toCollection().modify((blob: Record<string, unknown>) => {
      blob.uploadedAtUtc ??= "";
      blob.attempts ??= 0;
    });
  });
  await sixteenth.open();

  return sixteenth;
}

/**
 * The schema as W11 slice 12c shipped it — photographs know why they are stuck, and nothing knows
 * whether the *server* was told they arrived.
 *
 * Built on the sixteenth, for the reason every helper here gives: a device arrives at the current
 * version through the versions that existed, not through a shortcut this file invented.
 */
async function openVersionSeventeen(name: string): Promise<Dexie> {
  const legacy = await openVersionSixteen(name);
  legacy.close();

  const seventeenth = new Dexie(name);
  seventeenth.version(16).stores({ blobs: "objectKey, auditId, uploadedAtUtc" });
  seventeenth.version(17).stores({}).upgrade(async (tx) => {
    await tx.table("blobs").toCollection().modify((blob: Record<string, unknown>) => {
      blob.lastFailure ??= "";
    });
  });
  await seventeenth.open();

  return seventeenth;
}

function outletRow(id: string, rowVersion: number) {
  return {
    id,
    code: `SHOP-${id}`,
    name: `Shop ${id}`,
    channelId: "11111111-1111-4111-8111-111111111111",
    segment: null,
    status: "Active",
    latitude: null,
    longitude: null,
    countryCode: null,
    radiusMetres: 150,
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

    // It really upgraded rather than finding a fresh one: the current version, holding version 1's
    // rows. Without this the whole file could be passing against databases that never existed at
    // v1. The number moves with every schema version, which is the point — a device that skipped
    // one still has to arrive at the latest.
    expect(upgraded.verno).toBe(18);
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

  it("keeps the reference data and every watermark no version deliberately drops", async () => {
    // Losing these is survivable — the next pull would rebuild them — but only by re-downloading a
    // tenant's catalogue over a connection the rep may not have. A migration that silently reset
    // every watermark would look like a slow morning rather than a bug.
    //
    // The wording moved when version 3 arrived: it drops the *outlets* watermark on purpose, so
    // "keeps the watermarks" is no longer true and asserting it would have made the next deliberate
    // reset look like a regression. What holds is narrower and is the thing worth protecting —
    // nothing is reset as a side effect. `products` is checked because it is the expensive one.
    const name = `migration:${crypto.randomUUID()}`;

    const legacy = await openVersionOne(name);
    await legacy.table("ref_outlets").bulkAdd([outletRow("outlet-1", 4), outletRow("outlet-2", 7)]);
    await legacy.table("watermarks").bulkPut([
      { entity: "outlets", cursor: 7 },
      { entity: "products", cursor: 41 },
    ]);
    await legacy.table("meta").put({ key: "deviceId", value: "device-1" });
    legacy.close();

    const upgraded = new FieldKitDatabase(name);

    expect((await outlets(upgraded)).map((row) => row.id)).toEqual(["outlet-1", "outlet-2"]);
    expect(await watermark(upgraded, PRODUCTS)).toBe(41);
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

describe("upgrading a device whose outlets predate the outlet code", () => {
  it("drops the outlets watermark so the next pull re-baselines them", async () => {
    // `OutletSnapshot` gained `code`, and the delta will never mention a shop nobody edited — so a
    // device that synced under W8 would keep codeless outlets indefinitely unless something asks
    // for them again. Version 3 is that something.
    const name = `migration:${crypto.randomUUID()}`;

    const legacy = await openVersionTwo(name);

    // Yesterday's row: a name, no code. Written through the raw table rather than
    // `applyOutletChanges`, because today's function takes today's type — the point of the fixture
    // is a shape the current code can no longer produce.
    await legacy.table("ref_outlets").add({
      id: "outlet-1",
      name: "Mega Image Dorobanți",
      channelId: "11111111-1111-4111-8111-111111111111",
      segment: null,
      status: "Active",
      latitude: null,
      longitude: null,
      rowVersion: 9,
    });
    await legacy.table("watermarks").bulkPut([
      { entity: "outlets", cursor: 9 },
      { entity: "products", cursor: 41 },
    ]);
    legacy.close();

    const upgraded = new FieldKitDatabase(name);
    await upgraded.open();

    // Back to "I have nothing", which is what makes the server send the whole territory.
    expect(await watermark(upgraded, OUTLETS)).toBe(0);

    // And only outlets. Clearing every watermark would re-download the catalogue and the prices to
    // fix a field on one entity, which is the cost per-entity cursors exist to avoid.
    expect(await watermark(upgraded, PRODUCTS)).toBe(41);

    // The stale row is still there. A device that goes offline between the update and the next
    // successful pull keeps a territory it can work — a name and no code, which is what it had
    // yesterday — rather than an app with no shops in it.
    expect((await outlets(upgraded)).map((outlet) => outlet.id)).toEqual(["outlet-1"]);

    upgraded.close();
  });

  it("re-baselines over the codeless rows rather than beside them", async () => {
    // The half the watermark alone does not prove: the pull that follows has to *replace* the old
    // row, not add a second one. `applyOutletChanges` upserts by id, so this is really a check that
    // the id is unchanged by the version bump — if it were not, a rep would see every shop twice.
    const name = `migration:${crypto.randomUUID()}`;

    const legacy = await openVersionTwo(name);
    await legacy.table("ref_outlets").add({
      id: "outlet-1",
      name: "Mega Image Dorobanți",
      channelId: "11111111-1111-4111-8111-111111111111",
      segment: null,
      status: "Active",
      latitude: null,
      longitude: null,
      rowVersion: 9,
    });
    await legacy.table("watermarks").put({ entity: "outlets", cursor: 9 });
    legacy.close();

    const upgraded = new FieldKitDatabase(name);

    await applyOutletChanges(
      upgraded,
      { upserts: [outletRow("outlet-1", 9)], tombstones: [], cursor: 9 },
      "outlets#9",
    );

    const stored = await outlets(upgraded);

    expect(stored).toHaveLength(1);
    expect(stored[0].code).toBe("SHOP-outlet-1");

    upgraded.close();
  });
});

describe("upgrading a device whose outlets predate the geofence radius", () => {
  it("re-baselines a device that already ran version 3", async () => {
    // The case a second identical upgrade exists for, and the one that would be missed by folding
    // it into version 3: Dexie does not replay a version a database has already seen. A device on 3
    // has its code and no radius, and only a *new* version can tell it to ask again.
    const name = `migration:${crypto.randomUUID()}`;

    const legacy = await openVersionThree(name);

    // Yesterday's row: code, no radius. Written raw, because today's type cannot express it.
    await legacy.table("ref_outlets").add({
      id: "outlet-1",
      code: "RO-BUC-0001",
      name: "Mega Image Dorobanți",
      channelId: "11111111-1111-4111-8111-111111111111",
      segment: null,
      status: "Active",
      latitude: 44.46,
      longitude: 26.09,
      rowVersion: 9,
    });
    await legacy.table("watermarks").bulkPut([
      { entity: "outlets", cursor: 9 },
      { entity: "products", cursor: 41 },
    ]);
    legacy.close();

    const upgraded = new FieldKitDatabase(name);
    await upgraded.open();

    expect(upgraded.verno).toBe(18);
    expect(await watermark(upgraded, OUTLETS)).toBe(0);
    expect(await watermark(upgraded, PRODUCTS)).toBe(41);

    // Still workable in the meantime — a name, a place, and no radius until the next pull lands.
    const stored = await outlets(upgraded);
    expect(stored).toHaveLength(1);
    expect(stored[0].code).toBe("RO-BUC-0001");

    upgraded.close();
  });
});

describe("upgrading a device that predates the visits store", () => {
  it("adds the store without touching the work already queued", async () => {
    // Version 5 adds a *table*, which is the first time that has happened since v1 — and the case
    // `OFF-13` is really about: a rep updates the app mid-day with visits already captured, and the
    // outbox is the one store nothing can rebuild.
    const name = `migration:${crypto.randomUUID()}`;

    const legacy = await openVersionFour(name);
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
    await upgraded.open();

    expect(upgraded.verno).toBe(18);
    expect((await pending(upgraded)).map((entry) => entry.mutationId)).toEqual(["m-1"]);

    // The new store exists and is empty, which is the only correct starting state: there were no
    // visits before this version, so there is nothing to migrate and no `upgrade()` to write.
    expect(await upgraded.visits.count()).toBe(0);

    upgraded.close();
  });
});

describe("upgrading a device that predates the audit's reference stores", () => {
  it("adds both stores without touching the work already queued", async () => {
    // Version 6 (W10 slice 7). Two reference tables and no `upgrade()`: nothing existed to
    // transform, and the outbox — the one store nothing can rebuild — must come across untouched.
    const name = `migration:${crypto.randomUUID()}`;

    const legacy = await openVersionFive(name);
    await legacy.table("outbox").add({
      mutationId: "m-1",
      type: "CapturedAudit",
      subjectId: "audit-1",
      payload: { auditId: "audit-1" },
      status: "pending",
      createdAt: 1_000,
      attempts: 0,
    });

    // A visit captured before the upgrade, so the store added in v5 is checked to survive v6 too —
    // the version *before* the newest is the one an upgrade is most likely to disturb.
    await legacy.table("visits").add({ id: "visit-1", status: "inProgress", outletId: "o-1" });
    legacy.close();

    const upgraded = new FieldKitDatabase(name);
    await upgraded.open();

    expect(upgraded.verno).toBe(18);
    expect((await pending(upgraded)).map((entry) => entry.mutationId)).toEqual(["m-1"]);
    expect(await upgraded.visits.count()).toBe(1);

    // Both new stores exist and are empty — the same state a fresh install is in, so the next pull
    // fills them by the ordinary path rather than a special one.
    expect(await upgraded.surveys.count()).toBe(0);
    expect(await upgraded.scoreWeights.count()).toBe(0);

    // …and their watermarks are at zero, which is what makes the ordinary path work: the server
    // reads a missing cursor as "I have nothing".
    expect(await watermark(upgraded, SURVEYS)).toBe(0);
    expect(await watermark(upgraded, SCORE_WEIGHTS)).toBe(0);

    upgraded.close();
  });

  it("leaves the other watermarks exactly where they were", async () => {
    /*
     * The failure this version could plausibly have caused. Versions 3 and 4 both *delete* the
     * outlets watermark on purpose, to force a re-baseline; a copy-paste of that shape into v6
     * would silently make every device re-download its whole territory on update.
     *
     * So this asserts the negative: adding two stores touches nothing that was already there.
     *
     * <b>The outlets watermark left this test in W11 slice 7c</b>, and honestly rather than by
     * weakening the assertion. Opening the database runs *every* version, and v10 resets that one
     * deliberately — `countryCode` was added to the outlet, and a delta would only fill it in for
     * shops somebody happened to edit afterwards. Products is the surviving witness: it is the
     * watermark no version since has had a reason to touch, so a stray reset still fails here.
     */
    const name = `migration:${crypto.randomUUID()}`;

    const legacy = await openVersionFive(name);
    await legacy.table("watermarks").bulkPut([
      { entity: "outlets", cursor: 4_100 },
      { entity: "products", cursor: 900 },
    ]);
    legacy.close();

    const upgraded = new FieldKitDatabase(name);
    await upgraded.open();

    expect(await watermark(upgraded, PRODUCTS)).toBe(900);

    upgraded.close();
  });
});

describe("upgrading a device to the order store", () => {
  it("keeps unsent work and can hold a draft afterwards", async () => {
    /*
     * `OFF-13` for version 7, stated as the two halves that could each break on their own.
     *
     * The first is the standing promise: an app update must not strand an outbox. The second is
     * specific to a *new store* — a table declared in a version an existing device replays has to
     * end up usable, not merely present, and a mis-declared index is the way that fails. A fresh
     * install would not catch it, because a fresh install never replays anything.
     */
    const name = `migration:${crypto.randomUUID()}`;

    const legacy = await openVersionFive(name);
    await legacy.table("outbox").add({
      mutationId: "m-order-1",
      type: "CapturedVisit",
      subjectId: "visit-1",
      payload: { visitId: "visit-1" },
      status: "pending",
      createdAt: 1_000,
      attempts: 0,
    });
    legacy.close();

    const upgraded = new FieldKitDatabase(name);
    await upgraded.open();

    expect(upgraded.verno).toBe(18);
    expect(await upgraded.outbox.count()).toBe(1);

    // The store arrives empty on an upgraded device, which is the state a fresh install is in — so
    // the rep's next order takes the ordinary path rather than a special one.
    expect(await upgraded.orders.count()).toBe(0);

    const draft = await draftFor(upgraded, {
      visitId: "visit-1",
      outletId: "outlet-1",
      currencyCode: "RON",
      now: new Date("2026-08-12T09:45:00.000Z"),
    });

    // Read back through the `visitId` index rather than by key: the index is the part version 7
    // declares, and a `.get()` would pass against a table that had none.
    expect((await upgraded.orders.where("visitId").equals("visit-1").first())?.id).toBe(draft.id);

    upgraded.close();
  });
});

describe("upgrading a device whose prices are floats", () => {
  it("drops the price and promotion watermarks so the rows are re-pulled", async () => {
    /*
     * W11 slice 7a, and the reason this re-baseline is unlike versions 3 and 4.
     *
     * Those dropped a watermark because a *field had been added* — every row was still the right
     * shape, just thinner. This one drops it because the rows on the device are the wrong **type**:
     * `amount` was a JSON number, which is an IEEE-754 float by the time `JSON.parse` is done, and
     * the pricing engine reads decimal strings precisely so it never sees one.
     *
     * A delta pull alone would not fix it. It sends the rows that changed *after* the cursor, so a
     * price nobody edits again would keep its float for the life of the install — and the rep would
     * price orders from it every day without anything looking wrong.
     */
    const name = `migration:${crypto.randomUUID()}`;

    const legacy = await openVersionFive(name);
    await legacy.table("watermarks").bulkPut([
      { entity: "priceLines", cursor: 900 },
      { entity: "promotions", cursor: 700 },
      { entity: "products", cursor: 500 },
    ]);
    legacy.close();

    const upgraded = new FieldKitDatabase(name);
    await upgraded.open();

    expect(upgraded.verno).toBe(18);

    // The two that carried money go back to zero, so the next pull resends every row.
    expect(await watermark(upgraded, PRICE_LINES)).toBe(0);
    expect(await watermark(upgraded, PROMOTIONS)).toBe(0);

    // …and nothing else is reset. Re-downloading a tenant's whole catalogue over a connection the
    // rep may not have is the cost this deliberately does not pay twice.
    expect(await watermark(upgraded, PRODUCTS)).toBe(500);

    upgraded.close();
  });
});

describe("upgrading a device that has never held a tax rate", () => {
  it("adds the store, and its compound index works on a replayed database", async () => {
    /*
     * `OFF-13` for version 9 (W11 slice 7b), and the half a fresh install cannot catch.
     *
     * `taxRatesFor` reads through `[countryCode+taxClassId]`. A compound index declared in a version
     * an existing device *replays* is the way this quietly fails: the table appears, `.get()` works,
     * and only the indexed query throws — which on the device is the query that decides what tax a
     * rep charges. So the read here goes through the index rather than by key.
     */
    const name = `migration:${crypto.randomUUID()}`;

    const legacy = await openVersionFive(name);
    await legacy.table("outbox").add({
      mutationId: "m-tax-1",
      type: "CapturedVisit",
      subjectId: "visit-1",
      payload: { visitId: "visit-1" },
      status: "pending",
      createdAt: 1_000,
      attempts: 0,
    });
    legacy.close();

    const upgraded = new FieldKitDatabase(name);
    await upgraded.open();

    expect(upgraded.verno).toBe(18);
    expect(await upgraded.outbox.count()).toBe(1);

    // Empty on arrival, like every other new reference store: the next pull fills it, because the
    // watermark it starts from is zero and the server sends everything above zero.
    expect(await upgraded.taxRates.count()).toBe(0);

    await applyTaxRateChanges(upgraded, {
      upserts: [
        {
          id: "r1",
          taxClassId: "standard",
          countryCode: "RO",
          percentage: "19.00",
          effectiveFrom: "2026-01-01",
          effectiveTo: null,
          rowVersion: 3,
        },
      ],
      tombstones: [],
      cursor: 3,
    });

    expect((await taxRatesFor(upgraded, "RO", "standard")).map((each) => each.id)).toEqual(["r1"]);

    upgraded.close();
  });
});

describe("upgrading a device whose outlets have no country", () => {
  it("drops the outlets watermark so every shop is re-pulled", async () => {
    /*
     * W11 slice 7c, and the same shape as versions 3 and 4 rather than version 8's.
     *
     * Those re-baselined because a *field was added*; version 8 re-baselined because the rows were
     * the wrong **type**. This is the first kind: the outlets on the device are fine except that
     * they have no `countryCode`, which nothing could have used an hour ago.
     *
     * A delta pull alone would not fix it — it sends the rows that changed *after* the cursor, so a
     * shop nobody edits again would price untaxed for the life of the install, and the rep would
     * see a plausible total every day without anything looking wrong.
     *
     * And nothing else is reset: re-downloading a tenant's whole catalogue over a connection the
     * rep may not have is the cost this deliberately does not pay twice.
     */
    const name = `migration:${crypto.randomUUID()}`;

    const legacy = await openVersionFive(name);
    await legacy.table("watermarks").bulkPut([
      { entity: "outlets", cursor: 4_100 },
      { entity: "products", cursor: 900 },
      { entity: "taxRates", cursor: 31 },
    ]);
    legacy.close();

    const upgraded = new FieldKitDatabase(name);
    await upgraded.open();

    expect(upgraded.verno).toBe(18);

    expect(await watermark(upgraded, OUTLETS)).toBe(0);

    expect(await watermark(upgraded, PRODUCTS)).toBe(900);
    expect(await watermark(upgraded, TAX_RATES)).toBe(31);

    upgraded.close();
  });
});

describe("upgrading a device that has never held an order minimum", () => {
  it("adds the store, and both its indexes work on a replayed database", async () => {
    /*
     * `OFF-13` for version 11 (W11 slice 8b-ii), and the same trap version 9 named: an index
     * declared in a version an existing device *replays* fails in exactly one way — the table
     * appears, `.get()` works, and only the indexed query throws.
     *
     * Here that would be worse than a wrong tax figure. `orderMinimumFor` reads through *both*
     * `channelId` and `outletId`, and a throw inside a `liveQuery` is swallowed by `useLive`: the
     * screen would resolve no minimum, and `BR-ORD-5` would silently stop applying on precisely the
     * devices that had been running longest. So both indexes are exercised, not just the table.
     */
    const name = `migration:${crypto.randomUUID()}`;

    const legacy = await openVersionFive(name);
    await legacy.table("outbox").add({
      mutationId: "m-min-1",
      type: "CapturedVisit",
      subjectId: "visit-1",
      payload: { visitId: "visit-1" },
      status: "pending",
      createdAt: 1_000,
      attempts: 0,
    });
    legacy.close();

    const upgraded = new FieldKitDatabase(name);
    await upgraded.open();

    expect(upgraded.verno).toBe(18);
    expect(await upgraded.outbox.count()).toBe(1);

    // Empty on arrival, which for this store means every order passes — `BR-ORD-5` applies a minimum
    // *if configured*, and a device that has not synced holds none.
    expect(await upgraded.orderMinimums.count()).toBe(0);

    await applyOrderMinimumChanges(upgraded, {
      upserts: [
        {
          id: "min-channel",
          channelId: "channel-1",
          outletId: null,
          amount: "150.00",
          currencyCode: "RON",
          rowVersion: 3,
        },
        {
          id: "min-outlet",
          channelId: null,
          outletId: "outlet-1",
          amount: "50.00",
          currencyCode: "RON",
          rowVersion: 4,
        },
      ],
      tombstones: [],
      cursor: 4,
    });

    expect(
      (await upgraded.orderMinimums.where("channelId").equals("channel-1").toArray()).map((r) => r.id),
    ).toEqual(["min-channel"]);
    expect(
      (await upgraded.orderMinimums.where("outletId").equals("outlet-1").toArray()).map((r) => r.id),
    ).toEqual(["min-outlet"]);

    upgraded.close();
  });
});

describe("upgrading a device that predates the audit store", () => {
  it("adds the store without touching the work already queued", async () => {
    /*
     * `OFF-13` for version 12 (W11 slice 9a). The store the device *authors* is the one an update
     * must not disturb: every `ref_*` table is a copy the next sync rebuilds, and a queued mutation
     * is the only record that a rep did the work.
     *
     * The audit written afterwards proves the table is real rather than merely declared — a store
     * that opens and cannot be written to would pass a `count()` and fail at the shelf.
     */
    const name = `migration:${crypto.randomUUID()}`;

    const legacy = await openVersionFive(name);
    await legacy.table("outbox").add({
      mutationId: "m-audit-1",
      type: "CapturedVisit",
      subjectId: "visit-1",
      payload: { visitId: "visit-1" },
      status: "pending",
      createdAt: 1_000,
      attempts: 0,
    });
    legacy.close();

    const upgraded = new FieldKitDatabase(name);
    await upgraded.open();

    expect(upgraded.verno).toBe(18);
    expect(await upgraded.outbox.count()).toBe(1);
    expect(await upgraded.audits.count()).toBe(0);

    const started = await auditDraftFor(upgraded, {
      visitId: "visit-1",
      outletId: "outlet-1",
      weightSetVersion: 3,
      now: new Date("2026-03-17T10:00:00.000Z"),
    });

    expect((await upgraded.audits.where("visitId").equals("visit-1").first())?.id).toBe(started.id);

    upgraded.close();
  });
});

describe("upgrading a device holding an audit from before the numbers", () => {
  it("fills in the three fields the wire needs, without disturbing what was measured", async () => {
    /*
     * `OFF-13` for version 13 (W11 slice 9b), and the **first `upgrade()` on a store this device
     * authors**. Every earlier one added a table, or dropped a watermark so a `ref_*` table could be
     * re-pulled. Neither applies here: an audit draft is the rep's only copy, and the three fields
     * `AUD-02` and `AUD-03` add are missing from the rows already on the device.
     *
     * A missing field is normally a reader's problem to default. What makes it a version is
     * `captured()` — `CapturedAudit` takes `facings` and `prices` as **required** lists, so a draft
     * sealed with them `undefined` would send JSON missing two properties and be refused as a 400
     * that retries forever.
     *
     * The audit is written through the *old* shape deliberately: a v12 row is exactly what a rep
     * halfway down an aisle has when the app updates under them.
     */
    const name = `migration:${crypto.randomUUID()}`;

    const legacy = await openVersionTwelve(name);
    await legacy.table("audits").add({
      id: "audit-1",
      visitId: "visit-1",
      outletId: "outlet-1",
      status: "draft",
      weightSetVersion: 3,
      availability: [{ productId: "p-1", status: "OutOfStock" }],
      capturedAtUtc: null,
      updatedAtUtc: "2026-03-17T10:00:00.000Z",
    });
    legacy.close();

    const upgraded = new FieldKitDatabase(name);
    await upgraded.open();

    expect(upgraded.verno).toBe(18);

    const audit = (await upgraded.audits.get("audit-1"))!;

    expect(audit.facings).toEqual([]);
    expect(audit.prices).toEqual([]);

    // Null, not zero — `BR-AUD-2` skips the share-of-shelf pillar without a total, and a zero would
    // say the shop stocks none of the category.
    expect(audit.categoryFacings).toBeNull();

    // What the rep actually measured is untouched, which is the whole point of the exercise.
    expect(audit.availability).toEqual([{ productId: "p-1", status: "OutOfStock" }]);

    upgraded.close();
  });
});

describe("upgrading a device holding an audit from before the questionnaire", () => {
  it("gives it no survey and no answers, without disturbing what was measured", async () => {
    /*
     * `OFF-13` for version 14 (W11 slice 9c). The same argument version 13 makes, with one
     * difference worth stating: `surveyFormId` becomes **null**, which is the ordinary state of an
     * audit rather than a missing one. Most audits are a shelf and no form.
     *
     * What makes it a version rather than a reader's default is `captured()`. It sends the form and
     * the answers both-or-neither, and `undefined` on a v13 row would serialise to a `CapturedAudit`
     * missing `answers` — refused as a 400 that retries forever, which on a sealed audit is a rep's
     * only copy stuck in the outbox.
     *
     * Written through the *old* shape deliberately: a v13 row is what a rep halfway down an aisle has
     * when the app updates under them.
     */
    const name = `migration:${crypto.randomUUID()}`;

    const legacy = await openVersionThirteen(name);
    await legacy.table("audits").add({
      id: "audit-1",
      visitId: "visit-1",
      outletId: "outlet-1",
      status: "draft",
      weightSetVersion: 3,
      availability: [{ productId: "p-1", status: "Present" }],
      facings: [{ productId: "p-1", facings: 4 }],
      prices: [],
      categoryFacings: 20,
      capturedAtUtc: null,
      updatedAtUtc: "2026-03-17T10:00:00.000Z",
    });
    legacy.close();

    const upgraded = new FieldKitDatabase(name);
    await upgraded.open();

    expect(upgraded.verno).toBe(18);

    const audit = (await upgraded.audits.get("audit-1"))!;

    expect(audit.answers).toEqual([]);
    expect(audit.surveyFormId).toBeNull();

    // The shelf the rep worked is untouched, which is the whole point of the exercise.
    expect(audit.availability).toEqual([{ productId: "p-1", status: "Present" }]);
    expect(audit.facings).toEqual([{ productId: "p-1", facings: 4 }]);
    expect(audit.categoryFacings).toBe(20);

    upgraded.close();
  });
});

describe("upgrading a device that has never held a photograph", () => {
  it("adds the blobs store and gives every audit an empty photo list", async () => {
    /*
     * `OFF-13` for version 15 (W11 slice 11), and **the store W8 deliberately did not create**. A
     * table with no writer is a schema claim nobody can check, and its shape would have been guessed
     * a phase early; it arrives now, with the code that fills it.
     *
     * `photos` is back-filled for the reason 13 and 14 back-filled theirs: `captured()` sends it as a
     * list, so a draft sealed with it `undefined` would push JSON missing a property and be refused
     * as a 400 that retries forever.
     */
    const name = `migration:${crypto.randomUUID()}`;

    const legacy = await openVersionFourteen(name);
    await legacy.table("audits").add({
      id: "audit-1",
      visitId: "visit-1",
      outletId: "outlet-1",
      status: "draft",
      weightSetVersion: 3,
      availability: [{ productId: "p-1", status: "Present" }],
      facings: [],
      prices: [],
      categoryFacings: null,
      surveyFormId: null,
      answers: [],
      capturedAtUtc: null,
      updatedAtUtc: "2026-03-17T10:00:00.000Z",
    });
    legacy.close();

    const upgraded = new FieldKitDatabase(name);
    await upgraded.open();

    expect(upgraded.verno).toBe(18);

    const audit = (await upgraded.audits.get("audit-1"))!;

    expect(audit.photos).toEqual([]);
    expect(audit.availability).toEqual([{ productId: "p-1", status: "Present" }]);

    // The store exists and its index answers the question the uploader will ask.
    await upgraded.blobs.add({
      objectKey: "audits/audit-1/photo-1.jpg",
      auditId: "audit-1",
      section: "General",
      image: new Blob([new Uint8Array(8)], { type: "image/jpeg" }),
      bytes: 8,
      capturedAtUtc: "2026-03-17T10:05:00.000Z",
      // Written through *today's* schema, so it carries the fields every version since has added —
      // this test is about the table existing, not about what a version-15 row looked like. That is
      // the test below.
      uploadedAtUtc: WAITING,
      attempts: 0,
      lastFailure: "",
    });

    expect(await upgraded.blobs.where("auditId").equals("audit-1").count()).toBe(1);

    upgraded.close();
  });
});

describe("upgrading a device whose photographs never said why they were stuck", () => {
  it("gives every one an empty reason, without disturbing what it already knew", async () => {
    /*
     * `OFF-13` for version 17 (W11 slice 12c), and the field exists because its absence hid a bug for
     * a whole slice: the uploader caught every failure and recorded only *that* there had been one,
     * so a Content Security Policy refusing every `PUT` looked exactly like a bad signal — and the
     * retry made it look like a bad signal forever.
     *
     * Written through version 16's shape, which is what a device that uploaded nothing all week has.
     */
    const name = `migration:${crypto.randomUUID()}`;

    const legacy = await openVersionSixteen(name);
    await legacy.table("blobs").add({
      objectKey: "audits/audit-1/photo-1.jpg",
      auditId: "audit-1",
      section: "General",
      image: new Blob([new Uint8Array(8)], { type: "image/jpeg" }),
      bytes: 8,
      capturedAtUtc: "2026-03-17T10:05:00.000Z",
      uploadedAtUtc: "",
      attempts: 4,
    });
    legacy.close();

    const upgraded = new FieldKitDatabase(name);
    await upgraded.open();

    expect(upgraded.verno).toBe(18);

    const carried = await upgraded.blobs.get("audits/audit-1/photo-1.jpg");

    expect(carried?.lastFailure).toBe("");

    // What it already knew is untouched — including the four failures, which are the reason a rep
    // would be looking at this row at all.
    expect(carried?.attempts).toBe(4);
    expect(carried?.uploadedAtUtc).toBe(WAITING);
    expect(carried?.bytes).toBe(8);

    upgraded.close();
  });
});

describe("upgrading a device holding photographs nothing has uploaded", () => {
  it("marks every one as waiting, and indexes the answer", async () => {
    /*
     * `OFF-13` for version 16 (W11 slice 12b). Slice 11 stored photographs with no notion of an
     * upload, because there was none; this is the population that exists on every device that took a
     * picture in between.
     *
     * <b>Waiting is the empty string, not null</b>, and that is the version's whole point: IndexedDB
     * will not index `null`, and the uploader asks "what is still waiting" on every sync run. Without
     * the index that question is a scan of every image a rep has taken this week, because Dexie hands
     * back whole records — megabytes of JPEG to read one field.
     */
    const name = `migration:${crypto.randomUUID()}`;

    const legacy = await openVersionFifteen(name);
    await legacy.table("blobs").bulkAdd([
      {
        objectKey: "audits/audit-1/photo-1.jpg",
        auditId: "audit-1",
        section: "General",
        image: new Blob([new Uint8Array(8)], { type: "image/jpeg" }),
        bytes: 8,
        capturedAtUtc: "2026-03-17T10:05:00.000Z",
      },
      {
        objectKey: "audits/audit-1/photo-2.jpg",
        auditId: "audit-1",
        section: "PriceCompliance",
        image: new Blob([new Uint8Array(16)], { type: "image/jpeg" }),
        bytes: 16,
        capturedAtUtc: "2026-03-17T10:06:00.000Z",
      },
    ]);
    legacy.close();

    const upgraded = new FieldKitDatabase(name);
    await upgraded.open();

    expect(upgraded.verno).toBe(18);

    const carried = await upgraded.blobs.get("audits/audit-1/photo-1.jpg");

    expect(carried?.uploadedAtUtc).toBe(WAITING);
    expect(carried?.attempts).toBe(0);

    // The image itself is untouched, which is the point of the exercise: it is the only copy.
    expect(carried?.bytes).toBe(8);

    // And the new index answers the uploader's question rather than making it scan.
    expect(await upgraded.blobs.where("uploadedAtUtc").equals(WAITING).count()).toBe(2);

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

    expect(db.verno).toBe(18);

    // The outbox is still keyed by the mutation id, which is the property the server's ledger
    // depends on: a re-send has to arrive under the id it was captured with.
    expect(db.outbox.schema.primKey.keyPath).toBe("mutationId");

    db.close();
  });
});





describe("upgrading a device that never told the server its photographs arrived", () => {
  it("leaves a waiting photograph waiting, and writes off one already uploaded", async () => {
    /*
     * `OFF-13` for version 18 (W11 slice 13b). The field exists because uploading and being *known*
     * to have uploaded are different facts: the bytes go to storage on a URL the server never sees
     * used, so an upload that succeeded and an acknowledgement that never got through look identical
     * from the back office.
     *
     * <b>Two rows, because they get opposite treatment and the difference is a decision.</b> One
     * still waiting can be confirmed the ordinary way once it goes. One *already uploaded* cannot,
     * ever: its key came back from a presign nobody kept, the tenant prefix is not this device's to
     * rebuild, and there is nothing to send. Marking it confirmed is a small lie told deliberately —
     * the alternative is a row retrying a call it has no arguments for on every sync, forever.
     *
     * What that costs is worth stating: the server reads those references as missing once they are a
     * week old, which is the honest outcome for a photograph it was never told about.
     */
    const name = `migration:${crypto.randomUUID()}`;

    const legacy = await openVersionSeventeen(name);

    await legacy.table("blobs").bulkAdd([
      {
        objectKey: "audits/audit-1/waiting.jpg",
        auditId: "audit-1",
        section: "General",
        image: new Blob([new Uint8Array(8)], { type: "image/jpeg" }),
        bytes: 8,
        capturedAtUtc: "2026-03-17T10:05:00.000Z",
        uploadedAtUtc: "",
        attempts: 2,
        lastFailure: "Failed to fetch",
      },
      {
        objectKey: "audits/audit-1/gone.jpg",
        auditId: "audit-1",
        section: "General",
        image: new Blob([new Uint8Array(8)], { type: "image/jpeg" }),
        bytes: 8,
        capturedAtUtc: "2026-03-17T10:06:00.000Z",
        uploadedAtUtc: "2026-03-17T11:00:00.000Z",
        attempts: 0,
        lastFailure: "",
      },
    ]);

    legacy.close();

    const upgraded = new FieldKitDatabase(name);
    await upgraded.open();

    expect(upgraded.verno).toBe(18);

    const stillWaiting = await upgraded.blobs.get("audits/audit-1/waiting.jpg");
    const alreadyGone = await upgraded.blobs.get("audits/audit-1/gone.jpg");

    expect(stillWaiting?.storedKey).toBe("");
    expect(stillWaiting?.confirmedAtUtc).toBe(WAITING);

    expect(alreadyGone?.storedKey).toBe("");
    expect(alreadyGone?.confirmedAtUtc).toBe("2026-03-17T11:00:00.000Z");

    // What each row already knew is untouched — the reason a rep would be looking at the first one
    // at all, and the upload time on the second.
    expect(stillWaiting?.attempts).toBe(2);
    expect(stillWaiting?.lastFailure).toBe("Failed to fetch");
    expect(alreadyGone?.uploadedAtUtc).toBe("2026-03-17T11:00:00.000Z");

    upgraded.close();
  });

  it("indexes the answer, so the confirm pass is a seek rather than a scan", async () => {
    // The same reason version 16 indexed `uploadedAtUtc`: this question is asked on every sync run,
    // and without the index it reads every JPEG on the device to look at one field.
    const name = `migration:${crypto.randomUUID()}`;

    const legacy = await openVersionSeventeen(name);
    legacy.close();

    const upgraded = new FieldKitDatabase(name);
    await upgraded.open();

    expect(upgraded.blobs.schema.indexes.map((index) => index.name)).toContain("confirmedAtUtc");

    upgraded.close();
  });
});
