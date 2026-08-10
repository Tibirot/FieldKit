import "fake-indexeddb/auto";

import { afterEach, describe, expect, it, vi } from "vitest";

import {
  closeDatabase,
  databaseName,
  FieldKitDatabase,
  openDatabase,
  requestPersistentStorage,
} from "./db";
import {
  enqueue,
  markAccepted,
  markInflight,
  markRejected,
  pending,
  pendingCount,
  reclaimInflight,
  statusOf,
} from "./outbox";
import { applyOutletChanges, outlet, outlets, watermark, OUTLETS } from "./reference";

/** A database nobody else is using, so tests do not have to unpick each other's rows. */
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
    rowVersion,
  };
}

afterEach(() => {
  closeDatabase();
  vi.unstubAllGlobals();
});

describe("the database itself", () => {
  it("is named per tenant and per user, so one rep never reads another's territory", () => {
    // The client-side equivalent of the server's tenant filter: not a column somebody can forget to
    // filter on, but a different database that was never opened.
    expect(databaseName("acme", "subject-a")).not.toEqual(databaseName("acme", "subject-b"));
    expect(databaseName("acme", "subject-a")).not.toEqual(databaseName("globex", "subject-a"));
  });

  it("reuses one handle per user and drops the previous rep's on a switch", () => {
    const first = openDatabase("acme", "subject-a");

    expect(openDatabase("acme", "subject-a")).toBe(first);

    const second = openDatabase("acme", "subject-b");

    expect(second).not.toBe(first);
    expect(first.isOpen()).toBe(false);
  });
});

describe("the outbox", () => {
  it("is durable before it answers, and mints the id the retry depends on", async () => {
    const db = freshDatabase();

    const entry = await enqueue(db, { type: "CapturedVisit", subjectId: "visit-1", payload: { a: 1 } });

    // Read back through a *separate* connection to the same database, which is the closest a test
    // gets to "the tab was killed the instant enqueue resolved".
    const reopened = new FieldKitDatabase(db.name);
    const stored = await reopened.outbox.get(entry.mutationId);

    expect(stored).toMatchObject({ status: "pending", attempts: 0, subjectId: "visit-1" });
    expect(entry.mutationId).toMatch(/^[0-9a-f-]{36}$/);

    reopened.close();
    db.close();
  });

  it("hands work back in the order the rep did it", async () => {
    const db = freshDatabase();

    vi.spyOn(Date, "now").mockReturnValue(1_000);
    const first = await enqueue(db, { type: "CapturedVisit", subjectId: "a", payload: {} });
    vi.spyOn(Date, "now").mockReturnValue(2_000);
    const second = await enqueue(db, { type: "CapturedVisit", subjectId: "b", payload: {} });
    vi.restoreAllMocks();

    expect((await pending(db)).map((entry) => entry.mutationId)).toEqual([
      first.mutationId,
      second.mutationId,
    ]);

    // A partial drain leaves the tail, never a hole in the middle.
    expect((await pending(db, 1)).map((entry) => entry.mutationId)).toEqual([first.mutationId]);

    db.close();
  });

  it("counts the attempt when the mutation goes out, not when it comes back", async () => {
    // A push that never gets an answer is exactly the case a success-counted attempt cannot see:
    // the row would sit at zero attempts forever, looking untried.
    const db = freshDatabase();

    const entry = await enqueue(db, { type: "CapturedVisit", subjectId: "a", payload: {} });
    await markInflight(db, [entry.mutationId]);

    expect(await db.outbox.get(entry.mutationId)).toMatchObject({ status: "inflight", attempts: 1 });

    db.close();
  });

  it("returns work stranded in flight by a crash, and keeps the attempt count", async () => {
    const db = freshDatabase();

    const entry = await enqueue(db, { type: "CapturedVisit", subjectId: "a", payload: {} });
    await markInflight(db, [entry.mutationId]);

    // The tab dies here. Nothing will ever answer that request.
    const reclaimed = await reclaimInflight(db);

    expect(reclaimed).toBe(1);
    expect(await db.outbox.get(entry.mutationId)).toMatchObject({ status: "pending", attempts: 1 });
    expect(await pendingCount(db)).toBe(1);

    db.close();
  });

  it("deletes accepted work rather than keeping a record that it finished", async () => {
    const db = freshDatabase();

    const entry = await enqueue(db, { type: "CapturedVisit", subjectId: "visit-1", payload: {} });
    await markAccepted(db, [entry.mutationId]);

    expect(await db.outbox.count()).toBe(0);
    expect(await statusOf(db, "visit-1")).toBe("synced");

    db.close();
  });

  it("keeps a rejection with its reason, and stops offering it for retry", async () => {
    // The server refused it on its merits. Re-sending would get the same no, forever, on a
    // connection the rep is paying for — it needs a person (OFF-09).
    const db = freshDatabase();

    const entry = await enqueue(db, { type: "CapturedVisit", subjectId: "visit-1", payload: {} });
    await markInflight(db, [entry.mutationId]);
    await markRejected(db, entry.mutationId, "visit.ingest.outletUnknown", "No such outlet.");

    expect(await db.outbox.get(entry.mutationId)).toMatchObject({
      status: "failed",
      errorCode: "visit.ingest.outletUnknown",
    });
    expect(await pending(db)).toEqual([]);
    expect(await pendingCount(db)).toBe(0);
    expect(await statusOf(db, "visit-1")).toBe("failed");

    db.close();
  });

  it("reports a rejection ahead of anything still queued for the same entity", async () => {
    const db = freshDatabase();

    const rejected = await enqueue(db, { type: "CapturedVisit", subjectId: "visit-1", payload: {} });
    await enqueue(db, { type: "CapturedVisit", subjectId: "visit-1", payload: {} });
    await markRejected(db, rejected.mutationId, "visit.ingest.outletUnknown");

    // "Still syncing" would be a lie a rep only discovers when the day's work turns out to be gone.
    expect(await statusOf(db, "visit-1")).toBe("failed");

    db.close();
  });
});

describe("applying a pull", () => {
  it("stores the rows and the watermark, and answers reads from them", async () => {
    const db = freshDatabase();

    await applyOutletChanges(
      db,
      { upserts: [outletRow("outlet-1", 7, "Alpha"), outletRow("outlet-2", 8, "Beta")], tombstones: [], cursor: 8 },
      "outlets#8",
    );

    expect((await outlets(db)).map((row) => row.name)).toEqual(["Alpha", "Beta"]);
    expect(await watermark(db, OUTLETS)).toBe(8);
    expect(await db.meta.get("snapshotVersion")).toEqual({ key: "snapshotVersion", value: "outlets#8" });

    db.close();
  });

  it("drops what the server tombstoned", async () => {
    const db = freshDatabase();

    await applyOutletChanges(db, { upserts: [outletRow("outlet-1", 7)], tombstones: [], cursor: 7 });
    await applyOutletChanges(db, {
      upserts: [],
      tombstones: [{ id: "outlet-1", rowVersion: 9 }],
      cursor: 9,
    });

    expect(await outlet(db, "outlet-1")).toBeUndefined();
    expect(await watermark(db, OUTLETS)).toBe(9);

    db.close();
  });

  it("leaves nothing behind when the transaction fails — rows and watermark move together", async () => {
    // The property the whole module exists for. Written separately, a crash between them advances
    // the device past changes it never stored, and those outlets are gone until something unrelated
    // edits them. This provokes it by failing the write halfway.
    const db = freshDatabase();

    await applyOutletChanges(db, { upserts: [outletRow("outlet-1", 4)], tombstones: [], cursor: 4 });

    /*
     * The failure is injected at the *last* write, not the first, and that distinction is what
     * makes this test worth having. Failing the first one proves nothing — nothing else had run
     * yet, so the assertions below would hold with no transaction at all. Failing the last one
     * means the rows and the watermark are already written when it blows up, so they can only be
     * absent afterwards if something rolled them back.
     */
    const metaPut = vi
      .spyOn(db.meta, "put")
      .mockRejectedValueOnce(new Error("storage went away mid-write"));

    await expect(
      applyOutletChanges(
        db,
        { upserts: [outletRow("outlet-2", 11)], tombstones: [], cursor: 11 },
        "outlets#11",
      ),
    ).rejects.toThrow();

    metaPut.mockRestore();

    // Still at the old watermark holding the old row. Had the cursor landed alone, outlet-2 would
    // be unreachable forever: the next pull asks for changes *after* 11.
    expect(await watermark(db, OUTLETS)).toBe(4);
    expect(await outlet(db, "outlet-2")).toBeUndefined();
    expect(await outlet(db, "outlet-1")).toBeDefined();
    expect(await db.meta.get("snapshotVersion")).toBeUndefined();

    db.close();
  });

  it("never moves the watermark backwards", async () => {
    // A retried or reordered response carrying an older cursor would otherwise re-send everything
    // in between on the next pull — a device that oscillates instead of converging.
    const db = freshDatabase();

    await applyOutletChanges(db, { upserts: [], tombstones: [], cursor: 20 });
    await applyOutletChanges(db, { upserts: [], tombstones: [], cursor: 12 });

    expect(await watermark(db, OUTLETS)).toBe(20);

    db.close();
  });

  it("reports zero for an entity it has never pulled", async () => {
    // The same value the server reads as "I have nothing", so a fresh install and a device whose
    // store was evicted take one code path rather than two.
    const db = freshDatabase();

    expect(await watermark(db, OUTLETS)).toBe(0);

    db.close();
  });

  it("survives an app restart with its watermark intact", async () => {
    const db = freshDatabase();

    await applyOutletChanges(db, { upserts: [outletRow("outlet-1", 5)], tombstones: [], cursor: 5 });
    db.close();

    const restarted = new FieldKitDatabase(db.name);

    expect(await watermark(restarted, OUTLETS)).toBe(5);
    expect(await outlet(restarted, "outlet-1")).toBeDefined();

    restarted.close();
  });
});

describe("persistent storage", () => {
  it("asks once and reports what the browser said", async () => {
    const persist = vi.fn().mockResolvedValue(true);
    vi.stubGlobal("navigator", { storage: { persist, persisted: vi.fn().mockResolvedValue(false) } });

    expect(await requestPersistentStorage()).toBe(true);
    expect(persist).toHaveBeenCalledOnce();
  });

  it("does not ask again once the answer is yes", async () => {
    // Firefox prompts. A second prompt for permission already granted is a dialog a rep learns to
    // dismiss, which is how they end up dismissing the one that matters.
    const persist = vi.fn();
    vi.stubGlobal("navigator", { storage: { persist, persisted: vi.fn().mockResolvedValue(true) } });

    expect(await requestPersistentStorage()).toBe(true);
    expect(persist).not.toHaveBeenCalled();
  });

  it("reports false rather than throwing where the API does not exist", async () => {
    // Safari on older iOS, and any server-side render. Neither is an error — the store still works,
    // it is just evictable, and that is what OFF-11 surfaces.
    vi.stubGlobal("navigator", {});

    expect(await requestPersistentStorage()).toBe(false);
  });
});
