import "fake-indexeddb/auto";

import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";

import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { closeDatabase, openDatabase, type FieldKitDatabase } from "@/lib/sync/db";
import { syncOnce } from "@/lib/sync/manager";
import { enqueue } from "@/lib/sync/outbox";

/**
 * The TypeScript half of the shared `/sync/push` wire vectors (`OFF-04`, sync engine §4) — W9
 * slice 12.
 *
 * <b>This is the test that would have caught what shipped.</b> Every other test of the push path
 * mocks `@/lib/api/sync` and asserts that `push` was *called* — never with what. So the client sent
 * every payload under `visit` for a whole slice, with a green suite, and the server refused each one
 * with a 400 that a device then retried on every reconnect.
 *
 * The mock is still there; what changed is that its expectations come from a file the **server**
 * also reads, rather than from this file's own idea of the protocol. Neither language owns
 * `vectors/sync/push.v1.json`, which is the point of it living outside both.
 */
const VECTORS = fileURLToPath(new URL("../../../vectors/sync/push.v1.json", import.meta.url));

type Vector = {
  name: string;
  outbox?: { type: string; payload: unknown };
  wire: Record<string, unknown>;
  expected: { slot: string | null; bound: boolean };
};

const vectors: Vector[] = JSON.parse(readFileSync(VECTORS, "utf8")).mutations;

const api = vi.hoisted(() => ({ bindDevice: vi.fn(), pull: vi.fn(), push: vi.fn() }));

vi.mock("@/lib/api/sync", () => api);

const DEVICE = "0195e7c4-0000-7000-8000-00000000d001";
const TOKEN = "token";

/** An empty round, so the run reaches the push without the pull having anything to say. */
function nothingToPull() {
  const empty = { upserts: [], tombstones: [], cursor: 0 };

  return {
    changes: {
      outlets: empty,
      journeys: empty,
      configuration: empty,
      products: empty,
      assortment: empty,
      outletAssortment: empty,
      priceLists: empty,
      priceLines: empty,
      priceAssignments: empty,
      promotions: empty,
      promotionAssignments: empty,
      surveys: empty,
      scoreWeights: empty,
    },
    snapshotVersion: 1,
  };
}

let db: FieldKitDatabase;

beforeEach(async () => {
  api.push.mockReset();
  api.pull.mockResolvedValue(nothingToPull());

  db = openDatabase("fieldkit-dev", `wire-${crypto.randomUUID()}`);
  await db.meta.put({ key: "deviceId", value: DEVICE });
});

afterEach(async () => {
  await db.delete();
  closeDatabase();
});

describe("the shared push vectors", () => {
  it("are read from the same file the server reads", () => {
    // Guards the harness rather than the protocol: a path that silently resolved to nothing would
    // make every `it.each` below vacuous, and a suite of zero cases passes.
    expect(vectors.length).toBeGreaterThan(0);
    expect(vectors.some((vector) => vector.expected.slot === "notVisited")).toBe(true);

    // …and the newest slot, so a file that loaded but predated W10 slice 6 is caught here rather
    // than by every audit case quietly not running.
    expect(vectors.some((vector) => vector.expected.slot === "audit")).toBe(true);
  });

  it.each(vectors.filter((vector) => vector.outbox))(
    "puts $name on the wire exactly as the file says",
    async (vector) => {
      const entry = await enqueue(db, {
        type: vector.outbox!.type,
        subjectId: "subject",
        payload: vector.outbox!.payload,
      });

      api.push.mockResolvedValue({
        results: [{ mutationId: entry.mutationId, status: "accepted", reason: null, detail: null }],
      });

      await syncOnce(db, TOKEN, DEVICE);

      const [, , mutations] = api.push.mock.calls[0];

      /*
       * Compared against the vector with the mutation id substituted, because that one field is
       * minted per capture and cannot be in a committed file. Everything else — which property the
       * payload travels under, and every byte of the payload — is the file's.
       */
      expect(mutations).toEqual([{ ...vector.wire, mutationId: entry.mutationId }]);
    },
  );

  it.each(vectors.filter((vector) => vector.outbox))(
    "never sends $name under a property its type does not name",
    async (vector) => {
      // The failure mode stated directly, because the assertion above would also pass if the client
      // sent the right property *and* a stray one. A `visit: undefined` alongside `notVisited` is
      // invisible to `toEqual` and fatal to a server that requires the shape it declares.
      const entry = await enqueue(db, {
        type: vector.outbox!.type,
        subjectId: "subject",
        payload: vector.outbox!.payload,
      });

      api.push.mockResolvedValue({
        results: [{ mutationId: entry.mutationId, status: "accepted", reason: null, detail: null }],
      });

      await syncOnce(db, TOKEN, DEVICE);

      const [, , mutations] = api.push.mock.calls[0];
      const slots = Object.keys(mutations[0]).filter((key) =>
        ["visit", "notVisited", "rescheduled", "unplanned", "audit"].includes(key),
      );

      expect(slots).toEqual([vector.expected.slot]);
    },
  );
});
