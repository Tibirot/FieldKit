/**
 * @vitest-environment jsdom
 *
 * For `window`, which `startSync` touches on construction. Nothing here listens for `online`.
 */
import "fake-indexeddb/auto";

import { afterEach, describe, expect, it, vi } from "vitest";

import { FieldKitDatabase, type ReferenceOutlet } from "./db";
import { syncOnce } from "./manager";
import { enqueue, pending } from "./outbox";
import { outlets as storedOutlets, OUTLETS, watermark } from "./reference";

/**
 * The client half of slice 9's properties (`OFF-04`).
 *
 * `SyncPropertyTests` proves the *server* answers a replayed batch identically and resumes a pull
 * from any cursor. That says nothing about whether the device converges — the half that matters to a
 * rep, and the half where a bug looks like missing shops rather than an error.
 *
 * So this runs the real manager against a **model server**: a tiny in-memory implementation of the
 * protocol whose delta is a filter over a list. Deterministic and seeded by construction, the
 * position `VectorPropertyTests` set in W6 — every case below is a fixed shape, and changing one is
 * a diff somebody reviews rather than a seed nobody can reproduce.
 */
const api = vi.hoisted(() => ({
  bindDevice: vi.fn(),
  pull: vi.fn(),
  push: vi.fn(),
}));

vi.mock("@/lib/api/sync", () => api);

const DEVICE = "device-1";
const TOKEN = "token";

function freshDatabase(): FieldKitDatabase {
  return new FieldKitDatabase(`test:${crypto.randomUUID()}`);
}

function outletRow(id: string, rowVersion: number): ReferenceOutlet {
  return {
    id,
    code: `SHOP-${id}`,
    name: `Shop ${id}`,
    channelId: "11111111-1111-4111-8111-111111111111",
    segment: null,
    status: "Active",
    latitude: null,
    longitude: null,
    rowVersion,
  };
}

/** Everything but the outlets, which is all these properties are about. */
const QUIET = {
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
};

/**
 * A server that answers a pull the way the real one does: rows above the cursor, paged, with the
 * cursor of the page rather than of the table.
 */
function modelServer(rows: ReferenceOutlet[], pageSize: number) {
  const ordered = [...rows].sort((left, right) => left.rowVersion - right.rowVersion);

  return (cursor: number) => {
    const page = ordered.filter((row) => row.rowVersion > cursor).slice(0, pageSize);

    // The rule the whole delta rests on: the highest version *in this page*, never the table's.
    const after = page.length > 0 ? page[page.length - 1].rowVersion : cursor;

    return {
      changes: { outlets: { upserts: page, tombstones: [], cursor: after }, ...QUIET },
      snapshotVersion: `outlets#${after}`,
    };
  };
}

afterEach(() => {
  api.pull.mockReset();
  api.push.mockReset();
  vi.restoreAllMocks();
});

describe("a pull interrupted anywhere", () => {
  // rows, page size, and which attempt fails. The interesting combinations are the ones where the
  // failure lands mid-page-sequence rather than on the first or last request.
  it.each([
    [1, 1, 0],
    [3, 1, 0],
    [3, 1, 1],
    [3, 2, 1],
    [7, 2, 2],
    [7, 3, 1],
    [9, 4, 2],
  ])(
    "converges for %i rows at page size %i, failing attempt %i",
    async (rowCount, pageSize, failAt) => {
      const db = freshDatabase();
      const rows = Array.from({ length: rowCount }, (_, index) =>
        outletRow(`outlet-${index}`, index + 1),
      );
      const answer = modelServer(rows, pageSize);

      let attempt = 0;

      api.pull.mockImplementation(async (_token: string, _device: string, cursors: { outlets: number }) => {
        // The interruption. A rejected pull is a response that never arrived: the device neither
        // stores the rows nor advances its cursor, which is the whole reason it can retry at all.
        if (attempt++ === failAt) throw new TypeError("Failed to fetch");

        return answer(cursors.outlets ?? 0);
      });

      /*
       * Run until a *successful* run carries nothing, with a hard bound so a non-converging
       * protocol fails the test rather than hanging CI.
       *
       * "The watermark stopped moving" is the wrong stopping rule and was this test's first bug: an
       * interrupted run also leaves it where it was, so the loop declared victory on the failure it
       * was written to survive.
       */
      for (let run = 0; run < rowCount + 5; run++) {
        const result = await syncOnce(db, TOKEN, DEVICE);
        if (result.interrupted === undefined && result.pulled === 0) break;
      }

      const held = await storedOutlets(db);

      // No loss: every row the server has, the device has.
      expect(held.map((row) => row.id).sort()).toEqual(rows.map((row) => row.id).sort());

      // No duplication: IndexedDB keys by id, so this is really asserting the *cursor* never skipped
      // — a device that lost a page would be short, and one that re-fetched forever would not settle.
      expect(held).toHaveLength(rowCount);
      expect(await watermark(db, OUTLETS)).toBe(rowCount);

      db.close();
    },
  );

  it("stops asking once it has everything", async () => {
    // Convergence, asserted separately: a protocol that re-sent the world every time would satisfy
    // "no loss" forever and be useless.
    const db = freshDatabase();
    const rows = Array.from({ length: 5 }, (_, index) => outletRow(`outlet-${index}`, index + 1));
    const answer = modelServer(rows, 2);

    api.pull.mockImplementation(async (_t: string, _d: string, cursors: { outlets: number }) =>
      answer(cursors.outlets ?? 0),
    );

    for (let run = 0; run < 5; run++) await syncOnce(db, TOKEN, DEVICE);

    const calls = api.pull.mock.calls.length;

    await syncOnce(db, TOKEN, DEVICE);
    const settled = await syncOnce(db, TOKEN, DEVICE);

    expect(api.pull.mock.calls.length).toBe(calls + 2);
    expect(settled.pulled).toBe(0);
    expect(await watermark(db, OUTLETS)).toBe(5);

    db.close();
  });

  it("keeps what it had when a later page fails", async () => {
    // The property the transaction in `applyOutletChanges` exists for: a device that got two pages
    // and lost the third keeps two pages, not none and not one and a half.
    const db = freshDatabase();
    const rows = Array.from({ length: 6 }, (_, index) => outletRow(`outlet-${index}`, index + 1));
    const answer = modelServer(rows, 2);

    let attempt = 0;
    api.pull.mockImplementation(async (_t: string, _d: string, cursors: { outlets: number }) => {
      if (attempt++ === 2) throw new TypeError("Failed to fetch");
      return answer(cursors.outlets ?? 0);
    });

    await syncOnce(db, TOKEN, DEVICE);
    await syncOnce(db, TOKEN, DEVICE);

    const partial = await syncOnce(db, TOKEN, DEVICE);

    expect(partial.interrupted).toBe("offline");
    expect(await watermark(db, OUTLETS)).toBe(4);
    expect(await storedOutlets(db)).toHaveLength(4);

    db.close();
  });
});

describe("a batch replayed", () => {
  it.each([
    [1, 1],
    [3, 1],
    [3, 2],
    [5, 3],
  ])("drains %i mutations even when attempt %i is lost", async (count, failAt) => {
    // The client half of the replay property. The server's ledger makes a re-send free; this asserts
    // the device actually re-sends — that a lost response leaves the work `pending` rather than
    // stranded `inflight`, which is the failure that looks like syncing forever.
    const db = freshDatabase();

    for (let index = 0; index < count; index++) {
      await enqueue(db, { type: "CapturedVisit", subjectId: `visit-${index}`, payload: {} });
    }

    const seen = new Set<string>();
    let attempt = 0;

    api.pull.mockResolvedValue({
      changes: { outlets: { upserts: [], tombstones: [], cursor: 0 }, ...QUIET },
      snapshotVersion: "outlets#0",
    });

    api.push.mockImplementation(async (_t: string, _d: string, mutations: { mutationId: string }[]) => {
      // The server saw the batch either way — a lost *response* is indistinguishable from a lost
      // request to the device, and the harsher of the two is the one where the work landed.
      for (const mutation of mutations) seen.add(mutation.mutationId);

      if (attempt++ === failAt) throw new TypeError("Failed to fetch");

      return {
        results: mutations.map((mutation) => ({
          mutationId: mutation.mutationId,
          status: "accepted" as const,
          reason: null,
          detail: null,
        })),
      };
    });

    for (let run = 0; run < count + 3; run++) {
      const result = await syncOnce(db, TOKEN, DEVICE);
      if (result.interrupted === undefined && (await pending(db)).length === 0) break;
    }

    // Nothing stranded, nothing lost: the outbox is empty and every mutation reached the server.
    expect(await pending(db)).toEqual([]);
    expect(await db.outbox.count()).toBe(0);
    expect(seen.size).toBe(count);

    db.close();
  });

  it("re-sends under the same mutation ids, so the ledger can recognise them", async () => {
    // The property everything else rests on. If a retry minted new ids the server would have no way
    // to tell a re-send from new work, and a rep with a bad connection would end the day with five
    // copies of one visit.
    const db = freshDatabase();

    const entry = await enqueue(db, { type: "CapturedVisit", subjectId: "visit-1", payload: {} });
    const sent: string[] = [];
    let attempt = 0;

    api.pull.mockResolvedValue({
      changes: { outlets: { upserts: [], tombstones: [], cursor: 0 }, ...QUIET },
      snapshotVersion: "outlets#0",
    });

    api.push.mockImplementation(async (_t: string, _d: string, mutations: { mutationId: string }[]) => {
      sent.push(...mutations.map((mutation) => mutation.mutationId));

      if (attempt++ < 2) throw new TypeError("Failed to fetch");

      return {
        results: mutations.map((mutation) => ({
          mutationId: mutation.mutationId,
          status: "accepted" as const,
          reason: null,
          detail: null,
        })),
      };
    });

    for (let run = 0; run < 4; run++) await syncOnce(db, TOKEN, DEVICE);

    expect(sent).toEqual([entry.mutationId, entry.mutationId, entry.mutationId]);
    expect(await db.outbox.count()).toBe(0);

    db.close();
  });
});
