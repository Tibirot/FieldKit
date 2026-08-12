/**
 * @vitest-environment jsdom
 *
 * For `window` alone — the manager listens for `online`, and `OFF-06` says a reconnect syncs
 * without anybody tapping anything. Everything else here would run in `node`.
 */
import "fake-indexeddb/auto";

import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { ApiError } from "@/lib/api/client";

import { FieldKitDatabase } from "./db";
import { ensureDevice, startSync, syncOnce } from "./manager";
import { enqueue, pending } from "./outbox";
import {
  applyOutletChanges,
  ASSORTMENT,
  assortmentFor,
  CONFIGURATION,
  JOURNEYS,
  outlet,
  OUTLET_ASSORTMENT,
  OUTLETS,
  plannedVisits,
  PRICE_ASSIGNMENTS,
  PRICE_LINES,
  PRICE_LISTS,
  priceListFor,
  priceOf,
  product,
  products,
  PRODUCTS,
  PROMOTION_ASSIGNMENTS,
  PROMOTIONS,
  promotionsFor,
  TAX_RATES,
  taxRatesFor,
  watermark,
  workflowFor,
} from "./reference";

const api = vi.hoisted(() => ({
  bindDevice: vi.fn(),
  pull: vi.fn(),
  push: vi.fn(),
}));

vi.mock("@/lib/api/sync", () => api);

const DEVICE = "device-1";
const CHANNEL = "11111111-1111-4111-8111-111111111111";
const TOKEN = "token";

function freshDatabase(): FieldKitDatabase {
  return new FieldKitDatabase(`test:${crypto.randomUUID()}`);
}

function outletRow(id: string, rowVersion: number, name = "Corner Shop") {
  return {
    id,
    code: `SHOP-${id}`,
    name,
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

/** A pull that carries nothing, for the tests that are only about the push half. */
function emptyPull(cursor = 0) {
  return {
    changes: {
      outlets: { upserts: [], tombstones: [], cursor },
      journeys: { upserts: [], tombstones: [], cursor: 0 },
      configuration: { upserts: [], tombstones: [], cursor: 0 },
      products: { upserts: [], tombstones: [], cursor: 0 },
      assortment: { upserts: [], tombstones: [], cursor: 0 },
      outletAssortment: { upserts: [], tombstones: [], cursor: 0 },
      priceLists: { upserts: [], tombstones: [], cursor: 0 },
      priceLines: { upserts: [], tombstones: [], cursor: 0 },
      priceAssignments: { upserts: [], tombstones: [], cursor: 0 },
      promotions: { upserts: [], tombstones: [], cursor: 0 },
      promotionAssignments: { upserts: [], tombstones: [], cursor: 0 },
      surveys: { upserts: [], tombstones: [], cursor: 0 },
      scoreWeights: { upserts: [], tombstones: [], cursor: 0 },
      taxRates: { upserts: [], tombstones: [], cursor: 0 },
    },
    snapshotVersion: `outlets#${cursor}`,
  };
}

function plannedVisitRow(id: string, rowVersion: number, date = "2026-03-17") {
  return {
    id,
    outletId: "22222222-2222-4222-8222-222222222222",
    date,
    status: "Planned",
    source: "Generated",
    notVisitedReason: null,
    rowVersion,
  };
}

function productRow(id: string, rowVersion: number, name = "Cola 500ml") {
  return {
    id,
    sku: id.toUpperCase(),
    name,
    brandId: null,
    categoryId: null,
    taxClassId: null,
    unitOfMeasure: "EA",
    packSize: 24,
    status: "Active",
    rowVersion,
  };
}

function accepted(mutationIds: string[]) {
  return {
    results: mutationIds.map((mutationId) => ({
      mutationId,
      status: "accepted" as const,
      reason: null,
      detail: null,
    })),
  };
}

beforeEach(() => {
  api.bindDevice.mockReset();
  api.pull.mockReset().mockResolvedValue(emptyPull());
  api.push.mockReset();
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe("binding a device", () => {
  it("binds once and remembers which one", async () => {
    // Rebinding per launch would deactivate the previous registration as a swap every time, and a
    // rep would spend their life being told to sync again from zero.
    const db = freshDatabase();
    api.bindDevice.mockResolvedValue({ id: DEVICE });
    vi.stubGlobal("navigator", { userAgent: "Mozilla/5.0 (Linux; Android 14)", storage: {} });

    expect(await ensureDevice(db, TOKEN)).toBe(DEVICE);
    expect(await ensureDevice(db, TOKEN)).toBe(DEVICE);
    expect(api.bindDevice).toHaveBeenCalledOnce();
    expect(api.bindDevice).toHaveBeenCalledWith(TOKEN, "Android device");

    db.close();
  });

  it("binds once for two callers who arrive together, not once each", async () => {
    // The test above passes on a sequential pair and says nothing about a concurrent one, which is
    // the case that actually happens: React double-invokes effects in development, and that is two
    // callers reaching the stored-id check before either has written an id. Both miss, both post,
    // and the second is refused by the unique index behind "one active device per rep" — reported
    // as a 500. Found in the browser on the field shell's first launch (W9 slice 1).
    const db = freshDatabase();
    vi.stubGlobal("navigator", { userAgent: "Mozilla/5.0", storage: {} });

    // Held open so both callers are genuinely in flight at once rather than merely interleaved.
    let bound: (device: { id: string }) => void = () => {};
    api.bindDevice.mockReturnValue(new Promise<{ id: string }>((resolve) => (bound = resolve)));

    const first = ensureDevice(db, TOKEN);
    const second = ensureDevice(db, TOKEN);

    bound({ id: DEVICE });

    expect(await first).toBe(DEVICE);
    expect(await second).toBe(DEVICE);
    expect(api.bindDevice).toHaveBeenCalledOnce();

    db.close();
  });

  it("asks for persistent storage on the first bind, while the answer is still useful", async () => {
    const persist = vi.fn().mockResolvedValue(true);
    vi.stubGlobal("navigator", {
      userAgent: "Mozilla/5.0",
      storage: { persist, persisted: vi.fn().mockResolvedValue(false) },
    });

    const db = freshDatabase();
    api.bindDevice.mockResolvedValue({ id: DEVICE });

    await ensureDevice(db, TOKEN);

    expect(persist).toHaveBeenCalledOnce();

    db.close();
  });
});

describe("one sync run", () => {
  it("pushes before it pulls", async () => {
    // The day's work reaches the back office as early as possible. Pulling first would spend the
    // first — possibly only — seconds of a reconnect downloading reference data nobody is waiting
    // for, while the visits sit on the phone.
    const order: string[] = [];
    const db = freshDatabase();

    await enqueue(db, { type: "CapturedVisit", subjectId: "visit-1", payload: { id: "visit-1" } });

    api.push.mockImplementation(async (_t: string, _d: string, mutations: { mutationId: string }[]) => {
      order.push("push");
      return accepted(mutations.map((mutation) => mutation.mutationId));
    });
    api.pull.mockImplementation(async () => {
      order.push("pull");
      return emptyPull();
    });

    await syncOnce(db, TOKEN, DEVICE);

    expect(order).toEqual(["push", "pull"]);

    db.close();
  });

  it("sends the payload the outbox stored, under the id it was captured with", async () => {
    const db = freshDatabase();

    const entry = await enqueue(db, {
      type: "CapturedVisit",
      subjectId: "visit-1",
      payload: { visitId: "visit-1", outcome: "Productive" },
    });

    api.push.mockResolvedValue(accepted([entry.mutationId]));

    await syncOnce(db, TOKEN, DEVICE);

    expect(api.push).toHaveBeenCalledWith(TOKEN, DEVICE, [
      {
        mutationId: entry.mutationId,
        type: "CapturedVisit",
        visit: { visitId: "visit-1", outcome: "Productive" },
      },
    ], undefined);

    db.close();
  });

  it("puts each kind of mutation under the property its type names (W9 slice 9)", async () => {
    /*
     * Found by a live round trip, not by this suite — which is why it is here now.
     *
     * The wire format is a typed property per kind, so `type` alone does not say where the payload
     * goes: the server binds `notVisited` into a `NotVisitedCall` and `visit` into a `CapturedVisit`.
     * A payload under the wrong name is a **400**, and a 400 fails the whole batch and is retried on
     * every reconnect forever — strictly worse than a refusal, which is recorded once and stops.
     *
     * With one mutation type the manager hard-coded `visit:` and was right by accident. This is the
     * assertion that stops the next type being wrong by accident too.
     */
    const db = freshDatabase();

    const visit = await enqueue(db, {
      type: "CapturedVisit",
      subjectId: "visit-1",
      payload: { visitId: "visit-1" },
    });
    const report = await enqueue(db, {
      type: "NotVisitedCall",
      subjectId: "call-1",
      payload: { plannedVisitId: "call-1", reason: "Closed on arrival" },
    });

    api.push.mockResolvedValue(accepted([visit.mutationId, report.mutationId]));

    await syncOnce(db, TOKEN, DEVICE);

    expect(api.push).toHaveBeenCalledWith(TOKEN, DEVICE, [
      { mutationId: visit.mutationId, type: "CapturedVisit", visit: { visitId: "visit-1" } },
      {
        mutationId: report.mutationId,
        type: "NotVisitedCall",
        notVisited: { plannedVisitId: "call-1", reason: "Closed on arrival" },
      },
    ], undefined);

    db.close();
  });

  it("clears accepted work and keeps a rejection with its reason", async () => {
    const db = freshDatabase();

    const good = await enqueue(db, { type: "CapturedVisit", subjectId: "a", payload: {} });
    const bad = await enqueue(db, { type: "CapturedVisit", subjectId: "b", payload: {} });

    api.push.mockResolvedValue({
      results: [
        { mutationId: good.mutationId, status: "accepted", reason: null, detail: null },
        {
          mutationId: bad.mutationId,
          status: "rejected",
          reason: "visit.ingest.outletUnknown",
          detail: "No such outlet.",
        },
      ],
    });

    const result = await syncOnce(db, TOKEN, DEVICE);

    expect(result).toMatchObject({ pushed: 1, rejected: 1 });
    expect(await db.outbox.get(good.mutationId)).toBeUndefined();
    expect(await db.outbox.get(bad.mutationId)).toMatchObject({
      status: "failed",
      errorCode: "visit.ingest.outletUnknown",
    });

    // A rejection is not retried, so the run ends rather than pushing it again forever.
    expect(api.push).toHaveBeenCalledOnce();

    db.close();
  });

  it("applies the pull with its watermark", async () => {
    const db = freshDatabase();

    api.pull.mockResolvedValue({
      changes: {
        outlets: { upserts: [outletRow("outlet-1", 9)], tombstones: [], cursor: 9 },
        journeys: { upserts: [], tombstones: [], cursor: 0 },
        configuration: { upserts: [], tombstones: [], cursor: 0 },
        products: { upserts: [], tombstones: [], cursor: 0 },
        assortment: { upserts: [], tombstones: [], cursor: 0 },
        outletAssortment: { upserts: [], tombstones: [], cursor: 0 },
        priceLists: { upserts: [], tombstones: [], cursor: 0 },
        priceLines: { upserts: [], tombstones: [], cursor: 0 },
        priceAssignments: { upserts: [], tombstones: [], cursor: 0 },
        promotions: { upserts: [], tombstones: [], cursor: 0 },
        promotionAssignments: { upserts: [], tombstones: [], cursor: 0 },
        surveys: { upserts: [], tombstones: [], cursor: 0 },
        scoreWeights: { upserts: [], tombstones: [], cursor: 0 },
        taxRates: { upserts: [], tombstones: [], cursor: 0 },
      },
      snapshotVersion: "outlets#9",
    });

    const result = await syncOnce(db, TOKEN, DEVICE);

    expect(result).toMatchObject({ pulled: 1, dropped: 0, cursor: 9 });
    expect(await outlet(db, "outlet-1")).toBeDefined();
    expect(await watermark(db, OUTLETS)).toBe(9);
    expect(await db.meta.get("lastSyncAt")).toBeDefined();

    db.close();
  });

  it("stores the round and its own watermark, separately from the outlets'", async () => {
    // The two entities advance independently — a tenant that edits outlets hourly and publishes a
    // plan monthly would, on a shared cursor, make every outlet edit look like a journey change.
    const db = freshDatabase();

    api.pull.mockResolvedValue({
      changes: {
        outlets: { upserts: [outletRow("outlet-1", 9)], tombstones: [], cursor: 9 },
        journeys: { upserts: [plannedVisitRow("call-1", 3)], tombstones: [], cursor: 3 },
        configuration: { upserts: [], tombstones: [], cursor: 0 },
        products: { upserts: [], tombstones: [], cursor: 0 },
        assortment: { upserts: [], tombstones: [], cursor: 0 },
        outletAssortment: { upserts: [], tombstones: [], cursor: 0 },
        priceLists: { upserts: [], tombstones: [], cursor: 0 },
        priceLines: { upserts: [], tombstones: [], cursor: 0 },
        priceAssignments: { upserts: [], tombstones: [], cursor: 0 },
        promotions: { upserts: [], tombstones: [], cursor: 0 },
        promotionAssignments: { upserts: [], tombstones: [], cursor: 0 },
        surveys: { upserts: [], tombstones: [], cursor: 0 },
        scoreWeights: { upserts: [], tombstones: [], cursor: 0 },
        taxRates: { upserts: [], tombstones: [], cursor: 0 },
      },
      snapshotVersion: "outlets#9",
    });

    const result = await syncOnce(db, TOKEN, DEVICE);

    expect(result.pulled).toBe(2);
    expect(await watermark(db, OUTLETS)).toBe(9);
    expect(await watermark(db, JOURNEYS)).toBe(3);
    expect(await plannedVisits(db, "2026-03-17")).toHaveLength(1);

    db.close();
  });

  it("stores a workflow whole, with its steps and its own watermark", async () => {
    // The steps travel inside the workflow rather than as a fourth entity. A device holding four of
    // five steps would run a visit asking for less than the tenant configured, and BR-VIS-3 would
    // gate check-out on a mandatory step it never received.
    const db = freshDatabase();

    api.pull.mockResolvedValue({
      changes: {
        outlets: { upserts: [], tombstones: [], cursor: 0 },
        journeys: { upserts: [], tombstones: [], cursor: 0 },
        configuration: {
          upserts: [
            {
              id: "workflow-1",
              channelId: CHANNEL,
              presenceExpected: true,
              steps: [
                { order: 1, type: "Audit", mandatory: true, label: "Shelf check" },
                { order: 2, type: "Note", mandatory: false, label: "Anything else" },
              ],
              rowVersion: 5,
            },
          ],
          tombstones: [],
          cursor: 5,
        },
        products: { upserts: [], tombstones: [], cursor: 0 },
        assortment: { upserts: [], tombstones: [], cursor: 0 },
        outletAssortment: { upserts: [], tombstones: [], cursor: 0 },
        priceLists: { upserts: [], tombstones: [], cursor: 0 },
        priceLines: { upserts: [], tombstones: [], cursor: 0 },
        priceAssignments: { upserts: [], tombstones: [], cursor: 0 },
        promotions: { upserts: [], tombstones: [], cursor: 0 },
        promotionAssignments: { upserts: [], tombstones: [], cursor: 0 },
        surveys: { upserts: [], tombstones: [], cursor: 0 },
        scoreWeights: { upserts: [], tombstones: [], cursor: 0 },
        taxRates: { upserts: [], tombstones: [], cursor: 0 },
      },
      snapshotVersion: "outlets#0",
    });

    await syncOnce(db, TOKEN, DEVICE);

    const stored = await workflowFor(db, CHANNEL);

    expect(stored?.steps).toHaveLength(2);
    expect(stored?.steps[0]).toMatchObject({ type: "Audit", mandatory: true });
    expect(await watermark(db, CONFIGURATION)).toBe(5);

    db.close();
  });

  it("counts every page in the totals, not just the ones that were there first", async () => {
    // The hand-written sum silently stopped counting configuration when slice 8b added it. Nothing
    // failed — the totals only feed an indicator — and no test asserted on them. This is that test.
    const db = freshDatabase();

    api.pull.mockResolvedValue({
      changes: {
        outlets: { upserts: [outletRow("outlet-1", 9)], tombstones: [], cursor: 9 },
        journeys: { upserts: [plannedVisitRow("call-1", 3)], tombstones: [], cursor: 3 },
        configuration: {
          upserts: [
            { id: "workflow-1", channelId: CHANNEL, presenceExpected: true, steps: [], rowVersion: 5 },
          ],
          tombstones: [],
          cursor: 5,
        },
        products: {
          upserts: [productRow("product-1", 7)],
          tombstones: [{ id: "product-2", rowVersion: 8 }],
          cursor: 8,
        },
        assortment: { upserts: [], tombstones: [], cursor: 0 },
        outletAssortment: { upserts: [], tombstones: [], cursor: 0 },
        priceLists: { upserts: [], tombstones: [], cursor: 0 },
        priceLines: { upserts: [], tombstones: [], cursor: 0 },
        priceAssignments: { upserts: [], tombstones: [], cursor: 0 },
        promotions: { upserts: [], tombstones: [], cursor: 0 },
        promotionAssignments: { upserts: [], tombstones: [], cursor: 0 },
        surveys: { upserts: [], tombstones: [], cursor: 0 },
        scoreWeights: { upserts: [], tombstones: [], cursor: 0 },
        taxRates: { upserts: [], tombstones: [], cursor: 0 },
      },
      snapshotVersion: "outlets#9",
    });

    const result = await syncOnce(db, TOKEN, DEVICE);

    expect(result.pulled).toBe(4);
    expect(result.dropped).toBe(1);

    db.close();
  });

  it("stores the catalogue and offers only what is still sold", async () => {
    // The device holds a discontinued product so it can still *name* one on an order taken last
    // week. Offering it in a picker is how a rep orders something the tenant stopped selling.
    const db = freshDatabase();

    api.pull.mockResolvedValue({
      changes: {
        outlets: { upserts: [], tombstones: [], cursor: 0 },
        journeys: { upserts: [], tombstones: [], cursor: 0 },
        configuration: { upserts: [], tombstones: [], cursor: 0 },
        products: {
          upserts: [
            productRow("product-1", 7, "Alpha"),
            { ...productRow("product-2", 8, "Beta"), status: "Discontinued" },
          ],
          tombstones: [],
          cursor: 8,
        },
        assortment: { upserts: [], tombstones: [], cursor: 0 },
        outletAssortment: { upserts: [], tombstones: [], cursor: 0 },
        priceLists: { upserts: [], tombstones: [], cursor: 0 },
        priceLines: { upserts: [], tombstones: [], cursor: 0 },
        priceAssignments: { upserts: [], tombstones: [], cursor: 0 },
        promotions: { upserts: [], tombstones: [], cursor: 0 },
        promotionAssignments: { upserts: [], tombstones: [], cursor: 0 },
        surveys: { upserts: [], tombstones: [], cursor: 0 },
        scoreWeights: { upserts: [], tombstones: [], cursor: 0 },
        taxRates: { upserts: [], tombstones: [], cursor: 0 },
      },
      snapshotVersion: "outlets#0",
    });

    await syncOnce(db, TOKEN, DEVICE);

    expect((await products(db)).map((row) => row.name)).toEqual(["Alpha"]);
    expect(await product(db, "product-2")).toMatchObject({ status: "Discontinued" });
    expect(await watermark(db, PRODUCTS)).toBe(8);

    db.close();
  });

  it("drops a workflow the tenant deleted, and reports nothing for that channel", async () => {
    // The device then falls back to the default — no steps, presence expected — which is the same
    // answer the server gives for a channel nobody configured.
    const db = freshDatabase();

    api.pull.mockResolvedValueOnce({
      changes: {
        outlets: { upserts: [], tombstones: [], cursor: 0 },
        journeys: { upserts: [], tombstones: [], cursor: 0 },
        configuration: {
          upserts: [
            { id: "workflow-1", channelId: CHANNEL, presenceExpected: true, steps: [], rowVersion: 5 },
          ],
          tombstones: [],
          cursor: 5,
        },
        products: { upserts: [], tombstones: [], cursor: 0 },
        assortment: { upserts: [], tombstones: [], cursor: 0 },
        outletAssortment: { upserts: [], tombstones: [], cursor: 0 },
        priceLists: { upserts: [], tombstones: [], cursor: 0 },
        priceLines: { upserts: [], tombstones: [], cursor: 0 },
        priceAssignments: { upserts: [], tombstones: [], cursor: 0 },
        promotions: { upserts: [], tombstones: [], cursor: 0 },
        promotionAssignments: { upserts: [], tombstones: [], cursor: 0 },
        surveys: { upserts: [], tombstones: [], cursor: 0 },
        scoreWeights: { upserts: [], tombstones: [], cursor: 0 },
        taxRates: { upserts: [], tombstones: [], cursor: 0 },
      },
      snapshotVersion: "outlets#0",
    });

    await syncOnce(db, TOKEN, DEVICE);
    expect(await workflowFor(db, CHANNEL)).toBeDefined();

    api.pull.mockResolvedValueOnce({
      changes: {
        outlets: { upserts: [], tombstones: [], cursor: 0 },
        journeys: { upserts: [], tombstones: [], cursor: 0 },
        configuration: {
          upserts: [],
          tombstones: [{ id: "workflow-1", rowVersion: 6 }],
          cursor: 6,
        },
        products: { upserts: [], tombstones: [], cursor: 0 },
        assortment: { upserts: [], tombstones: [], cursor: 0 },
        outletAssortment: { upserts: [], tombstones: [], cursor: 0 },
        priceLists: { upserts: [], tombstones: [], cursor: 0 },
        priceLines: { upserts: [], tombstones: [], cursor: 0 },
        priceAssignments: { upserts: [], tombstones: [], cursor: 0 },
        promotions: { upserts: [], tombstones: [], cursor: 0 },
        promotionAssignments: { upserts: [], tombstones: [], cursor: 0 },
        surveys: { upserts: [], tombstones: [], cursor: 0 },
        scoreWeights: { upserts: [], tombstones: [], cursor: 0 },
        taxRates: { upserts: [], tombstones: [], cursor: 0 },
      },
      snapshotVersion: "outlets#0",
    });

    await syncOnce(db, TOKEN, DEVICE);

    expect(await workflowFor(db, CHANNEL)).toBeUndefined();
    expect(await watermark(db, CONFIGURATION)).toBe(6);

    db.close();
  });

  it("keeps the outlets when storing the round fails", async () => {
    // Two transactions rather than one: a device that got half a pull keeps the half it got, and
    // asks for the rest next time. One transaction would throw the outlets away too.
    const db = freshDatabase();

    api.pull.mockResolvedValue({
      changes: {
        outlets: { upserts: [outletRow("outlet-1", 9)], tombstones: [], cursor: 9 },
        journeys: { upserts: [plannedVisitRow("call-1", 3)], tombstones: [], cursor: 3 },
        configuration: { upserts: [], tombstones: [], cursor: 0 },
        products: { upserts: [], tombstones: [], cursor: 0 },
        assortment: { upserts: [], tombstones: [], cursor: 0 },
        outletAssortment: { upserts: [], tombstones: [], cursor: 0 },
        priceLists: { upserts: [], tombstones: [], cursor: 0 },
        priceLines: { upserts: [], tombstones: [], cursor: 0 },
        priceAssignments: { upserts: [], tombstones: [], cursor: 0 },
        promotions: { upserts: [], tombstones: [], cursor: 0 },
        promotionAssignments: { upserts: [], tombstones: [], cursor: 0 },
        surveys: { upserts: [], tombstones: [], cursor: 0 },
        scoreWeights: { upserts: [], tombstones: [], cursor: 0 },
        taxRates: { upserts: [], tombstones: [], cursor: 0 },
      },
      snapshotVersion: "outlets#9",
    });

    vi.spyOn(db.plannedVisits, "bulkPut").mockRejectedValueOnce(new Error("storage went away"));

    await expect(syncOnce(db, TOKEN, DEVICE)).rejects.toThrow();

    expect(await outlet(db, "outlet-1")).toBeDefined();
    expect(await watermark(db, OUTLETS)).toBe(9);
    expect(await watermark(db, JOURNEYS)).toBe(0);

    db.close();
  });

  it("asks for changes since the watermark it already holds", async () => {
    const db = freshDatabase();

    await applyOutletChanges(db, { upserts: [outletRow("outlet-1", 4)], tombstones: [], cursor: 4 });

    await syncOnce(db, TOKEN, DEVICE);

    expect(api.pull).toHaveBeenCalledWith(TOKEN, DEVICE, {
      outlets: 4,
      journeys: 0,
      configuration: 0,
      products: 0,
      assortment: 0,
      outletAssortment: 0,
      priceLists: 0,
      priceLines: 0,
      priceAssignments: 0,
      promotions: 0,
      promotionAssignments: 0,
      surveys: 0,
      scoreWeights: 0,
      taxRates: 0,
    }, undefined);

    db.close();
  });
});

describe("when the run is interrupted", () => {
  it("returns a lost batch to pending rather than stranding it in flight", async () => {
    // We cannot tell a lost response from a lost request, so the work has to be re-sendable — and
    // it is, because the mutation ids have not changed and the server's ledger is keyed by them.
    const db = freshDatabase();

    await enqueue(db, { type: "CapturedVisit", subjectId: "a", payload: {} });
    api.push.mockRejectedValue(new TypeError("Failed to fetch"));

    const result = await syncOnce(db, TOKEN, DEVICE);

    expect(result.interrupted).toBe("offline");
    expect((await pending(db)).length).toBe(1);
    expect(await db.outbox.where("status").equals("inflight").count()).toBe(0);

    db.close();
  });

  it("does not pull after a failed push", async () => {
    // A pull would be refused for the same reason and fail the same way: one request, not two.
    const db = freshDatabase();

    await enqueue(db, { type: "CapturedVisit", subjectId: "a", payload: {} });
    api.push.mockRejectedValue(new TypeError("Failed to fetch"));

    await syncOnce(db, TOKEN, DEVICE);

    expect(api.pull).not.toHaveBeenCalled();

    db.close();
  });

  it("keeps what the pull did land when it fails", async () => {
    const db = freshDatabase();

    await applyOutletChanges(db, { upserts: [outletRow("outlet-1", 3)], tombstones: [], cursor: 3 });
    api.pull.mockRejectedValue(new TypeError("Failed to fetch"));

    const result = await syncOnce(db, TOKEN, DEVICE);

    expect(result.interrupted).toBe("offline");
    expect(result.cursor).toBe(3);
    expect(await outlet(db, "outlet-1")).toBeDefined();

    db.close();
  });

  it.each([
    [401, "unauthorized"],
    [403, "deviceRejected"],
    [404, "deviceRejected"],
    [409, "deviceRejected"],
    [500, "failed"],
  ])("tells the caller whose problem a %i is", async (status, expected) => {
    // Collapsing these into "sync failed" is how a rep spends an hour retrying against a 401, or
    // waits for a connection that is fine while the server has revoked their device.
    const db = freshDatabase();

    api.pull.mockRejectedValue(new ApiError(status));

    expect((await syncOnce(db, TOKEN, DEVICE)).interrupted).toBe(expected);

    db.close();
  });

  it("stops instead of spinning when the server answers about fewer mutations than were sent", async () => {
    // A loop that re-reads `pending` after a partial answer would push a batch that never shrinks,
    // forever, on the one connection the rep is short of.
    const db = freshDatabase();

    const first = await enqueue(db, { type: "CapturedVisit", subjectId: "a", payload: {} });
    await enqueue(db, { type: "CapturedVisit", subjectId: "b", payload: {} });

    api.push.mockResolvedValue(accepted([first.mutationId]));

    const result = await syncOnce(db, TOKEN, DEVICE);

    expect(result).toMatchObject({ pushed: 1, interrupted: "failed" });
    expect(api.push).toHaveBeenCalledOnce();
    expect((await pending(db)).length).toBe(1);

    db.close();
  });
});

describe("the manager", () => {
  it("joins a run already in progress instead of starting a second", async () => {
    // Two concurrent runs would push the same batch twice — harmless server-side thanks to the
    // ledger, but it doubles the traffic on the one connection the rep is short of, and the second
    // pull could apply an older page over a newer one.
    const db = freshDatabase();

    await enqueue(db, { type: "CapturedVisit", subjectId: "a", payload: {} });

    let release: () => void = () => {};
    const held = new Promise<void>((resolve) => {
      release = resolve;
    });

    api.push.mockImplementation(async (_t: string, _d: string, mutations: { mutationId: string }[]) => {
      await held;
      return accepted(mutations.map((mutation) => mutation.mutationId));
    });

    const manager = startSync(db, () => TOKEN, DEVICE);

    const first = manager.syncNow();
    const second = manager.syncNow();

    expect(second).toBe(first);

    release();
    await first;

    expect(api.push).toHaveBeenCalledOnce();

    manager.stop();
    db.close();
  });

  it("runs again once the previous run is done", async () => {
    const db = freshDatabase();
    const manager = startSync(db, () => TOKEN, DEVICE);

    await manager.syncNow();
    await manager.syncNow();

    expect(api.pull).toHaveBeenCalledTimes(2);

    manager.stop();
    db.close();
  });

  it("reclaims work stranded by a crash, once, before the first run", async () => {
    // The rows a device killed mid-push leaves behind. Nothing will ever answer them, and without
    // this they sit there while the rep is told their work is syncing.
    const db = freshDatabase();

    const entry = await enqueue(db, { type: "CapturedVisit", subjectId: "a", payload: {} });
    await db.outbox.update(entry.mutationId, { status: "inflight" });

    api.push.mockResolvedValue(accepted([entry.mutationId]));

    const manager = startSync(db, () => TOKEN, DEVICE);
    const result = await manager.syncNow();

    expect(result.pushed).toBe(1);
    expect(await db.outbox.count()).toBe(0);

    manager.stop();
    db.close();
  });

  it("syncs when the device comes back online", async () => {
    const db = freshDatabase();
    const manager = startSync(db, () => TOKEN, DEVICE);

    window.dispatchEvent(new Event("online"));
    await vi.waitFor(() => expect(api.pull).toHaveBeenCalled());

    manager.stop();
    db.close();
  });

  it("stops listening when it is stopped", async () => {
    // A manager left listening after the signed-in user changes would sync one rep's outbox with
    // another rep's device id.
    const db = freshDatabase();
    const manager = startSync(db, () => TOKEN, DEVICE);

    manager.stop();
    window.dispatchEvent(new Event("online"));
    await Promise.resolve();

    expect(api.pull).not.toHaveBeenCalled();

    db.close();
  });

  it("waits rather than failing when there is no session", async () => {
    // Signed out is not a sync failure. The outbox keeps the work, and the next run after sign-in
    // sends it.
    const db = freshDatabase();

    await enqueue(db, { type: "CapturedVisit", subjectId: "a", payload: {} });

    const manager = startSync(db, () => null, DEVICE);
    const result = await manager.syncNow();

    expect(result.interrupted).toBe("unauthorized");
    expect(api.push).not.toHaveBeenCalled();
    expect((await pending(db)).length).toBe(1);

    manager.stop();
    db.close();
  });
});

describe("the assortment", () => {
  const OUTLET = "outlet-1";

  function pullWith(
    assortment: { upserts: unknown[]; tombstones: unknown[]; cursor: number },
    outletAssortment: { upserts: unknown[]; tombstones: unknown[]; cursor: number },
    outlets: { upserts: unknown[]; tombstones: unknown[]; cursor: number } = {
      upserts: [outletRow(OUTLET, 1)],
      tombstones: [],
      cursor: 1,
    },
  ) {
    return {
      changes: {
        outlets,
        journeys: { upserts: [], tombstones: [], cursor: 0 },
        configuration: { upserts: [], tombstones: [], cursor: 0 },
        products: { upserts: [], tombstones: [], cursor: 0 },
        assortment,
        outletAssortment,
        priceLists: { upserts: [], tombstones: [], cursor: 0 },
        priceLines: { upserts: [], tombstones: [], cursor: 0 },
        priceAssignments: { upserts: [], tombstones: [], cursor: 0 },
        promotions: { upserts: [], tombstones: [], cursor: 0 },
        promotionAssignments: { upserts: [], tombstones: [], cursor: 0 },
        surveys: { upserts: [], tombstones: [], cursor: 0 },
        scoreWeights: { upserts: [], tombstones: [], cursor: 0 },
        taxRates: { upserts: [], tombstones: [], cursor: 0 },
      },
      snapshotVersion: "outlets#1",
    };
  }

  function line(id: string, productId: string, rowVersion: number, isMustStock = false) {
    return { id, channelId: CHANNEL, productId, isMustStock, rowVersion };
  }

  function override(id: string, productId: string, kind: string, rowVersion: number) {
    return { id, outletId: OUTLET, productId, kind, isMustStock: false, rowVersion };
  }

  it("stores both halves under their own watermarks", async () => {
    const db = freshDatabase();

    api.pull.mockResolvedValue(
      pullWith(
        { upserts: [line("line-1", "product-1", 4)], tombstones: [], cursor: 4 },
        { upserts: [override("over-1", "product-2", "Added", 6)], tombstones: [], cursor: 6 },
      ),
    );

    await syncOnce(db, TOKEN, DEVICE);

    expect(await watermark(db, ASSORTMENT)).toBe(4);
    expect(await watermark(db, OUTLET_ASSORTMENT)).toBe(6);

    db.close();
  });

  it("resolves the channel list with the outlet's exceptions applied", async () => {
    // Computed on the device rather than sent resolved: PRD-02 stores overrides precisely so there
    // is no materialised per-outlet list to keep in step.
    const db = freshDatabase();

    api.pull.mockResolvedValue(
      pullWith(
        {
          upserts: [
            line("line-1", "kept", 4),
            line("line-2", "refused", 5),
            line("line-3", "must-stock", 6, true),
          ],
          tombstones: [],
          cursor: 6,
        },
        {
          upserts: [
            override("over-1", "refused", "Removed", 7),
            override("over-2", "local-speciality", "Added", 8),
          ],
          tombstones: [],
          cursor: 8,
        },
      ),
    );

    await syncOnce(db, TOKEN, DEVICE);

    const effective = await assortmentFor(db, OUTLET, CHANNEL);

    expect([...effective.keys()].sort()).toEqual(["kept", "local-speciality", "must-stock"]);
    expect(effective.get("must-stock")).toBe(true);
    expect(effective.has("refused")).toBe(false);

    db.close();
  });

  it("drops a line the tenant removed from the channel", async () => {
    // Setting a channel's assortment replaces it, so an ordinary edit deletes lines. Without
    // tombstones a device would accumulate the union of every list the channel has ever had.
    const db = freshDatabase();

    api.pull.mockResolvedValueOnce(
      pullWith(
        { upserts: [line("line-1", "product-1", 4)], tombstones: [], cursor: 4 },
        { upserts: [], tombstones: [], cursor: 0 },
      ),
    );
    await syncOnce(db, TOKEN, DEVICE);

    api.pull.mockResolvedValueOnce(
      pullWith(
        { upserts: [], tombstones: [{ id: "line-1", rowVersion: 9 }], cursor: 9 },
        { upserts: [], tombstones: [], cursor: 0 },
      ),
    );
    await syncOnce(db, TOKEN, DEVICE);

    expect((await assortmentFor(db, OUTLET, CHANNEL)).size).toBe(0);

    db.close();
  });

  it("drops the overrides of an outlet that left the rep's territory", async () => {
    // The server sends no scope tombstone for these. The device already knows the outlet is gone —
    // it was tombstoned — and an override is meaningless without the outlet it qualifies.
    const db = freshDatabase();

    api.pull.mockResolvedValueOnce(
      pullWith(
        { upserts: [], tombstones: [], cursor: 0 },
        { upserts: [override("over-1", "product-1", "Added", 6)], tombstones: [], cursor: 6 },
      ),
    );
    await syncOnce(db, TOKEN, DEVICE);
    expect(await db.assortmentOverrides.count()).toBe(1);

    api.pull.mockResolvedValueOnce(
      pullWith(
        { upserts: [], tombstones: [], cursor: 0 },
        { upserts: [], tombstones: [], cursor: 6 },
        { upserts: [], tombstones: [{ id: OUTLET, rowVersion: 2 }], cursor: 2 },
      ),
    );
    await syncOnce(db, TOKEN, DEVICE);

    expect(await db.assortmentOverrides.count()).toBe(0);

    db.close();
  });

  it("keeps overrides for outlets the device still holds", async () => {
    // The prune runs after every pull, so it has to be precise about what it deletes — an
    // over-eager version would quietly strip a shop's exceptions on an ordinary sync.
    const db = freshDatabase();

    api.pull.mockResolvedValue(
      pullWith(
        { upserts: [], tombstones: [], cursor: 0 },
        { upserts: [override("over-1", "product-1", "Added", 6)], tombstones: [], cursor: 6 },
      ),
    );

    await syncOnce(db, TOKEN, DEVICE);
    await syncOnce(db, TOKEN, DEVICE);

    expect(await db.assortmentOverrides.count()).toBe(1);

    db.close();
  });
});

describe("prices", () => {
  const OUTLET = "outlet-1";
  const LIST = "list-1";

  function pricePull(
    priceLists: { upserts: unknown[]; tombstones: unknown[]; cursor: number },
    priceLines: { upserts: unknown[]; tombstones: unknown[]; cursor: number },
    priceAssignments: { upserts: unknown[]; tombstones: unknown[]; cursor: number },
    outlets: { upserts: unknown[]; tombstones: unknown[]; cursor: number } = {
      upserts: [outletRow(OUTLET, 1)],
      tombstones: [],
      cursor: 1,
    },
  ) {
    return {
      changes: {
        outlets,
        journeys: { upserts: [], tombstones: [], cursor: 0 },
        configuration: { upserts: [], tombstones: [], cursor: 0 },
        products: { upserts: [], tombstones: [], cursor: 0 },
        assortment: { upserts: [], tombstones: [], cursor: 0 },
        outletAssortment: { upserts: [], tombstones: [], cursor: 0 },
        priceLists,
        priceLines,
        priceAssignments,
        promotions: { upserts: [], tombstones: [], cursor: 0 },
        promotionAssignments: { upserts: [], tombstones: [], cursor: 0 },
        surveys: { upserts: [], tombstones: [], cursor: 0 },
        scoreWeights: { upserts: [], tombstones: [], cursor: 0 },
        taxRates: { upserts: [], tombstones: [], cursor: 0 },
      },
      snapshotVersion: "outlets#1",
    };
  }

  function list(id: string, from: string, to: string | null, rowVersion: number) {
    return { id, name: id, currency: "RON", effectiveFrom: from, effectiveTo: to, rowVersion };
  }

  function assignment(id: string, priceListId: string, scope: "outlet" | "channel", rowVersion: number) {
    return {
      id,
      priceListId,
      channelId: scope === "channel" ? CHANNEL : null,
      outletId: scope === "outlet" ? OUTLET : null,
      rowVersion,
    };
  }

  it("stores all three shapes under their own watermarks", async () => {
    const db = freshDatabase();

    api.pull.mockResolvedValue(
      pricePull(
        { upserts: [list(LIST, "2026-01-01", null, 4)], tombstones: [], cursor: 4 },
        {
          upserts: [{ id: "line-1", priceListId: LIST, productId: "product-1", amount: 12.5, rowVersion: 5 }],
          tombstones: [],
          cursor: 5,
        },
        { upserts: [assignment("assign-1", LIST, "channel", 6)], tombstones: [], cursor: 6 },
      ),
    );

    await syncOnce(db, TOKEN, DEVICE);

    expect(await watermark(db, PRICE_LISTS)).toBe(4);
    expect(await watermark(db, PRICE_LINES)).toBe(5);
    expect(await watermark(db, PRICE_ASSIGNMENTS)).toBe(6);

    db.close();
  });

  it("prefers the outlet's own list over its channel's", async () => {
    // BR-PRD-2's precedence, re-expressed on the device because it prices an order with no
    // connection. The parity suite is what keeps this from drifting from the server's resolver.
    const db = freshDatabase();

    api.pull.mockResolvedValue(
      pricePull(
        {
          upserts: [list("channel-list", "2026-01-01", null, 4), list("outlet-list", "2026-01-01", null, 5)],
          tombstones: [],
          cursor: 5,
        },
        { upserts: [], tombstones: [], cursor: 0 },
        {
          upserts: [
            assignment("assign-channel", "channel-list", "channel", 6),
            assignment("assign-outlet", "outlet-list", "outlet", 7),
          ],
          tombstones: [],
          cursor: 7,
        },
      ),
    );

    await syncOnce(db, TOKEN, DEVICE);

    expect((await priceListFor(db, OUTLET, CHANNEL, "2026-06-01"))?.id).toBe("outlet-list");

    db.close();
  });

  it("picks the list in effect on the day it is asked about, not the day it synced", async () => {
    // A device offline for a week may be pricing an order on the day a new list takes over.
    const db = freshDatabase();

    api.pull.mockResolvedValue(
      pricePull(
        {
          upserts: [
            list("old", "2026-01-01", "2026-05-31", 4),
            list("new", "2026-06-01", null, 5),
          ],
          tombstones: [],
          cursor: 5,
        },
        { upserts: [], tombstones: [], cursor: 0 },
        {
          upserts: [
            assignment("assign-old", "old", "channel", 6),
            assignment("assign-new", "new", "channel", 7),
          ],
          tombstones: [],
          cursor: 7,
        },
      ),
    );

    await syncOnce(db, TOKEN, DEVICE);

    expect((await priceListFor(db, OUTLET, CHANNEL, "2026-05-15"))?.id).toBe("old");
    expect((await priceListFor(db, OUTLET, CHANNEL, "2026-06-15"))?.id).toBe("new");
    expect(await priceListFor(db, OUTLET, CHANNEL, "2025-12-31")).toBeUndefined();

    db.close();
  });

  it("finds a product's price on a list, and nothing for one it does not price", async () => {
    const db = freshDatabase();

    api.pull.mockResolvedValue(
      pricePull(
        { upserts: [list(LIST, "2026-01-01", null, 4)], tombstones: [], cursor: 4 },
        {
          upserts: [{ id: "line-1", priceListId: LIST, productId: "product-1", amount: 12.5, rowVersion: 5 }],
          tombstones: [],
          cursor: 5,
        },
        { upserts: [], tombstones: [], cursor: 0 },
      ),
    );

    await syncOnce(db, TOKEN, DEVICE);

    expect((await priceOf(db, LIST, "product-1"))?.amount).toBe(12.5);
    expect(await priceOf(db, LIST, "product-2")).toBeUndefined();

    db.close();
  });

  it("drops an outlet assignment when the outlet leaves the territory, and keeps the channel one", async () => {
    // The same cascade the overrides get — and the same trap: an over-eager prune would take the
    // channel assignment with it, and every shop in that channel would lose its price.
    const db = freshDatabase();

    api.pull.mockResolvedValueOnce(
      pricePull(
        { upserts: [], tombstones: [], cursor: 0 },
        { upserts: [], tombstones: [], cursor: 0 },
        {
          upserts: [
            assignment("assign-outlet", LIST, "outlet", 6),
            assignment("assign-channel", LIST, "channel", 7),
          ],
          tombstones: [],
          cursor: 7,
        },
      ),
    );
    await syncOnce(db, TOKEN, DEVICE);
    expect(await db.priceAssignments.count()).toBe(2);

    api.pull.mockResolvedValueOnce(
      pricePull(
        { upserts: [], tombstones: [], cursor: 0 },
        { upserts: [], tombstones: [], cursor: 0 },
        { upserts: [], tombstones: [], cursor: 7 },
        { upserts: [], tombstones: [{ id: OUTLET, rowVersion: 2 }], cursor: 2 },
      ),
    );
    await syncOnce(db, TOKEN, DEVICE);

    const remaining = await db.priceAssignments.toArray();
    expect(remaining).toHaveLength(1);
    expect(remaining[0].id).toBe("assign-channel");

    db.close();
  });
});

describe("promotions", () => {
  const OUTLET = "outlet-1";

  function promotionPull(
    promotions: { upserts: unknown[]; tombstones: unknown[]; cursor: number },
    promotionAssignments: { upserts: unknown[]; tombstones: unknown[]; cursor: number },
    outlets: { upserts: unknown[]; tombstones: unknown[]; cursor: number } = {
      upserts: [outletRow(OUTLET, 1)],
      tombstones: [],
      cursor: 1,
    },
  ) {
    return {
      changes: {
        outlets,
        journeys: { upserts: [], tombstones: [], cursor: 0 },
        configuration: { upserts: [], tombstones: [], cursor: 0 },
        products: { upserts: [], tombstones: [], cursor: 0 },
        assortment: { upserts: [], tombstones: [], cursor: 0 },
        outletAssortment: { upserts: [], tombstones: [], cursor: 0 },
        priceLists: { upserts: [], tombstones: [], cursor: 0 },
        priceLines: { upserts: [], tombstones: [], cursor: 0 },
        priceAssignments: { upserts: [], tombstones: [], cursor: 0 },
        promotions,
        promotionAssignments,
        surveys: { upserts: [], tombstones: [], cursor: 0 },
        scoreWeights: { upserts: [], tombstones: [], cursor: 0 },
        taxRates: { upserts: [], tombstones: [], cursor: 0 },
      },
      snapshotVersion: "outlets#1",
    };
  }

  function promotion(
    id: string,
    rowVersion: number,
    options: { priority?: number; from?: string; to?: string | null; tiers?: unknown[] } = {},
  ) {
    return {
      id,
      name: id,
      type: "VolumeTiered",
      percentOff: null,
      amountOff: null,
      currency: null,
      buyQuantity: null,
      getQuantity: null,
      getPercentOff: null,
      getProductId: null,
      validFrom: options.from ?? "2026-01-01",
      validTo: options.to ?? null,
      priority: options.priority ?? 0,
      targets: [],
      tiers: options.tiers ?? [
        { minQuantity: 6, percentOff: 5, amountOff: null, currency: null },
        { minQuantity: 12, percentOff: 10, amountOff: null, currency: null },
      ],
      rowVersion,
    };
  }

  function assignment(id: string, promotionId: string, scope: "outlet" | "channel", rowVersion: number) {
    return {
      id,
      promotionId,
      channelId: scope === "channel" ? CHANNEL : null,
      outletId: scope === "outlet" ? OUTLET : null,
      rowVersion,
    };
  }

  it("stores a promotion whole, with every tier", async () => {
    // The reason the aggregate travels as one row: a device holding four of five tiers does not
    // fail, it computes a *different discount*, and neither the rep nor the shop can tell.
    const db = freshDatabase();

    api.pull.mockResolvedValue(
      promotionPull(
        { upserts: [promotion("promo-1", 4)], tombstones: [], cursor: 4 },
        { upserts: [assignment("assign-1", "promo-1", "channel", 5)], tombstones: [], cursor: 5 },
      ),
    );

    await syncOnce(db, TOKEN, DEVICE);

    const stored = await db.promotions.get("promo-1");

    expect(stored?.tiers).toHaveLength(2);
    expect(stored?.tiers[1]).toMatchObject({ minQuantity: 12, percentOff: 10 });
    expect(await watermark(db, PROMOTIONS)).toBe(4);
    expect(await watermark(db, PROMOTION_ASSIGNMENTS)).toBe(5);

    db.close();
  });

  it("returns the outlet's and its channel's promotions, highest priority first", async () => {
    // Unlike prices, where the outlet's assignment *replaces* the channel's. A promotion is an
    // offer, and offers accumulate until the resolver decides which ones stack.
    const db = freshDatabase();

    api.pull.mockResolvedValue(
      promotionPull(
        {
          upserts: [
            promotion("low", 4, { priority: 1 }),
            promotion("high", 5, { priority: 9 }),
          ],
          tombstones: [],
          cursor: 5,
        },
        {
          upserts: [
            assignment("assign-low", "low", "channel", 6),
            assignment("assign-high", "high", "outlet", 7),
          ],
          tombstones: [],
          cursor: 7,
        },
      ),
    );

    await syncOnce(db, TOKEN, DEVICE);

    const running = await promotionsFor(db, OUTLET, CHANNEL, "2026-06-01");

    expect(running.map((row) => row.id)).toEqual(["high", "low"]);

    db.close();
  });

  it("answers for the order's date, not today's", async () => {
    // Expired promotions are held rather than filtered out of the pull, so a device pricing an
    // order dated last Tuesday gets the promotion that was running last Tuesday.
    const db = freshDatabase();

    api.pull.mockResolvedValue(
      promotionPull(
        {
          upserts: [promotion("spring", 4, { from: "2026-03-01", to: "2026-05-31" })],
          tombstones: [],
          cursor: 4,
        },
        { upserts: [assignment("assign-1", "spring", "channel", 5)], tombstones: [], cursor: 5 },
      ),
    );

    await syncOnce(db, TOKEN, DEVICE);

    expect((await promotionsFor(db, OUTLET, CHANNEL, "2026-04-15")).map((row) => row.id)).toEqual([
      "spring",
    ]);
    expect(await promotionsFor(db, OUTLET, CHANNEL, "2026-06-15")).toEqual([]);

    db.close();
  });

  it("counts a promotion once when both the outlet and its channel are assigned it", async () => {
    // Both assignment rows are legitimate, and a resolver handed the same promotion twice would
    // apply it twice.
    const db = freshDatabase();

    api.pull.mockResolvedValue(
      promotionPull(
        { upserts: [promotion("promo-1", 4)], tombstones: [], cursor: 4 },
        {
          upserts: [
            assignment("assign-channel", "promo-1", "channel", 5),
            assignment("assign-outlet", "promo-1", "outlet", 6),
          ],
          tombstones: [],
          cursor: 6,
        },
      ),
    );

    await syncOnce(db, TOKEN, DEVICE);

    expect(await promotionsFor(db, OUTLET, CHANNEL, "2026-06-01")).toHaveLength(1);

    db.close();
  });

  it("drops an outlet assignment when the outlet leaves, and keeps the channel one", async () => {
    const db = freshDatabase();

    api.pull.mockResolvedValueOnce(
      promotionPull(
        { upserts: [promotion("promo-1", 4)], tombstones: [], cursor: 4 },
        {
          upserts: [
            assignment("assign-outlet", "promo-1", "outlet", 5),
            assignment("assign-channel", "promo-1", "channel", 6),
          ],
          tombstones: [],
          cursor: 6,
        },
      ),
    );
    await syncOnce(db, TOKEN, DEVICE);
    expect(await db.promotionAssignments.count()).toBe(2);

    api.pull.mockResolvedValueOnce(
      promotionPull(
        { upserts: [], tombstones: [], cursor: 4 },
        { upserts: [], tombstones: [], cursor: 6 },
        { upserts: [], tombstones: [{ id: OUTLET, rowVersion: 2 }], cursor: 2 },
      ),
    );
    await syncOnce(db, TOKEN, DEVICE);

    const remaining = await db.promotionAssignments.toArray();
    expect(remaining).toHaveLength(1);
    expect(remaining[0].id).toBe("assign-channel");

    db.close();
  });
});


describe("tax rates", () => {
  function taxPull(taxRates: { upserts: unknown[]; tombstones: unknown[]; cursor: number }) {
    return {
      changes: {
        outlets: { upserts: [], tombstones: [], cursor: 0 },
        journeys: { upserts: [], tombstones: [], cursor: 0 },
        configuration: { upserts: [], tombstones: [], cursor: 0 },
        products: { upserts: [], tombstones: [], cursor: 0 },
        assortment: { upserts: [], tombstones: [], cursor: 0 },
        outletAssortment: { upserts: [], tombstones: [], cursor: 0 },
        priceLists: { upserts: [], tombstones: [], cursor: 0 },
        priceLines: { upserts: [], tombstones: [], cursor: 0 },
        priceAssignments: { upserts: [], tombstones: [], cursor: 0 },
        promotions: { upserts: [], tombstones: [], cursor: 0 },
        promotionAssignments: { upserts: [], tombstones: [], cursor: 0 },
        surveys: { upserts: [], tombstones: [], cursor: 0 },
        scoreWeights: { upserts: [], tombstones: [], cursor: 0 },
        taxRates,
      },
      snapshotVersion: "outlets#0",
    };
  }

  function rate(id: string, percentage: string, rowVersion: number) {
    return {
      id,
      taxClassId: "standard",
      countryCode: "RO",
      percentage,
      effectiveFrom: "2026-01-01",
      effectiveTo: null,
      rowVersion,
    };
  }

  it("stores what the pull carried, under its own watermark", async () => {
    /*
     * The wiring test, and it exists because the feed and the store can both be right while nothing
     * joins them. A missing line in the manager is silent: the pull succeeds, every other store
     * fills, and the device simply never has a rate — which `priceLine` reads as *unknown* and
     * charges nothing for. The rep sees a plausible net total and the server's recomputation exceeds
     * it by exactly the tax, on every order.
     */
    const db = freshDatabase();

    api.pull.mockResolvedValue(
      taxPull({ upserts: [rate("r1", "19.00", 12)], tombstones: [], cursor: 12 }),
    );

    await syncOnce(db, TOKEN, DEVICE);

    expect((await taxRatesFor(db, "RO", "standard")).map((each) => each.id)).toEqual(["r1"]);
    expect(await watermark(db, TAX_RATES)).toBe(12);

    db.close();
  });

  it("asks for changes since the watermark it already holds", async () => {
    // The delta half. Sending zero every run would re-download a tenant's whole rate table on a
    // connection the rep may not have, and the cursor exists precisely so it does not.
    const db = freshDatabase();

    api.pull.mockResolvedValue(
      taxPull({ upserts: [rate("r1", "19.00", 12)], tombstones: [], cursor: 12 }),
    );

    await syncOnce(db, TOKEN, DEVICE);
    await syncOnce(db, TOKEN, DEVICE);

    expect(api.pull.mock.calls[1][2].taxRates).toBe(12);

    db.close();
  });
});
