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
  reopen,
  submit as submitOrder,
} from "@/lib/orders/local-order";
import { priceOrder, type PricedOrder } from "@/lib/orders/pricing";
import { businessDay } from "@/lib/pricing/business-day";
import { Decimal } from "@/lib/pricing/money";
import { checkOrderMinimum, type ResolvedOrderMinimum } from "@/lib/pricing/order-minimum";
import type { FieldKitDatabase, LocalOrder, ReferenceProduct } from "@/lib/sync/db";
import { useLive } from "@/lib/sync/live";
import { assortmentFor, orderMinimumFor, outlet as heldOutlet } from "@/lib/sync/reference";
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
 * <b>Submitting arrived after the screen did</b>, and this paragraph used to say it never would: the
 * seal into the outbox landed in slice 8a and `ORD-06`'s minimum in 8b-ii, both refused in `Submit`
 * below. Left as a correction rather than deleted, because the sentence it replaces was accurate when
 * written and false for two slices afterwards — the same way a comment goes wrong everywhere else in
 * this codebase.
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
   * The day the order is *for* — the **shop's**, not the phone's (`BR-PRD-6`) — W11½ R6b.
   *
   * This read the device's local day until R6b, and the server re-priced against the UTC one: two
   * rules rather than one rounded twice, so an order taken before 03:00 in Bucharest was flagged as
   * a disagreement the rep did nothing to cause (regression F6). `OutletSnapshot` now carries the
   * zone, and `businessDay` is the same rule the server runs — held to it by
   * `vectors/pricing/business-day.v1.json`.
   *
   * `null` while the shop is still being read, and `null` for a shop pulled before R6a whose zone
   * has not arrived. The pricing query below already declines without a shop and declines on this
   * for the same reason: a price resolved against a guessed day is worse than no price, because it
   * looks like one.
   */
  const on = shop ? businessDay(new Date(), shop.timeZoneId) : null;

  /*
   * The minimum this shop's order has to meet (`ORD-06`, `BR-ORD-5`) — W11 slice 8b-ii.
   *
   * Resolved with the order rather than at submit, so the rep can be told the threshold while they
   * can still do something about it. `null` covers three cases the screen treats alike: no minimum
   * configured, none reaching this shop, and a device whose first pull has not landed — all of which
   * mean every order passes, which is what `BR-ORD-5`'s "if configured" says.
   */
  const minimum = useLive(
    async () => (shop ? await orderMinimumFor(db, shop.id) : null),
    null,
    [db, shop?.id],
  );

  /*
   * The order is read *inside* the query, not passed in from `held` above.
   *
   * It used to take `held.lines` and list `held.updatedAtUtc` as a dependency, which meant every
   * edit tore down the subscription and built a new one. That re-subscribe is a race: in the tests
   * for this slice it intermittently produced a live query that never emitted — no error, nothing
   * logged, just a priced line rendering as "No price" for good, on a screen whose store held the
   * right number all along. Reading the order here lets Dexie see that this query depends on the
   * `orders` table and re-run it itself, which is the thing `liveQuery` is for.
   */
  const priced = useLive(
    async () => {
      // `on` joins `shop` in the guard (W11½ R6b): a shop whose zone has not arrived cannot be
      // priced against a day, and pricing it against the phone's would be the defect restored.
      if (!shop || !on) return null;

      const current = await orderFor(db, visitId);

      return await priceOrder(
        db,
        shop.id,
        on,
        (current?.lines ?? []).map((line) => ({
          productId: line.productId,
          quantity: line.quantity,
        })),
      );
    },
    null,
    [db, visitId, shop?.id, on],
  );

  if (visit === undefined) return <Waiting message={t("opening")} />;

  if (visit === null) {
    return <Explained title={t("unknownVisit.title")} body={t("unknownVisit.body")} />;
  }

  /*
   * <b>A sealed visit closes this screen — unless the back office refused the order</b> (`BR-ORD-9`)
   * — W12 F5b.
   *
   * `BR-ORD-4` locks an order after submit and a sealed visit cannot grow one, and the rule's own
   * text names the single exception: a server-rejected order. It has to be *this* exception rather
   * than a looser one, because a rejection almost always arrives **after** check-out — an operator
   * refuses an order minutes or days later — so a guard that only admitted an open visit would put
   * the fix behind a door that is already shut. That is F4's lesson, one screen over.
   *
   * <b>Keyed on the rejection, not on the status, and the difference is the whole fix.</b> Re-opening
   * turns the order back into a `draft` — so a status test would admit the rep, then lock them out
   * again on the very next render, before they had changed a line. The rejection outlives that
   * transition on purpose (`reopen`), and it is what says *this order is being corrected*. It goes
   * when the server accepts the correction, and the door closes behind the rep exactly then.
   */
  if (visit.status !== "inProgress" && !held?.rejection) {
    // Shown as a statement rather than as a screen of disabled controls, the same call the visit
    // screen makes.
    return <Explained title={t("sealed.title")} body={t("sealed.body")} />;
  }

  return (
    <div className="flex flex-col gap-4">
      <header className="flex flex-col gap-1">
        <h1 className="text-lg font-medium">{t("title")}</h1>
        {shop ? <p className="text-sm text-muted-foreground">{shop.name}</p> : null}
      </header>

      {/* A sealed order says so before anything else on the screen. The lines below it are then a
          record of what went, rather than a list with controls the rep will look for.

          A *refused* one says that instead, and says it louder — it is the only state on this screen
          that asks the rep to do something (W12 F5b).

          Shown while the rejection stands, which outlasts the `rejected` status: once the rep taps
          *fix* the order is a draft again, and the reason is the only thing naming the line they
          came here to change. It goes when the server accepts the correction. */}
      {held?.rejection ? <Refused order={held} /> : sealed ? <Sent /> : null}

      <Lines order={held} priced={priced} products={orderable} editable={!sealed} />

      <Totals priced={priced} />

      {/*
        Submit appears with the draft, not with the first line — so tapping it on an order the rep
        has just emptied says *why* rather than doing nothing. The catalogue is keyed on `sealed`
        rather than on the draft existing, which is the bug the first version of this had: gating it
        on `editable` meant no draft, no catalogue, no way to add the line that creates the draft.
      */}
      {!sealed && held ? (
        <Submit order={held} visitId={visitId} minimum={minimum} priced={priced} />
      ) : null}

      {/*
        No catalogue without a day to price against (W11½ R6b).

        Two things produce that, and both are transient: the shop is still being read — in which case
        `orderable` is empty and the catalogue would render nothing anyway — or its zone has not
        arrived yet on a device that upgraded before its next sync. Adding a line against a guessed
        day would price it wrong and look right, which is the failure this whole slice is about.
      */}
      {!sealed && on ? (
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
 * The back office refused it, and the rep can fix it (`ORD-12`, `BR-ORD-9`) — W12 F5b.
 *
 * <b>The reason, the line, and one action.</b> `F4` splits rejections into the kind a rep fixes and
 * the kind they can only cancel; this offers the fix for all four codes, because *cancel* is a
 * device-owned mutation the push arm does not carry yet. A rep sent here by `OutletClosed` finds a
 * screen that lets them edit an order nobody can accept — which is a smaller wrong than no screen at
 * all, and it is named in the regression rather than left to be discovered.
 *
 * <b>`role="alert"`</b>, not `status`: this is the one thing on the screen that asks for something.
 */
function Refused({ order }: { order: LocalOrder }) {
  const t = useTranslations("Field.order");
  const { db } = useSync();

  const [reopening, setReopening] = useState(false);

  // The catalogue's own name for the line, when this device holds it. A rejection names a product id
  // and a rep cannot act on one — and an id printed at them is worse than the reason alone.
  const offending = useLive(
    async () =>
      order.rejection?.offendingProductId
        ? ((await db.products.get(order.rejection.offendingProductId)) ?? null)
        : null,
    null,
    [db, order.rejection?.offendingProductId],
  );

  const reason = order.rejection?.reason ?? "";

  return (
    <section className="flex flex-col gap-2 rounded-xl border border-destructive p-3" role="alert">
      {/*
        A closed map rather than the code interpolated into a key.

        `t(\`refused.reason.${reason}\`)` is what this wants to be, and next-intl's typed keys refuse
        it — rightly. The type error is the same hazard W11½ R5 shipped and had to fix: a server that
        grows a fifth reason would hand a rep the key path itself, because `t` renders a miss as the
        path rather than throwing. A lookup with an explicit default cannot do that.
      */}
      <p className="text-sm font-medium">{t(REASONS[reason] ?? "refused.reason.unknown")}</p>

      {offending ? (
        <p className="text-sm text-muted-foreground">
          {t("refused.line", { product: offending.name })}
        </p>
      ) : null}

      {/* The operator's own words, when they left any. Never the rejection's meaning — that is the
          code above — but the only thing that makes `Other` actionable. */}
      {order.rejection?.note ? (
        <p className="text-sm text-muted-foreground">{order.rejection.note}</p>
      ) : null}

      {/* Only while it is still locked. Once the rep has tapped it the order is a draft and the
          controls below are the way to act; a second *fix* button would do nothing and read as if
          something had failed. */}
      {order.status === "rejected" ? (
        <Button
          variant="outline"
          size="sm"
          className="self-start"
          disabled={reopening}
          onClick={() => {
            setReopening(true);
            void reopen(db, order.id, new Date()).finally(() => setReopening(false));
          }}
        >
          {t("refused.fix")}
        </Button>
      ) : null}
    </section>
  );
}

/**
 * `OrderRejectionReason`'s four names, as message keys — W12 F5b.
 *
 * Written out rather than derived, so a reason the catalogue has no wording for is a compile-time
 * question here and a rendered fallback at runtime, instead of a key path shown to a rep.
 */
const REASONS: Record<string, "refused.reason.OffAssortment" | "refused.reason.OutletClosed"
  | "refused.reason.OutletOnHold" | "refused.reason.Other"> = {
  OffAssortment: "refused.reason.OffAssortment",
  OutletClosed: "refused.reason.OutletClosed",
  OutletOnHold: "refused.reason.OutletOnHold",
  Other: "refused.reason.Other",
};

/**
 * Sealing the order and putting it in the outbox (`ORD-07`) — W11 slice 8a, the minimum in 8b-ii.
 *
 * <b>One transaction, both writes</b> — `submit()` owns that, and it is the same bargain check-out
 * makes: the order becoming `submitted` and the mutation existing are one fact, and splitting them
 * leaves a window where a crash produces either an order the rep believes was sent and never was, or
 * a mutation for an order still showing as editable.
 *
 * <b>An empty order is refused here rather than at the server</b>, which the store also refuses —
 * this exists so the rep is told *why* instead of watching a button do nothing.
 *
 * <b>`ORD-06`'s minimum is refused here too, and only here.</b> `BR-ORD-5` is the one business rule
 * in this module with no server-side gate, deliberately: "must be met to submit" is a question asked
 * at a counter with no signal, and a rep who found out on sync that yesterday's order was too small
 * cannot go back and add a case to it. The server still *resolves* the same minimum (8b-i) through
 * the same pure rule, so the two never disagree about which threshold applies.
 *
 * <b>Refused before the store is touched</b>, like the empty check — an order under the minimum
 * stays a draft the rep can add to, which is the only useful thing to leave them holding.
 */
function Submit({
  order,
  visitId,
  minimum,
  priced,
}: {
  order: LocalOrder;
  visitId: string;
  minimum: ResolvedOrderMinimum | null;
  priced: PricedOrder | null;
}) {
  const t = useTranslations("Field.order");
  const router = useRouter();
  const { db } = useSync();

  const [sealing, setSealing] = useState(false);
  const [refused, setRefused] = useState<
    "empty" | "belowMinimum" | "currencyMismatch" | "uncheckedMinimum" | "unexpected" | null
  >(null);

  /*
   * The **net** total is what a minimum is measured against, not the gross.
   *
   * A decision this slice had to make and `BR-ORD-5` did not: a minimum is a commercial rule about
   * what an order is worth to the supplier, and the tax on it is collected for the state rather than
   * earned. Two things settle it beyond taste. `priceLine` reads a missing tax rate as *unknown* and
   * charges nothing (`PRD-07`), so a gross-based minimum would make the verdict depend on how far a
   * tenant has got with configuring tax — the same order passing in one country and failing in
   * another for reasons nobody authored. And `BR-ORD-6` has the server re-price on arrival: a
   * threshold that moves with a recomputed VAT line is one a rep could meet on the device and miss on
   * the server.
   */
  const verdict = checkOrderMinimum(minimum, priced?.net ?? null);

  const seal = async () => {
    setRefused(null);

    if (order.lines.length === 0) {
      setRefused("empty");

      return;
    }

    if (verdict === "NotMet") {
      setRefused("belowMinimum");

      return;
    }

    if (verdict === "CurrencyMismatch") {
      // Its own message, because "your order is too small" would send a rep to add stock nobody
      // asked for and be refused again. This is somebody's configuration to fix, not theirs.
      setRefused("currencyMismatch");

      return;
    }

    if (verdict === "Unreadable") {
      // A minimum applies and this device cannot decide against it — the stored amount is broken, or
      // nothing on the order priced. Refusing is the safe half: the order stays, and stays editable.
      setRefused("uncheckedMinimum");

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
      {/*
        The threshold is shown before the rep taps, not only after they are refused — which is what
        keeping `resolveOrderMinimum` and `checkOrderMinimum` apart bought. A rep who can see they
        need another 40 lei adds a case; one who finds out by being turned away has already decided
        the order was finished.

        Shown while the order is under the minimum only. A met minimum is a rule that has stopped
        mattering, and a line about it would be one more number to read past on a small screen.
      */}
      {minimum && verdict === "NotMet" ? (
        <p className="text-sm text-muted-foreground" role="status">
          {t("minimum.short", { amount: `${minimum.amount} ${minimum.currencyCode}` })}
        </p>
      ) : null}

      {refused ? (
        <p className="text-sm text-destructive" role="alert">
          {t(`refusal.${refused}`)}
        </p>
      ) : null}

      {/*
        Not `disabled`. A button that cannot be pressed says nothing about why, and `BR-ORD-5` is a
        rule a rep can actually satisfy — pressing it and being told the number is what turns a dead
        control into an instruction. The same call the empty-order refusal already made.
      */}
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
 * <b>Tax is shown and is now sent</b> (W11 slice 14). It was not, for three slices: `CapturedOrderLine`
 * carried `unitPrice` and `lineTotal` and nothing else, so the order that reached the server was net
 * of tax and the gross the rep read out to the shopkeeper had nowhere to travel. `taxAmount` on the
 * line and `taxTotal` on the order are that field — and they are what makes `BR-ORD-6`'s comparison
 * mean anything, since the server's recomputation includes tax and would otherwise have been measured
 * against a number that never did.
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

      // And the tax beside it, which W11 slice 14 gave the captured shape somewhere to put. Until
      // then this number was shown to the shopkeeper and thrown away.
      taxAmount: line.tax.amount.toString(),
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
