import "fake-indexeddb/auto";

import { afterEach, beforeEach, describe, expect, it } from "vitest";

import { draft, draftFor, order, putLine, removeLine, submit } from "@/lib/orders/local-order";
import { closeDatabase, openDatabase, type FieldKitDatabase } from "@/lib/sync/db";

/**
 * The device's order store (`ORD-05`, `OFF-01b`) — W11 slice 6.
 *
 * <b>What is asserted here is durability and arithmetic, not a screen.</b> Slice 7 renders this;
 * these are the properties that have to hold whether or not anything is rendering — a draft that
 * outlives the tab that made it, a total that agrees with the column above it, and a seal that puts
 * the order in the outbox in the same breath as marking it sent.
 */
const VISIT = "0195e7c4-0000-7000-8000-00000000b001";
const OUTLET = "0195e7c4-0000-7000-8000-00000000e001";
const PRODUCT = "0195e7c4-0000-7000-8000-00000000d001";
const OTHER = "0195e7c4-0000-7000-8000-00000000d002";

const NOW = new Date("2026-08-12T09:45:00.000Z");

let db: FieldKitDatabase;
let subject: string;

beforeEach(() => {
  subject = `orders-${crypto.randomUUID()}`;
  db = openDatabase("fieldkit-dev", subject);
});

afterEach(async () => {
  await db.delete();
  closeDatabase();
});

function start() {
  return draftFor(db, { visitId: VISIT, outletId: OUTLET, currencyCode: "RON", now: NOW });
}

function line(productId: string, quantity: string, lineTotal: string) {
  return {
    productId,
    quantity,
    unitOfMeasure: "case",
    packSize: 12,
    unitPrice: "4.50",
    lineTotal,
    now: NOW,
  };
}

describe("the device's order store", () => {
  it("starts one draft per visit and returns the same one afterwards", async () => {
    // B4 allows at most one order per visit, and the server's aggregate enforces it. A second draft
    // here would be the device inventing a conflict the push is guaranteed to refuse.
    const first = await start();
    const second = await start();

    expect(second.id).toBe(first.id);
    expect(await db.orders.count()).toBe(1);
  });

  it("keeps the draft after the database is closed and reopened", async () => {
    /*
     * `ORD-05` and `OFF-01b` in one assertion. A draft lives *only* on the device — the server has no
     * create-a-draft path (`B4`, `B7`) — so a reload that lost it would lose work that existed
     * nowhere else. Reopening by name is what a closed tab and a fresh launch both look like.
     */
    const started = await start();
    await putLine(db, started.id, line(PRODUCT, "6", "27.00"));

    closeDatabase();

    // The same tenant and subject, which is what a fresh launch resolves to — a different name would
    // be a different rep's database and would pass by finding nothing for the wrong reason.
    db = openDatabase("fieldkit-dev", subject);

    const found = await draft(db, VISIT);

    expect(found?.lines).toHaveLength(1);
    expect(found?.total).toBe("27");
  });

  it("replaces a line for a product already on the order rather than summing it", async () => {
    // A rep picking the same product twice has changed their mind about the quantity. Summing would
    // invent a number nobody typed, and the aggregate refuses a duplicate product outright.
    const started = await start();

    await putLine(db, started.id, line(PRODUCT, "6", "27.00"));
    const updated = await putLine(db, started.id, line(PRODUCT, "3", "13.50"));

    expect(updated?.lines).toHaveLength(1);
    expect(updated?.lines[0].quantity).toBe("3");
    expect(updated?.total).toBe("13.5");
  });

  it("totals by summing the stored line totals rather than re-deriving them", async () => {
    /*
     * `BR-PRD-9`: each line total is already rounded to the currency's minor units by the pricing
     * engine, and re-multiplying quantity by price here would round a second time. The one
     * arithmetic error a reader always notices is a total that disagrees with the column above it.
     */
    const started = await start();

    await putLine(db, started.id, line(PRODUCT, "6", "27.00"));
    const updated = await putLine(db, started.id, line(OTHER, "5", "31.50"));

    expect(updated?.total).toBe("58.5");
  });

  it("drops a line and brings the total back down", async () => {
    const started = await start();

    await putLine(db, started.id, line(PRODUCT, "6", "27.00"));
    await putLine(db, started.id, line(OTHER, "5", "31.50"));

    const updated = await removeLine(db, started.id, OTHER, NOW);

    expect(updated?.lines).toHaveLength(1);
    expect(updated?.total).toBe("27");
  });

  it("seals the order and queues it in one transaction", async () => {
    const started = await start();
    await putLine(db, started.id, line(PRODUCT, "6", "27.00"));

    const sealed = await submit(db, started.id, NOW);

    expect(sealed?.status).toBe("submitted");
    expect(sealed?.capturedAtUtc).toBe(NOW.toISOString());

    const queued = await db.outbox.toArray();

    expect(queued).toHaveLength(1);
    expect(queued[0].type).toBe("CapturedOrder");
    expect(queued[0].subjectId).toBe(started.id);
  });

  it("puts the order on the wire as numbers, not decimal strings", async () => {
    /*
     * The conversion this store exists to postpone, asserted where it happens.
     *
     * `CapturedOrderLine` takes bare `decimal` server-side and nothing configures
     * `AllowReadingFromString`, so a quoted "27.00" is a 400 — which fails the whole batch and is
     * retried on every reconnect forever rather than recorded and stopped. W11 slice 5's wire vector
     * pins the numeric form; this is the client half of the same statement.
     *
     * Everything before this point is a decimal string, so the value crosses `Number` exactly once,
     * already rounded — rather than an IEEE-754 float sitting between the rep's screen and the record.
     */
    const started = await start();
    await putLine(db, started.id, line(PRODUCT, "6", "27.00"));
    await submit(db, started.id, NOW);

    const [queued] = await db.outbox.toArray();
    const payload = queued.payload as Record<string, unknown>;

    expect(payload.total).toBe(27);
    expect(payload.visitId).toBe(VISIT);

    // …and no outlet: the server takes it from the visit, because a device that could name one could
    // name a different shop from the one the rep stood in.
    expect(payload).not.toHaveProperty("outletId");

    const lines = payload.lines as Record<string, unknown>[];

    expect(lines[0].quantity).toBe(6);
    expect(lines[0].unitPrice).toBe(4.5);
    expect(lines[0].lineTotal).toBe(27);
  });

  it("refuses to submit an order for nothing", async () => {
    // The aggregate refuses it too. Letting it reach the wire would cost a rep a round trip to be
    // told something the device already knew.
    const started = await start();

    expect(await submit(db, started.id, NOW)).toBeUndefined();
    expect(await db.outbox.count()).toBe(0);
  });

  it("refuses to change an order after it is submitted", async () => {
    // `BR-ORD-4`, on the device. The server enforces it too, and a store that let a rep keep typing
    // into a sealed order would build an edit the push is guaranteed to refuse.
    const started = await start();
    await putLine(db, started.id, line(PRODUCT, "6", "27.00"));
    await submit(db, started.id, NOW);

    expect(await putLine(db, started.id, line(OTHER, "1", "4.50"))).toBeUndefined();
    expect(await removeLine(db, started.id, PRODUCT, NOW)).toBeUndefined();

    const stored = await order(db, started.id);

    expect(stored?.lines).toHaveLength(1);
  });

  it("stops offering a submitted order as the visit's draft", async () => {
    const started = await start();
    await putLine(db, started.id, line(PRODUCT, "6", "27.00"));
    await submit(db, started.id, NOW);

    expect(await draft(db, VISIT)).toBeUndefined();
  });
});
