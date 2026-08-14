import "fake-indexeddb/auto";

import { afterEach, beforeEach, describe, expect, it } from "vitest";

import {
  closeDatabase,
  FieldKitDatabase,
  type ReferenceOrderMinimum,
  type ReferenceOutlet,
} from "@/lib/sync/db";
import { applyOrderMinimumChanges, ORDER_MINIMUMS, orderMinimumFor } from "@/lib/sync/reference";

/**
 * Order minimums as the device holds and reads them (`ORD-06`, `OFF-03`) — W11 slice 8b-ii.
 *
 * The pure rule has its own suite (`lib/pricing/order-minimum.test.ts`). What is only visible from
 * here is the **join**: that a page of the feed lands in the right table, that a withdrawn minimum
 * stops applying, and that the two scopes are gathered from two indexes into one ranked answer.
 */
const SHOP: ReferenceOutlet = {
  id: "outlet-1",
  code: "RO-BUC-0001",
  name: "Mega Image Dorobanți",
  channelId: "channel-1",
  segment: "A",
  status: "Active",
  countryCode: "RO",
  latitude: null,
  longitude: null,
  timeZoneId: "Europe/Bucharest",
  radiusMetres: 150,
  rowVersion: 4,
};

function minimum(overrides: Partial<ReferenceOrderMinimum> = {}): ReferenceOrderMinimum {
  return {
    id: "min-channel",
    channelId: "channel-1",
    outletId: null,
    amount: "150.00",
    currencyCode: "RON",
    rowVersion: 1,
    ...overrides,
  };
}

let db: FieldKitDatabase;

beforeEach(async () => {
  db = new FieldKitDatabase(`minimums:${crypto.randomUUID()}`);
  await db.outlets.add(SHOP);
});

afterEach(async () => {
  await db.delete();
  closeDatabase();
});

describe("order minimums on the device", () => {
  it("has none before the first pull, which lets every order through", async () => {
    /*
     * The state a freshly-installed device is in, and the reason it must not read as a threshold of
     * zero or as "refuse everything": a rep whose tenant has never configured a minimum would
     * otherwise be blocked from sending anything until a sync landed.
     */
    expect(await orderMinimumFor(db, "outlet-1")).toBeNull();
    expect(await db.orderMinimums.count()).toBe(0);
  });

  it("applies a channel minimum to a shop in that channel, and moves the watermark", async () => {
    await applyOrderMinimumChanges(db, {
      upserts: [minimum()],
      tombstones: [],
      cursor: 7,
    });

    const resolved = await orderMinimumFor(db, "outlet-1");

    expect(resolved?.orderMinimumId).toBe("min-channel");
    expect(resolved?.scope).toBe("Channel");
    expect(resolved?.amount).toBe("150.00");
    expect(resolved?.currencyCode).toBe("RON");

    // The watermark and the row are one transaction (`apply`), so asserting it here is asserting
    // that the next pull will ask for the delta rather than the whole set again.
    expect((await db.watermarks.get(ORDER_MINIMUMS))?.cursor).toBe(7);
  });

  it("prefers the shop's own minimum over its channel's", async () => {
    await applyOrderMinimumChanges(db, {
      upserts: [
        minimum({ id: "z-channel", amount: "500.00" }),
        minimum({ id: "a-outlet", channelId: null, outletId: "outlet-1", amount: "50.00" }),
      ],
      tombstones: [],
      cursor: 9,
    });

    const resolved = await orderMinimumFor(db, "outlet-1");

    // The ids sort the other way, so a lookup that gathered only one index — or ranked by id alone —
    // would answer 500 and read as a passing precedence test.
    expect(resolved?.orderMinimumId).toBe("a-outlet");
    expect(resolved?.scope).toBe("Outlet");
  });

  it("ignores a minimum scoped to another channel or another shop", async () => {
    await applyOrderMinimumChanges(db, {
      upserts: [
        minimum({ id: "other-channel", channelId: "channel-2" }),
        minimum({ id: "other-outlet", channelId: null, outletId: "outlet-2" }),
      ],
      tombstones: [],
      cursor: 3,
    });

    expect(await orderMinimumFor(db, "outlet-1")).toBeNull();
  });

  it("stops applying a minimum the tenant withdrew", async () => {
    /*
     * The worst failure this feed has, checked at the end that matters. The authoring PUT replaces
     * the whole set, so every edit is a delete-and-recreate — a device that applied only upserts
     * would go on refusing orders against a threshold nobody can see any more, silently, and looking
     * exactly like the rule working.
     */
    await applyOrderMinimumChanges(db, { upserts: [minimum()], tombstones: [], cursor: 4 });

    expect(await orderMinimumFor(db, "outlet-1")).not.toBeNull();

    await applyOrderMinimumChanges(db, {
      upserts: [],
      tombstones: [{ id: "min-channel", rowVersion: 5 }],
      cursor: 5,
    });

    expect(await orderMinimumFor(db, "outlet-1")).toBeNull();
  });

  it("holds the replacement alone when one figure is corrected", async () => {
    // The ordinary edit, which arrives as a tombstone *and* a new row with a new id. Applying only
    // the upsert would leave both, and the tiebreak would then pick whichever id sorted higher.
    await applyOrderMinimumChanges(db, { upserts: [minimum()], tombstones: [], cursor: 4 });

    await applyOrderMinimumChanges(db, {
      upserts: [minimum({ id: "min-channel-2", amount: "200.00", rowVersion: 6 })],
      tombstones: [{ id: "min-channel", rowVersion: 5 }],
      cursor: 6,
    });

    expect(await db.orderMinimums.count()).toBe(1);
    expect((await orderMinimumFor(db, "outlet-1"))?.amount).toBe("200.00");
  });

  it("answers null for an outlet this device does not hold", async () => {
    await applyOrderMinimumChanges(db, { upserts: [minimum()], tombstones: [], cursor: 4 });

    expect(await orderMinimumFor(db, "outlet-somewhere-else")).toBeNull();
  });
});
