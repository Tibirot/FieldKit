import { Money } from "@/lib/pricing/money";
import type {
  FieldKitDatabase,
  LocalOrder,
  LocalOrderLine,
  LocalPricingSnapshot,
} from "@/lib/sync/db";
import {
  PRICE_ASSIGNMENTS,
  PRICE_LINES,
  PRICE_LISTS,
  PROMOTION_ASSIGNMENTS,
  PROMOTIONS,
  TAX_RATES,
  watermark,
} from "@/lib/sync/reference";

/**
 * The order a rep builds at a counter, and the moment they seal it (`ORD-01`, `ORD-05`, `ORD-07`) —
 * W11 slice 6.
 *
 * <b>This is the store, not the screen.</b> Slice 7 renders it; what lives here is the part that has
 * to be true whether or not anything is rendering — a draft that survives a reload, a closed tab and
 * an app update (`OFF-01b`, `OFF-13`), and a seal that puts the order in the outbox in the same
 * transaction that marks it submitted.
 *
 * <b>Why `Draft` is a device state and not a server one.</b> `B4` puts it here: an order is edited at
 * a counter with no signal and only leaves when the rep submits. The server has no create-a-draft
 * path — a second writer into a record whose whole conflict story rests on having one (`B7`) — so a
 * draft lost before submit is work that existed nowhere else. That is what `ORD-05` is really about,
 * and why the draft lives in a store of its own rather than as an unsent outbox payload.
 */

/** What a caller asks for. The device supplies the identity, the status and the clock. */
export type DraftRequest = {
  visitId: string;
  outletId: string;
  currencyCode: string;
  now: Date;
};

export type LineRequest = {
  productId: string;
  quantity: string;
  unitOfMeasure: string;
  packSize: number | null;
  unitPrice: string;
  lineTotal: string;
  /** The tax on top of it, which the screen already prices (`ORD-02`) — W11 slice 14. */
  taxAmount: string;
  now: Date;
};

/**
 * The draft for a visit, starting one if there is none (`ORD-05`).
 *
 * <b>At most one order per visit</b>, which is the aggregate's rule server-side and is enforced here
 * by making "start" mean "get or start". A rep at one counter on one call places one order; two would
 * be a double-tap far more often than an intention, and a second draft would be the device inventing
 * a conflict the server would then have to refuse.
 */
export async function draftFor(
  db: FieldKitDatabase,
  request: DraftRequest,
): Promise<LocalOrder> {
  return db.transaction("rw", db.orders, async () => {
    const existing = await db.orders.where("visitId").equals(request.visitId).first();
    if (existing) return existing;

    const draft: LocalOrder = {
      id: crypto.randomUUID(),
      visitId: request.visitId,
      outletId: request.outletId,
      status: "draft",
      currencyCode: request.currencyCode,
      total: "0",
      taxTotal: "0",
      capturedAgainst: null,
      lines: [],
      capturedAtUtc: null,
      updatedAtUtc: request.now.toISOString(),
    };

    await db.orders.add(draft);

    return draft;
  });
}

/** The draft for a visit, or undefined. Never a submitted one — see {@link order}. */
export function draft(
  db: FieldKitDatabase,
  visitId: string,
): Promise<LocalOrder | undefined> {
  return db.orders
    .where("visitId")
    .equals(visitId)
    .filter((candidate) => candidate.status === "draft")
    .first();
}

export function order(db: FieldKitDatabase, id: string): Promise<LocalOrder | undefined> {
  return db.orders.get(id);
}

/**
 * The visit's order whatever state it is in — draft, or sealed and queued (W11 slice 8a).
 *
 * <b>Separate from {@link draft} rather than a flag on it.</b> A caller asking for *the draft* is
 * asking what may still be edited, and that question has to keep answering `undefined` once the
 * order is submitted — it is what `BR-ORD-4`'s lock looks like from the store. A screen showing the
 * rep what they sent is asking a different question, and conflating the two is how a sealed order
 * ends up rendered with an Add button beside it.
 */
export function orderFor(
  db: FieldKitDatabase,
  visitId: string,
): Promise<LocalOrder | undefined> {
  return db.orders.where("visitId").equals(visitId).first();
}

/**
 * Adds a line, or replaces the one already naming that product.
 *
 * <b>Replaces rather than sums.</b> A rep who picks the same product twice has changed their mind
 * about the quantity, not asked for twice as much — and the aggregate refuses a duplicate product
 * outright, so a store that allowed one would build an order the server is guaranteed to reject.
 */
export async function putLine(
  db: FieldKitDatabase,
  orderId: string,
  request: LineRequest,
): Promise<LocalOrder | undefined> {
  return db.transaction("rw", db.orders, async () => {
    const current = await db.orders.get(orderId);
    if (!current || current.status !== "draft") return undefined;

    const line: LocalOrderLine = {
      productId: request.productId,
      quantity: request.quantity,
      unitOfMeasure: request.unitOfMeasure,
      packSize: request.packSize,
      unitPrice: request.unitPrice,
      lineTotal: request.lineTotal,
      taxAmount: request.taxAmount,
    };

    const lines = [
      ...current.lines.filter((existing) => existing.productId !== request.productId),
      line,
    ];

    return save(db, current, lines, request.now);
  });
}

export async function removeLine(
  db: FieldKitDatabase,
  orderId: string,
  productId: string,
  now: Date,
): Promise<LocalOrder | undefined> {
  return db.transaction("rw", db.orders, async () => {
    const current = await db.orders.get(orderId);
    if (!current || current.status !== "draft") return undefined;

    const lines = current.lines.filter((line) => line.productId !== productId);

    return save(db, current, lines, now);
  });
}

/**
 * Seals the draft and puts it in the outbox (`ORD-07`, `BR-ORD-4`).
 *
 * <b>One transaction, both writes.</b> The order becoming `submitted` and the mutation existing are
 * the same fact; splitting them leaves a window where a crash produces either an order the rep
 * believes was sent and never was, or a mutation for an order still showing as editable. The same
 * call `checkOut` makes, and for the same reason.
 *
 * <b>An empty order is refused here rather than at the server.</b> The aggregate refuses it too — an
 * order for nothing is not an order — and letting it reach the wire would cost a rep a round trip to
 * be told something the device already knew.
 */
export async function submit(
  db: FieldKitDatabase,
  orderId: string,
  now: Date,
): Promise<LocalOrder | undefined> {
  return db.transaction("rw", db.orders, db.outbox, db.watermarks, async () => {
    const current = await db.orders.get(orderId);
    if (!current || current.status !== "draft" || current.lines.length === 0) return undefined;

    const sealed: LocalOrder = {
      ...current,
      status: "submitted",
      capturedAtUtc: now.toISOString(),
      updatedAtUtc: now.toISOString(),
      /*
       * What the device had pulled at the moment the rep stopped editing (`ORD-08`).
       *
       * Read here rather than when the first line was added: a rep can price a line, sync, and price
       * another, and the numbers that travel are the ones from the seal. Inside the transaction, so
       * a sync landing mid-submit cannot move a cursor between the read and the write.
       */
      capturedAgainst: await pricingSnapshot(db),
    };

    await db.orders.put(sealed);

    /*
     * Enqueued inline rather than through `enqueue`, for the reason `checkOut` gives: `enqueue`
     * opens its own write, which would put the outbox row outside this transaction and reintroduce
     * exactly the window it exists to close.
     */
    await db.outbox.add({
      mutationId: crypto.randomUUID(),
      type: "CapturedOrder",
      subjectId: sealed.id,
      payload: captured(sealed),
      status: "pending",
      createdAt: now.getTime(),
      attempts: 0,
    });

    return sealed;
  });
}

/**
 * The six watermarks that decided this order's prices (`ORD-08`) — W11 slice 14.
 *
 * <b>Six, because pricing has six inputs and they advance independently.</b> The sync engine calls
 * its own snapshot "a patchwork, not a point in time"; a single number would summarise something with
 * no single value, and the point of recording this is to be able to say *which* input was stale when
 * the server disagrees.
 */
async function pricingSnapshot(db: FieldKitDatabase): Promise<LocalPricingSnapshot> {
  const [priceLists, priceLines, priceAssignments, promotions, promotionAssignments, taxRates] =
    await Promise.all([
      watermark(db, PRICE_LISTS),
      watermark(db, PRICE_LINES),
      watermark(db, PRICE_ASSIGNMENTS),
      watermark(db, PROMOTIONS),
      watermark(db, PROMOTION_ASSIGNMENTS),
      watermark(db, TAX_RATES),
    ]);

  return { priceLists, priceLines, priceAssignments, promotions, promotionAssignments, taxRates };
}

/**
 * Recomputes the total and stores the draft.
 *
 * <b>A sum of the stored line totals, never a re-derivation.</b> Each `lineTotal` is already rounded
 * to the currency's minor units by the pricing engine (`BR-PRD-9`), and re-multiplying quantity by
 * price here would round a second time — the one arithmetic error a reader always notices is a total
 * that disagrees with the column above it. The same call `PricingService` makes server-side.
 */
async function save(
  db: FieldKitDatabase,
  current: LocalOrder,
  lines: LocalOrderLine[],
  now: Date,
): Promise<LocalOrder> {
  const total = lines.reduce(
    (running, line) => running.add(Money.of(line.lineTotal, current.currencyCode)),
    Money.zero(current.currencyCode),
  );

  // Summed the same way and for the same reason: each line's tax is already rounded to the minor
  // unit, and re-deriving it from a rate here would round a second time (`BR-PRD-9`).
  const tax = lines.reduce(
    (running, line) => running.add(Money.of(line.taxAmount, current.currencyCode)),
    Money.zero(current.currencyCode),
  );

  const updated: LocalOrder = {
    ...current,
    lines,
    total: total.amount.toString(),
    taxTotal: tax.amount.toString(),
    updatedAtUtc: now.toISOString(),
  };

  await db.orders.put(updated);

  return updated;
}

/**
 * The order as `/sync/push` expects it (`vectors/sync/push.v1.json`).
 *
 * <b>This is where decimal strings become JSON numbers, and it is the only place that conversion is
 * allowed to happen.</b> `CapturedOrderLine` on the server takes bare `decimal`, and nothing
 * configures `AllowReadingFromString` — so a quoted `"27.00"` is a **400**, which fails the whole
 * batch and is retried on every reconnect forever rather than recorded and stopped. W11 slice 5's
 * wire vector pins the numeric form.
 *
 * Keeping the strings everywhere else and converting once, here, is what makes that safe: every
 * arithmetic the rep sees happened in `decimal.js`, and the value crosses `Number` once, already
 * rounded, at a magnitude where the conversion is exact. Converting earlier — storing numbers — would
 * put an IEEE-754 float between the rep's screen and the record, which is the whole thing `BR-PRD-8`
 * forbids.
 *
 * <b>It is also the wrong shape long-term</b>, and worth saying so rather than leaving it to be
 * discovered: `Money` crosses this API as a string by rule, and these fields are not `Money` because
 * slice 1 chose bare decimals for the captured line. Audit dodged the same question with integer
 * minor units. Whichever way that is settled, this function is the single place it changes.
 */
function captured(order: LocalOrder): Record<string, unknown> {
  return {
    orderId: order.id,
    visitId: order.visitId,
    currencyCode: order.currencyCode,
    total: Number(order.total),
    taxTotal: Number(order.taxTotal),
    capturedAtUtc: order.capturedAtUtc,
    // What the device priced against (`ORD-08`). Numbers, not strings: these are row versions
    // rather than money, so none of `Money`'s reasoning applies to them.
    capturedAgainst: order.capturedAgainst,
    lines: order.lines.map((line) => ({
      productId: line.productId,
      quantity: Number(line.quantity),
      unitOfMeasure: line.unitOfMeasure,
      packSize: line.packSize,
      unitPrice: Number(line.unitPrice),
      lineTotal: Number(line.lineTotal),
      taxAmount: Number(line.taxAmount),
    })),
  };
}

