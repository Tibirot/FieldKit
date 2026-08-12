"use client";

import { useTranslations } from "next-intl";
import { useState } from "react";

import { useSync } from "@/components/sync/sync-provider";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { useRouter } from "@/i18n/navigation";
import {
  draftFor,
  orderFor,
  putLine,
  removeLine,
  submit as submitOrder,
} from "@/lib/orders/local-order";
import { priceOrder, type PricedOrder } from "@/lib/orders/pricing";
import { Decimal } from "@/lib/pricing/money";
import type { FieldKitDatabase, LocalOrder, ReferenceProduct } from "@/lib/sync/db";
import { useLive } from "@/lib/sync/live";
import { assortmentFor, outlet as heldOutlet } from "@/lib/sync/reference";
import { visit as heldVisit } from "@/lib/visits/local-visit";

/**
 * The order a rep builds at a counter (`ORD-01`, `ORD-02`, `ORD-03`) — W11 slice 7.
 *
 * <b>Everything on this screen is computed on the device.</b> The rep is standing in a shop with no
 * signal; `priceOrder` resolves the price, the promotion and the tax from what the last sync left
 * behind, and `BR-ORD-2` is the promise that the server's recomputation on push will agree. That is
 * why the numbers here come from `lib/orders/pricing.ts` rather than from anything this file works
 * out — a total assembled in a component is one the parity vectors cannot see.
 *
 * <b>Nothing is held in React state that a rep would mind losing.</b> The quantity being typed is;
 * the order is not. Every add and remove goes through the Dexie store, so a phone that dies mid-order
 * loses at most the number half-entered in the box (`ORD-05`, `OFF-01b`).
 *
 * <b>Submitting is not here.</b> `ORD-06`'s order minimum and the seal into the outbox are slice 8 —
 * the draft this screen builds is exactly what that one seals, and splitting them keeps the minimum
 * (which needs a configuration that does not exist yet) out of the screen that has to work first.
 */
export function Order({ visitId }: { visitId: string }) {
  const t = useTranslations("Field.order");
  const { db } = useSync();

  const visit = useLive(async () => (await heldVisit(db, visitId)) ?? null, undefined, [db, visitId]);

  const shop = useLive(
    async () => (visit ? ((await heldOutlet(db, visit.outletId)) ?? null) : null),
    null,
    [db, visit?.outletId],
  );

  /*
   * The visit's order whatever state it is in, not just the draft.
   *
   * Reading `draft()` here was right until submitting existed: the moment an order is sealed it
   * stops being a draft, and a screen bound to that query would blink back to "nothing on this order
   * yet" with a catalogue under it — telling a rep who has just sent an order that they have not
   * started one. What is `BR-ORD-4`'s lock in the store is a *rendering* decision here.
   */
  const held = useLive(async () => (await orderFor(db, visitId)) ?? null, null, [db, visitId]);

  // Three states, not two: no order yet, a draft, and one that has been sent. The middle and the
  // first look the same to the catalogue and different to everything else.
  const sealed = held !== null && held.status !== "draft";
  const editable = sealed ? null : held;

  const orderable = useLive(
    async () => (shop ? await orderableProducts(db, shop.id, shop.channelId) : []),
    [],
    [db, shop?.id, shop?.channelId],
  );

  /*
   * The day the order is *for*, from the device's own clock.
   *
   * `BR-PRD-6` wants the **outlet's** day, and `OutletSnapshot` does not carry a timezone — so this
   * is the device's, which is the shop's for as long as the rep is standing in it. That covers every
   * ordinary call; what it does not cover is a phone that has crossed a border without updating, or
   * a rep working within an hour of midnight where the two disagree. Named rather than papered over:
   * `timeZoneId` is the next field this snapshot wants, after `countryCode` in slice 7c.
   */
  const on = businessDay(new Date());

  const priced = useLive(
    async () =>
      shop
        ? await priceOrder(
            db,
            shop.id,
            on,
            (held?.lines ?? []).map((line) => ({
              productId: line.productId,
              quantity: line.quantity,
            })),
          )
        : null,
    null,
    [db, shop?.id, on, held?.updatedAtUtc],
  );

  if (visit === undefined) return <Waiting message={t("opening")} />;

  if (visit === null) {
    return <Explained title={t("unknownVisit.title")} body={t("unknownVisit.body")} />;
  }

  if (visit.status !== "inProgress") {
    // `BR-ORD-4` locks an order after submit, and a sealed visit cannot grow one either. Shown as a
    // statement rather than as a screen of disabled controls, the same call the visit screen makes.
    return <Explained title={t("sealed.title")} body={t("sealed.body")} />;
  }

  return (
    <div className="flex flex-col gap-4">
      <header className="flex flex-col gap-1">
        <h1 className="text-lg font-medium">{t("title")}</h1>
        {shop ? <p className="text-sm text-muted-foreground">{shop.name}</p> : null}
      </header>

      {/* A sealed order says so before anything else on the screen. The lines below it are then a
          record of what went, rather than a list with controls the rep will look for. */}
      {sealed ? <Sent /> : null}

      <Lines order={held} priced={priced} products={orderable} editable={!sealed} />

      <Totals priced={priced} />

      {/*
        Submit appears with the draft, not with the first line — so tapping it on an order the rep
        has just emptied says *why* rather than doing nothing. The catalogue is keyed on `sealed`
        rather than on the draft existing, which is the bug the first version of this had: gating it
        on `editable` meant no draft, no catalogue, no way to add the line that creates the draft.
      */}
      {!sealed && held ? <Submit order={held} visitId={visitId} /> : null}

      {!sealed ? (
        <Catalogue
          products={orderable}
          order={editable}
          visitId={visitId}
          outletId={visit.outletId}
          on={on}
        />
      ) : null}
    </div>
  );
}

/**
 * The order is sealed and queued (`ORD-07`, `BR-ORD-4`) — W11 slice 8a.
 *
 * <b>Queued, not sent, and the wording says so.</b> The rep is offline more often than not; the
 * outbox row exists and the shell's pending count is where "has the back office got it" is answered
 * (`OFF-05`). Telling them "sent" here would be a claim this screen cannot make and the indicator
 * would then contradict it.
 */
function Sent() {
  const t = useTranslations("Field.order");

  return (
    <div className="flex flex-col gap-1 rounded-xl border border-border p-3" role="status">
      <p className="text-sm">{t("sent.title")}</p>
      <p className="text-sm text-muted-foreground">{t("sent.body")}</p>
    </div>
  );
}

/**
 * Sealing the order and putting it in the outbox (`ORD-07`) — W11 slice 8a.
 *
 * <b>One transaction, both writes</b> — `submit()` owns that, and it is the same bargain check-out
 * makes: the order becoming `submitted` and the mutation existing are one fact, and splitting them
 * leaves a window where a crash produces either an order the rep believes was sent and never was, or
 * a mutation for an order still showing as editable.
 *
 * <b>An empty order is refused here rather than at the server</b>, which the store also refuses —
 * this exists so the rep is told *why* instead of watching a button do nothing.
 *
 * <b>`ORD-06`'s order minimum is not here</b>, and its absence is a dependency rather than an
 * omission: `BR-ORD-5` says a minimum applies *if configured*, and there is no configuration for one
 * anywhere in the system — no entity, no authoring surface, no feed. Slice 2a named that gap and
 * slice 8b is where it gets paid for. A minimum invented on the device would be a rule no
 * administrator could see, change, or be held to.
 */
function Submit({ order, visitId }: { order: LocalOrder; visitId: string }) {
  const t = useTranslations("Field.order");
  const router = useRouter();
  const { db } = useSync();

  const [sealing, setSealing] = useState(false);
  const [refused, setRefused] = useState<"empty" | "unexpected" | null>(null);

  const seal = async () => {
    setRefused(null);

    if (order.lines.length === 0) {
      setRefused("empty");

      return;
    }

    setSealing(true);

    const sent = await submitOrder(db, order.id, new Date());

    if (!sent) {
      // The store refused for a reason this screen already checked, so it is not a state a rep can
      // reach by working normally — a concurrent seal from another tab is the honest candidate.
      setSealing(false);
      setRefused("unexpected");

      return;
    }

    /*
     * Back to the visit rather than staying here. The order step is what the rep came from and what
     * they still have to tick, and the visit screen is where the rest of the call is — the same call
     * check-out makes about returning to the round.
     */
    router.replace(`/field/visits/${visitId}`);
  };

  return (
    <section className="flex flex-col gap-2">
      {refused ? (
        <p className="text-sm text-destructive" role="alert">
          {t(`refusal.${refused}`)}
        </p>
      ) : null}

      <Button onClick={() => void seal()} disabled={sealing}>
        {sealing ? t("sealing") : t("submit")}
      </Button>
    </section>
  );
}

/** What the rep has put on the order so far, priced. */
function Lines({
  order,
  priced,
  products,
  editable,
}: {
  order: LocalOrder | null;
  priced: PricedOrder | null;
  products: ReferenceProduct[];
  editable: boolean;
}) {
  const t = useTranslations("Field.order");
  const { db } = useSync();

  if (!order || order.lines.length === 0) {
    return (
      <p className="text-sm text-muted-foreground" role="status">
        {t("empty")}
      </p>
    );
  }

  const byProduct = new Map(priced?.lines.map((line) => [line.productId, line]) ?? []);
  const named = new Map(products.map((product) => [product.id, product]));

  return (
    <section className="flex flex-col gap-2">
      <h2 className="text-sm font-medium">{t("linesLabel")}</h2>

      <ul aria-label={t("linesLabel")} className="flex flex-col gap-2">
        {order.lines.map((line) => {
          const priceOf = byProduct.get(line.productId);

          return (
            <li
              key={line.productId}
              className="flex items-start justify-between gap-3 rounded-xl border border-border p-3"
            >
              <div className="flex min-w-0 flex-col">
                <span className="font-medium">
                  {named.get(line.productId)?.name ?? t("unknownProduct")}
                </span>

                <span className="text-xs text-muted-foreground">
                  {t("quantity", { quantity: line.quantity, unit: line.unitOfMeasure })}
                  {" · "}
                  {priceOf ? priceOf.unitPrice.toString() : line.unitPrice}
                </span>

                {/*
                  The rep is told *that* a deal applied, not which one. The name would be the useful
                  thing and the priced line carries only the id — `resolvePromotion` returns the
                  winner, and looking its name up here would be a second read per line on a list that
                  re-prices on every keystroke. Slice 8's review screen is where it belongs.
                */}
                {priceOf?.promotionId ? (
                  <span className="mt-1 self-start">
                    <Badge variant="secondary">{t("promoted")}</Badge>
                  </span>
                ) : null}
              </div>

              <div className="flex shrink-0 flex-col items-end gap-2">
                <span className="font-medium">
                  {priceOf ? priceOf.total.toString() : t("unpriced")}
                </span>

                {/* Not rendered on a sealed order rather than rendered disabled: `removeLine`
                    already refuses one, so this is about not offering the action — a rep looking at
                    a greyed-out Remove is a rep wondering what they did wrong. */}
                {editable ? (
                  <Button
                    variant="outline"
                    size="sm"
                    onClick={() => void removeLine(db, order.id, line.productId, new Date())}
                  >
                    {t("remove")}
                  </Button>
                ) : null}
              </div>
            </li>
          );
        })}
      </ul>

      {/* A line the device can no longer price is reported rather than dropped — the rep added it,
          and a total that quietly omitted it would be a number nobody could check. */}
      {priced && priced.unpriced.length > 0 ? (
        <p className="text-sm text-destructive" role="alert">
          {t("unpricedLines", { count: priced.unpriced.length })}
        </p>
      ) : null}
    </section>
  );
}

/**
 * The four numbers a shopkeeper is told.
 *
 * <b>Tax is shown and is not sent</b>, and that is a gap in the model rather than in this screen.
 * `CapturedOrderLine` carries `unitPrice` and `lineTotal` and nothing else, and `OrderLine.LineTotal`
 * is documented as "what the device made of the line **after any promotion it applied**" — so the
 * order that reaches the server is net of tax, and the gross the rep read out to the shopkeeper has
 * nowhere to travel. `ORD-02` asks the device to price tax, `BR-ORD-6` makes the device's totals the
 * record, and between them there is no field. Recorded in the delivery plan as the next thing the
 * captured shape needs.
 */
function Totals({ priced }: { priced: PricedOrder | null }) {
  const t = useTranslations("Field.order");

  if (!priced || priced.total === null) return null;

  return (
    // Named, so it is a landmark a screen-reader user can jump to — this is the block a rep reads
    // out loud, and it is otherwise four unlabelled rows between two lists.
    <section aria-label={t("totalsLabel")}>
      <dl className="flex flex-col gap-1 rounded-xl border border-border p-3 text-sm">
        <Row label={t("subtotal")} value={priced.subtotal!.toString()} />

        {/* Only when there is one. A row reading "0.00" on every ordinary order teaches a rep to
            stop reading the block that also contains the number they are about to say out loud. */}
        {priced.discount!.amount.isZero() ? null : (
          <Row label={t("discount")} value={`-${priced.discount!.toString()}`} />
        )}

        <Row label={t("tax")} value={priced.tax!.toString()} />

        <div className="flex justify-between border-t border-border pt-1 font-medium">
          <dt>{t("total")}</dt>
          <dd>{priced.total.toString()}</dd>
        </div>
      </dl>
    </section>
  );
}

function Row({ label, value }: { label: string; value: string }) {
  return (
    <div className="flex justify-between text-muted-foreground">
      <dt>{label}</dt>
      <dd>{value}</dd>
    </div>
  );
}

/**
 * What this shop may be sold (`ORD-01`, `BR-ORD-1`).
 *
 * <b>The assortment is the list, not the catalogue.</b> A rep offered every product the tenant sells
 * would build orders the server rejects on push (slice 4b), which strands the work until they can
 * fix it — so the refusal happens here, by not offering the line at all.
 */
function Catalogue({
  products,
  order,
  visitId,
  outletId,
  on,
}: {
  products: ReferenceProduct[];
  order: LocalOrder | null;
  visitId: string;
  outletId: string;
  on: string;
}) {
  const t = useTranslations("Field.order");
  const { db } = useSync();

  const [quantities, setQuantities] = useState<Record<string, string>>({});

  /*
   * The two ways adding a line can be refused, as a literal union rather than a `string`.
   *
   * `t()` is typed against the catalogue, so a widened type turns the message key into
   * `` `refusal.${string}` `` and stops compiling — which is the check doing its job: a refusal
   * with no message is a rep told nothing at all.
   */
  const [refused, setRefused] = useState<"quantity" | "unpriced" | null>(null);

  const add = async (product: ReferenceProduct) => {
    const quantity = (quantities[product.id] ?? "").trim();

    setRefused(null);

    if (!isPositiveDecimal(quantity)) {
      setRefused("quantity");

      return;
    }

    /*
     * Priced *before* the line is stored, and priced as part of the whole order rather than alone.
     *
     * A volume tier reads the quantity on this line, so pricing one line in isolation would give the
     * same answer — but an order-level rule never would, and `PricedOrder` is the shape that has
     * somewhere to put one when `B1`'s order-level promotion finally exists. Pricing the prospective
     * order also means the refusal below is the engine's answer rather than a second opinion about
     * whether this shop has a price.
     */
    const prospective = [
      ...(order?.lines ?? [])
        .filter((line) => line.productId !== product.id)
        .map((line) => ({ productId: line.productId, quantity: line.quantity })),
      { productId: product.id, quantity },
    ];

    const priced = await priceOrder(db, outletId, on, prospective);
    const line = priced?.lines.find((candidate) => candidate.productId === product.id);

    if (!line) {
      setRefused("unpriced");

      return;
    }

    /*
     * The draft is created here rather than on opening the screen, and the currency is why.
     *
     * `BR-ORD-7` takes it from the resolved price list, so an order that has never been priced has
     * no currency to be created with — and a draft minted on arrival would either guess one or hold
     * an empty string until the first line. Created on the first add, it is created knowing.
     *
     * It also means a rep who opens the order step and changes their mind leaves no empty draft
     * behind for slice 8 to decide what to do with.
     */
    const current = order ?? (await draftFor(db, {
      visitId,
      outletId,
      currencyCode: line.total.currency,
      now: new Date(),
    }));

    await putLine(db, current.id, {
      productId: product.id,
      quantity,
      unitOfMeasure: product.unitOfMeasure ?? "",
      packSize: product.packSize,
      unitPrice: line.unitPrice.amount.toString(),

      /*
       * The **net**, which is what `OrderLine.LineTotal` is documented to be: "what the device made
       * of the line after any promotion it applied". Storing the gross here would put tax into a
       * field the server sums into an order total that has no tax in it, and the two sides would
       * disagree by exactly the VAT on every order — the failure slice 7b was opened to fix,
       * reintroduced from the other end.
       */
      lineTotal: line.net.amount.toString(),
      now: new Date(),
    });

    setQuantities((current) => ({ ...current, [product.id]: "" }));
  };

  if (products.length === 0) {
    return (
      <p className="text-sm text-muted-foreground" role="status">
        {t("noAssortment")}
      </p>
    );
  }

  return (
    <section className="flex flex-col gap-2">
      <h2 className="text-sm font-medium">{t("catalogueLabel")}</h2>

      {refused ? (
        <p className="text-sm text-destructive" role="alert">
          {t(`refusal.${refused}`)}
        </p>
      ) : null}

      <ul aria-label={t("catalogueLabel")} className="flex flex-col gap-2">
        {products.map((product) => (
          <li
            key={product.id}
            className="flex items-center justify-between gap-3 rounded-xl border border-border p-3"
          >
            <div className="flex min-w-0 flex-col">
              <span className="font-medium">{product.name}</span>
              <span className="font-mono text-xs text-muted-foreground">{product.sku}</span>
            </div>

            <div className="flex shrink-0 items-center gap-2">
              {/*
                The label wraps nothing but its own text, and the unit sits outside it. A label
                containing both would name the box "How many Cola 500ml case" — the unit is beside
                the field for the eye, not part of what the field is called.
              */}
              <label className="sr-only" htmlFor={`quantity-${product.id}`}>
                {t("quantityLabel", { product: product.name })}
              </label>

              {/*
                `inputMode="decimal"` rather than `type="number"`: a quantity can be a weight, the
                value has to stay the exact string the rep typed all the way to `decimal.js`, and a
                numeric input hands back a `number` on some browsers — the one coercion `BR-PRD-8`
                forbids, on a phone keyboard that would otherwise be right.
              */}
              <input
                id={`quantity-${product.id}`}
                className="w-20 rounded-xl border border-border bg-transparent p-2 text-right text-sm"
                inputMode="decimal"
                value={quantities[product.id] ?? ""}
                onChange={(event) =>
                  setQuantities((current) => ({ ...current, [product.id]: event.target.value }))
                }
              />

              <span className="text-xs text-muted-foreground">{product.unitOfMeasure}</span>

              <Button variant="outline" size="sm" onClick={() => void add(product)}>
                {t("add")}
              </Button>
            </div>
          </li>
        ))}
      </ul>
    </section>
  );
}

/**
 * The products this outlet may be sold, by name.
 *
 * A product the assortment names and this device has never pulled is skipped rather than shown as a
 * blank row: the two arrive on different feeds, and a device part-way through its first sync holds
 * one and not the other.
 */
async function orderableProducts(
  db: FieldKitDatabase,
  outletId: string,
  channelId: string,
): Promise<ReferenceProduct[]> {
  const assorted = await assortmentFor(db, outletId, channelId);
  const held = await db.products.bulkGet([...assorted.keys()]);

  return held
    .filter((product): product is ReferenceProduct => product !== undefined)
    .filter((product) => product.status === "Active")
    .sort((left, right) => left.name.localeCompare(right.name));
}

/**
 * A quantity the engine can price.
 *
 * Checked as a **string** rather than by parsing: `Number("")` is 0 and `Number("1e3")` is 1000, and
 * neither is a quantity a rep typed. What reaches `Money` has to be the digits they entered.
 */
function isPositiveDecimal(value: string): boolean {
  if (!/^\d+(\.\d+)?$/.test(value)) return false;

  return new Decimal(value).greaterThan(0);
}

/** `YYYY-MM-DD` in the device's own day — see the note at the call site. */
function businessDay(now: Date): string {
  const pad = (part: number) => String(part).padStart(2, "0");

  return `${now.getFullYear()}-${pad(now.getMonth() + 1)}-${pad(now.getDate())}`;
}

function Waiting({ message }: { message: string }) {
  return (
    <p className="text-sm text-muted-foreground" role="status">
      {message}
    </p>
  );
}

function Explained({ title, body }: { title: string; body: string }) {
  return (
    <div className="flex flex-col gap-1" role="alert">
      <h1 className="text-lg font-medium">{title}</h1>
      <p className="text-sm text-muted-foreground">{body}</p>
    </div>
  );
}
