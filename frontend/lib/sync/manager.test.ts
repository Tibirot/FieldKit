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
  product,
  products,
  PRODUCTS,
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
    name,
    channelId: "11111111-1111-4111-8111-111111111111",
    segment: null,
    status: "Active",
    latitude: null,
    longitude: null,
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
