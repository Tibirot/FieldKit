import "fake-indexeddb/auto";

import { afterEach, beforeEach, describe, expect, it } from "vitest";

import type { OrderVerdict } from "@/lib/api/sync";
import { applyOrderVerdicts, ORDERS } from "@/lib/orders/verdicts";
import { closeDatabase, FieldKitDatabase, type LocalOrder } from "@/lib/sync/db";
import { watermark } from "@/lib/sync/reference";

/**
 * What the back office made of this device's orders (`BR-ORD-9`, `ORD-12`) — W12 F5b.
 *
 * <b>The half of regression F5 that closes the loop.</b> F5a put the verdict on the wire; until this
 * applied it, `LocalOrderStatus` had no `rejected` and the device's own store said why: a status it
 * could not keep true would be worse than one it does not have.
 */
let db: FieldKitDatabase;

function order(overrides: Partial<LocalOrder> = {}): LocalOrder {
  return {
    id: "order-1",
    visitId: "visit-1",
    outletId: "outlet-1",
    status: "submitted",
    rejection: null,
    currencyCode: "RON",
    total: "20.00",
    taxTotal: "3.80",
    capturedAgainst: null,
    lines: [
      {
        productId: "product-1",
        quantity: "2",
        unitOfMeasure: "unit",
        packSize: null,
        unitPrice: "10.00",
        lineTotal: "20.00",
        taxAmount: "3.80",
      },
    ],
    capturedAtUtc: "2026-08-14T09:45:00.000Z",
    updatedAtUtc: "2026-08-14T09:45:00.000Z",
    ...overrides,
  };
}

function verdict(overrides: Partial<OrderVerdict> = {}): OrderVerdict {
  return {
    orderId: "order-1",
    status: "Rejected",
    rejection: { reason: "OffAssortment", offendingProductId: "product-1", note: null },
    rowVersion: 7,
    ...overrides,
  };
}

function page(upserts: OrderVerdict[], cursor = 7) {
  return { upserts, tombstones: [], cursor };
}

beforeEach(() => {
  db = new FieldKitDatabase(`verdicts:${crypto.randomUUID()}`);
});

afterEach(async () => {
  await db.delete();
  closeDatabase();
});

describe("applying what the back office decided", () => {
  it("marks a refused order and keeps the reason beside it", async () => {
    await db.orders.add(order());

    await applyOrderVerdicts(db, page([verdict()]));

    const held = await db.orders.get("order-1");

    expect(held?.status).toBe("rejected");
    expect(held?.rejection).toEqual({
      reason: "OffAssortment",
      offendingProductId: "product-1",
      note: null,
    });
  });

  it("leaves the money exactly as the rep captured it", async () => {
    /*
     * <b>`BR-ORD-6` in one assertion.</b> The device's totals are what the rep and the shopkeeper
     * agreed; the server's arithmetic is an annotation beside them and never over them.
     *
     * There is nothing on a verdict that *could* overwrite these — that is F5a's design and this is
     * the device-side statement of it. If a later slice widens the wire, this fails.
     */
    await db.orders.add(order());

    await applyOrderVerdicts(db, page([verdict()]));

    const held = await db.orders.get("order-1");

    expect(held?.total).toBe("20.00");
    expect(held?.taxTotal).toBe("3.80");
    expect(held?.lines).toHaveLength(1);
    expect(held?.capturedAtUtc).toBe("2026-08-14T09:45:00.000Z");
  });

  it("clears the rejection when a correction is accepted", async () => {
    /*
     * The second half of `BR-ORD-9`, and the reason the feed carries `Submitted` at all. A device
     * that only ever learned about rejections would show *refused* against an order the back office
     * has since taken — forever, because a delta carries only what changed.
     */
    await db.orders.add(
      order({
        status: "rejected",
        rejection: { reason: "OffAssortment", offendingProductId: "product-1", note: null },
      }),
    );

    await applyOrderVerdicts(db, page([verdict({ status: "Submitted", rejection: null })]));

    const held = await db.orders.get("order-1");

    expect(held?.status).toBe("submitted");
    expect(held?.rejection).toBeNull();
  });

  it("skips an order this device does not hold, rather than inventing one", async () => {
    /*
     * An ordinary state, not an error: verdicts are scoped to the *person*, and a rep works two
     * phones or replaces one. A local order built from a verdict would have no lines and no total —
     * an order saying the rep sold nothing, which is worse than not showing it.
     */
    await applyOrderVerdicts(db, page([verdict({ orderId: "somebody-elses" })]));

    expect(await db.orders.count()).toBe(0);

    // And the cursor still moves, or the device asks for the same page forever.
    expect(await watermark(db, ORDERS)).toBe(7);
  });

  it("reads a status it has never heard of as 'not refused'", async () => {
    /*
     * A server that grows a seventh `OrderStatus` must not strand a rep's screen on a word this
     * build cannot render. *Not refused* is the safe reading: it leaves the order where it was and
     * raises no false alarm.
     */
    await db.orders.add(order());

    await applyOrderVerdicts(db, page([verdict({ status: "Dispatched", rejection: null })]));

    expect((await db.orders.get("order-1"))?.status).toBe("submitted");
  });

  it("does not rewrite a row the verdict agrees with", async () => {
    /*
     * The feed re-sends every order the rep has ever taken after a rebind, and every `liveQuery`
     * watching this table re-runs on a `put` — identical object or not. Cheap to skip, and the
     * alternative is a screen that flickers through a rep's whole history on one sync.
     */
    await db.orders.add(order({ updatedAtUtc: "2026-08-14T09:45:00.000Z" }));

    await applyOrderVerdicts(db, page([verdict({ status: "Submitted", rejection: null })]));

    expect((await db.orders.get("order-1"))?.updatedAtUtc).toBe("2026-08-14T09:45:00.000Z");
  });
});

describe("the watermark", () => {
  it("advances to the page's cursor", async () => {
    await applyOrderVerdicts(db, page([], 41));

    expect(await watermark(db, ORDERS)).toBe(41);
  });

  it("never moves backwards", async () => {
    // A retried or re-ordered response carrying an older cursor would re-send everything between
    // the two — a device that oscillates instead of converging. The same rule `reference.ts` states.
    await applyOrderVerdicts(db, page([], 41));
    await applyOrderVerdicts(db, page([], 12));

    expect(await watermark(db, ORDERS)).toBe(41);
  });
});
