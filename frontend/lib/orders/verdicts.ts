import type { OrderVerdict } from "@/lib/api/sync";
import type { EntityChanges } from "@/lib/sync/reference";
import type { FieldKitDatabase, LocalOrder } from "@/lib/sync/db";

/**
 * What the back office made of this device's orders (`BR-ORD-9`, `ORD-12`) — W12 F5b.
 *
 * <b>The one thing on the pull feed that is not applied by `reference.ts`.</b> Every entity there is
 * a `ref_*` table the server owns outright, so `apply()` can `bulkPut` a page and be done. An order
 * is the *device's* record — `BR-ORD-6` makes its totals the thing the rep and the shopkeeper agreed
 * — so a verdict is merged into a row this device already holds rather than replacing it.
 *
 * That is why the wire carries no money (F5a): there is nothing on a verdict that *could* overwrite
 * the order, so the merge below cannot be written wrongly.
 */

/** The two fields a verdict decides, and the only two it may touch. */
type Verdicted = Pick<LocalOrder, "status" | "rejection">;

/**
 * Applies a page of verdicts and advances the `orders` watermark.
 *
 * <b>Its own transaction</b>, like every other entity's — a device that got one page of a pull keeps
 * it whatever happened to the rest.
 */
export async function applyOrderVerdicts(
  db: FieldKitDatabase,
  page: EntityChanges<OrderVerdict>,
): Promise<void> {
  await db.transaction("rw", db.orders, db.watermarks, async () => {
    for (const verdict of page.upserts) {
      const held = await db.orders.get(verdict.orderId);

      /*
       * <b>An order this device does not hold is skipped, not created.</b>
       *
       * It is an ordinary state rather than an error: a rep works two phones, or replaces one, and
       * the server's verdicts are scoped to the *person*. Inventing a local order from a verdict
       * would produce a row with no lines and no total — an order that says a rep sold nothing —
       * which is worse in every direction than not showing it.
       *
       * `F4`'s device-swap promise is about the *server* retaining the rejected order, and honouring
       * it on the device needs the order itself on the feed, not the verdict. That is a different
       * slice and it is not this one.
       */
      if (!held) continue;

      const decided = decide(verdict);

      // Nothing to write when the verdict says what the row already says. Dexie would happily put
      // an identical object, and every `liveQuery` watching this table would re-run for it — on a
      // feed that re-sends every order the rep has ever taken after a rebind.
      if (held.status === decided.status && same(held.rejection, decided.rejection)) continue;

      await db.orders.put({ ...held, ...decided });
    }

    /*
     * Never backwards, for the reason `reference.ts` gives: a retried or re-ordered response could
     * carry a cursor behind the one stored, and taking it at face value makes a device oscillate
     * instead of converge.
     */
    const current = await db.watermarks.get(ORDERS);

    await db.watermarks.put({
      entity: ORDERS,
      cursor: Math.max(page.cursor, current?.cursor ?? 0),
    });
  });
}

/** The watermark key. Its own, like every entity's — one entity's traffic never spends another's. */
export const ORDERS = "orders";

/**
 * The server's status as this device records it.
 *
 * <b>Only `Rejected` is read as anything.</b> Everything else means *the back office has not refused
 * this*, which on the device is `submitted` — there is no local `accepted`, because the server has
 * no such state and a device that invented one would be asserting a fact nobody holds.
 *
 * An unrecognised status therefore reads as `submitted` rather than throwing: a server that grows a
 * seventh `OrderStatus` should not strand a rep's screen on a value this build has never heard of,
 * and *not refused* is the safe reading — it leaves the order where it was and shows no false alarm.
 */
function decide(verdict: OrderVerdict): Verdicted {
  return verdict.status === "Rejected"
    ? { status: "rejected", rejection: verdict.rejection }
    : { status: "submitted", rejection: null };
}

/** Whether two rejections say the same thing. Field-wise, because they cross the wire as JSON. */
function same(left: LocalOrder["rejection"], right: LocalOrder["rejection"]): boolean {
  if (left === null || right === null) return left === right;

  return (
    left.reason === right.reason &&
    left.offendingProductId === right.offendingProductId &&
    left.note === right.note
  );
}
