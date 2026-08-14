import "fake-indexeddb/auto";

import { afterEach, beforeEach, describe, expect, it } from "vitest";

import {
  draft,
  draftFor,
  order,
  putLine,
  removeLine,
  reopen,
  submit,
} from "@/lib/orders/local-order";
import { closeDatabase, openDatabase, type FieldKitDatabase } from "@/lib/sync/db";
import {
  PRICE_ASSIGNMENTS,
  PRICE_LINES,
  PRICE_LISTS,
  PROMOTION_ASSIGNMENTS,
  PROMOTIONS,
  TAX_RATES,
} from "@/lib/sync/reference";

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

function line(productId: string, quantity: string, lineTotal: string, taxAmount = "0") {
  return {
    productId,
    quantity,
    unitOfMeasure: "case",
    packSize: 12,
    unitPrice: "4.50",
    lineTotal,
    taxAmount,
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

describe("what an order says it was priced against", () => {
  it("sends the tax the screen worked out, on the line and on the order", async () => {
    /*
     * <b>The field the wire was missing for three slices.</b> `lineTotal` is the net, so before
     * `taxAmount` existed the back office received every order short of its VAT — and the server's
     * recomputation, which includes tax, had nothing like-for-like to be compared against.
     */
    const started = await start();
    await putLine(db, started.id, line(PRODUCT, "6", "27.00", "5.13"));
    await putLine(db, started.id, line(OTHER, "5", "31.50", "5.99"));

    await submit(db, started.id, NOW);

    const payload = (await db.outbox.toArray())[0].payload as Record<string, unknown>;
    const lines = payload.lines as Record<string, unknown>[];

    expect(lines[0].taxAmount).toBe(5.13);
    expect(lines[1].taxAmount).toBe(5.99);

    // Summed the same way the net is: from the rounded lines, never re-derived from a rate.
    expect(payload.taxTotal).toBe(11.12);
  });

  it("records the watermarks it priced from, at the moment of the seal", async () => {
    /*
     * `ORD-08`. Six numbers rather than one, because pricing has six inputs that advance
     * independently — and the point of keeping them is that a server which disagrees can say *which*
     * one was stale rather than only that one was.
     */
    await db.watermarks.bulkPut([
      { entity: PRICE_LISTS, cursor: 41 },
      { entity: PRICE_LINES, cursor: 118 },
      { entity: PRICE_ASSIGNMENTS, cursor: 27 },
      { entity: PROMOTIONS, cursor: 9 },
      { entity: PROMOTION_ASSIGNMENTS, cursor: 14 },
      { entity: TAX_RATES, cursor: 6 },
    ]);

    const started = await start();
    await putLine(db, started.id, line(PRODUCT, "6", "27.00"));

    await submit(db, started.id, NOW);

    const payload = (await db.outbox.toArray())[0].payload as Record<string, unknown>;

    expect(payload.capturedAgainst).toEqual({
      priceLists: 41,
      priceLines: 118,
      priceAssignments: 27,
      promotions: 9,
      promotionAssignments: 14,
      taxRates: 6,
    });
  });

  it("reads the watermarks at the seal, not when the first line was added", async () => {
    /*
     * A rep can price a line, sync, and price another — which is an ordinary morning. The numbers
     * that travel have to be the ones from the moment they stopped editing, or the order would claim
     * to have been priced against data that arrived after some of it was.
     */
    await db.watermarks.put({ entity: PRICE_LINES, cursor: 100 });

    const started = await start();
    await putLine(db, started.id, line(PRODUCT, "6", "27.00"));

    // A sync lands between the line and the seal.
    await db.watermarks.put({ entity: PRICE_LINES, cursor: 175 });

    await submit(db, started.id, NOW);

    const payload = (await db.outbox.toArray())[0].payload as Record<string, unknown>;
    const against = payload.capturedAgainst as Record<string, number>;

    expect(against.priceLines).toBe(175);
  });

  it("says zero for an entity this device has never pulled", async () => {
    // A legitimate state — a tenant that has authored no promotions — and the reason these are not
    // nullable. "Never pulled" and "pulled an empty set" are the same thing to a price.
    const started = await start();
    await putLine(db, started.id, line(PRODUCT, "6", "27.00"));

    await submit(db, started.id, NOW);

    const payload = (await db.outbox.toArray())[0].payload as Record<string, unknown>;

    expect((payload.capturedAgainst as Record<string, number>).promotions).toBe(0);
  });
});

describe("an order the back office refused", () => {
  it("re-opens for editing, which is the one exception BR-ORD-4 names", async () => {
    /*
     * `BR-ORD-4` locks an order after submit — that lock is what keeps orders conflict-free on sync
     * (`B7`) — and the rule's own text carves out a server-rejected order. This is that carve-out.
     */
    const started = await start();
    await putLine(db, started.id, line(PRODUCT, "2", "20.00"));
    await submit(db, started.id, NOW);

    // As the pull feed would leave it.
    await db.orders.update(started.id, {
      status: "rejected",
      rejection: { reason: "OffAssortment", offendingProductId: PRODUCT, note: null },
    });

    const reopened = await reopen(db, started.id, NOW);

    expect(reopened?.status).toBe("draft");

    // And it is a draft to every reader, not just to this one — `draft()` is what the screen's
    // editing controls are keyed on.
    expect((await draft(db, VISIT))?.id).toBe(started.id);
  });

  it("keeps the reason while the rep is acting on it", async () => {
    /*
     * The rep is editing the order *because* of the rejection, and a screen that erased the reason
     * at the moment they started would take away the only thing naming the line to change. It goes
     * when the server says so — a correction returns `Submitted` with no rejection, and the verdict
     * clears it.
     */
    const started = await start();
    await putLine(db, started.id, line(PRODUCT, "2", "20.00"));
    await submit(db, started.id, NOW);
    await db.orders.update(started.id, {
      status: "rejected",
      rejection: { reason: "OffAssortment", offendingProductId: PRODUCT, note: "Delisted." },
    });

    expect((await reopen(db, started.id, NOW))?.rejection).toEqual({
      reason: "OffAssortment",
      offendingProductId: PRODUCT,
      note: "Delisted.",
    });
  });

  it("tells the server nothing until the rep resubmits", async () => {
    // Re-opening is a local act. A rep who opens it and thinks better of it has said nothing, which
    // is right: the order is still rejected until they send a correction.
    const started = await start();
    await putLine(db, started.id, line(PRODUCT, "2", "20.00"));
    await submit(db, started.id, NOW);
    await db.orders.update(started.id, { status: "rejected", rejection: null });

    const before = await db.outbox.count();
    await reopen(db, started.id, NOW);

    expect(await db.outbox.count()).toBe(before);
  });

  it("resubmits under a new mutation id, which is what BR-ORD-9 asks for", async () => {
    /*
     * <b>The whole loop, and the assertion the rule is written around.</b> The order keeps its
     * identity — so "how many orders did this outlet place" counts intent rather than attempts —
     * and the *submission* is new, so the original id stays terminal and the push stays idempotent.
     */
    const started = await start();
    await putLine(db, started.id, line(PRODUCT, "2", "20.00"));
    await submit(db, started.id, NOW);

    const first = (await db.outbox.toArray())[0].mutationId;

    await db.orders.update(started.id, { status: "rejected", rejection: null });
    await reopen(db, started.id, NOW);

    // The rep swaps the flagged line for one the shop may order.
    await removeLine(db, started.id, PRODUCT, NOW);
    await putLine(db, started.id, line(OTHER, "3", "27.00"));
    await submit(db, started.id, NOW);

    const queued = await db.outbox.toArray();

    // Two attempts, two rows: the first stays terminal and the second is the correction.
    expect(queued).toHaveLength(2);

    /*
     * Found by what it *says* rather than by position.
     *
     * Both submits run on the same fixed clock, so `createdAt` ties and any ordering over it is
     * arbitrary — which is how the first version of this compared a row against itself and failed
     * for a reason that had nothing to do with the rule under test.
     */
    const second = queued.find(
      (entry) => (entry.payload as { lines: { productId: string }[] }).lines[0].productId === OTHER,
    )!;

    expect(second.mutationId).not.toBe(first);
    expect(second.subjectId).toBe(started.id);
    expect((second.payload as { lines: { productId: string }[] }).lines).toEqual([
      expect.objectContaining({ productId: OTHER }),
    ]);
  });

  it("refuses to re-open an order nobody objected to", async () => {
    // The carve-out is exactly one status wide. Any wider and it is a path to editing a submitted
    // order, which is the lock `B7`'s conflict story rests on.
    const started = await start();
    await putLine(db, started.id, line(PRODUCT, "2", "20.00"));
    await submit(db, started.id, NOW);

    expect(await reopen(db, started.id, NOW)).toBeUndefined();
    expect((await order(db, started.id))?.status).toBe("submitted");
  });
});
