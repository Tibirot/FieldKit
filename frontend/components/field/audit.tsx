"use client";

import { useTranslations } from "next-intl";
import { useRef, useState } from "react";

import { useSync } from "@/components/sync/sync-provider";
import { Button } from "@/components/ui/button";
import { useRouter } from "@/i18n/navigation";
import {
  auditFor,
  clearAvailability,
  draftFor,
  measured,
  putAvailability,
  putCategoryFacings,
  putFacings,
  putPrice,
  seal as sealAudit,
} from "@/lib/audits/local-audit";
import { expectedPrices } from "@/lib/orders/pricing";
import { looksLikeAnAmount } from "@/lib/api/price-lists";
import type { ResolvedPrice } from "@/lib/pricing/price-resolver";
import type {
  FieldKitDatabase,
  LocalAudit,
  LocalAvailabilityStatus,
  ReferenceProduct,
} from "@/lib/sync/db";
import { useLive } from "@/lib/sync/live";
import {
  assortmentFor,
  currentScoreWeightSet,
  outlet as heldOutlet,
} from "@/lib/sync/reference";
import { visit as heldVisit } from "@/lib/visits/local-visit";

/** The three answers, in the order a rep meets them at a shelf. */
const ANSWERS: readonly LocalAvailabilityStatus[] = ["Present", "Absent", "OutOfStock"];

/**
 * The audit a rep works at a shelf (`AUD-01`, `BR-AUD-1`, `OFF-01b`) — W11 slice 9a.
 *
 * <b>Availability only, and the rest is named rather than implied.</b> Facings, the category total
 * and observed prices are 9b; the questionnaire is 9c. This slice is the spine: an audit that exists
 * on the device, survives a reload, and reaches the server through a drain that will not send it
 * before the visit it belongs to.
 *
 * <b>The list is the outlet's MSL, not its assortment.</b> `BR-AUD-1` is explicit, and the two are
 * different sets: a shop may be allowed to sell a hundred products and be *required* to stock twenty.
 * Auditing everything on the assortment would ask a rep to walk a shelf answering for products
 * nobody committed to, and the availability pillar would then score a shop against a list it never
 * agreed to.
 *
 * <b>Nothing is held in React state that a rep would mind losing.</b> Every tap goes through the
 * Dexie store, so a phone that dies halfway down the aisle loses nothing (`ORD-05`'s argument, on
 * `AUD-01`'s screen).
 *
 * <b>The weighting is fixed when the draft starts, not when it is sealed</b> (`BR-AUD-8`). A rep who
 * begins before a re-weighting syncs and finishes after must be scored on the numbers they were
 * shown, and the version is the one fact that cannot be recovered later.
 */
export function Audit({ visitId }: { visitId: string }) {
  const t = useTranslations("Field.audit");
  const { db } = useSync();

  const visit = useLive(async () => (await heldVisit(db, visitId)) ?? null, undefined, [db, visitId]);

  const shop = useLive(
    async () => (visit ? ((await heldOutlet(db, visit.outletId)) ?? null) : null),
    null,
    [db, visit?.outletId],
  );

  /*
   * The audit whatever state it is in, not just the draft — `orderFor`'s argument, and the bug it
   * was written about: a screen bound to `draft()` blinks back to "nothing audited yet" the moment
   * the rep seals, telling somebody who has just sent an audit that they never started one.
   */
  const held = useLive(async () => (await auditFor(db, visitId)) ?? null, null, [db, visitId]);

  const sealed = held !== null && held.status !== "draft";

  const products = useLive(
    async () => (shop ? await mustStock(db, shop.id, shop.channelId) : []),
    [],
    [db, shop?.id, shop?.channelId],
  );

  /*
   * `undefined` while reading, `null` when the tenant has published none.
   *
   * The second is a real state rather than an error: a device can hold a rep's whole round before an
   * administrator has ever opened the weights screen. It is also unworkable — `BR-AUD-8` records the
   * version at capture and the server refuses an audit naming one it cannot find
   * (`UnknownWeightSet`), so an audit started without weights would be captured, sealed, pushed and
   * refused, and `markRejected` would leave it `failed` with nothing to retry.
   */
  const weights = useLive(
    async () => (await currentScoreWeightSet(db)) ?? null,
    undefined,
    [db],
  );

  /*
   * The day the audit is *for*, from the device's own clock (W11 slice 9b).
   *
   * `BR-AUD-3` resolves the expected price for the outlet **and the date**, and the order screen
   * takes the same reading with the same caveat: `OutletSnapshot` carries no timezone, so this is
   * the device's day, which is the shop's for as long as the rep is standing in it.
   */
  const on = businessDay(new Date());

  /*
   * What each product is *meant* to cost, resolved once and stored with each observation.
   *
   * Read here rather than at the seal because `BR-AUD-3` judges against the price resolved for that
   * outlet and date — a list republished between the rep reading a shelf edge and finishing the
   * audit would otherwise move the number they are measured by, after the fact.
   */
  const expected = useLive(
    async () =>
      shop ? await expectedPrices(db, shop.id, on, products.map((product) => product.id)) : new Map(),
    new Map<string, ResolvedPrice>(),
    [db, shop?.id, on, products.length],
  );

  if (visit === undefined || weights === undefined) return <Waiting message={t("opening")} />;

  if (visit === null) {
    return <Explained title={t("unknownVisit.title")} body={t("unknownVisit.body")} />;
  }

  if (visit.status !== "inProgress") {
    // `BR-AUD-6` seals an audit with its visit, and a sealed visit cannot grow one either. Stated
    // rather than rendered as a screen of dead controls — the call the visit and order screens make.
    return <Explained title={t("sealed.title")} body={t("sealed.body")} />;
  }

  // Refused up front rather than at the seal, because the rep would have walked the whole shelf by
  // then. This is somebody's configuration to fix, and saying so is the only useful thing here.
  if (weights === null && held === null) {
    return <Explained title={t("noWeights.title")} body={t("noWeights.body")} />;
  }

  return (
    <div className="flex flex-col gap-4">
      <header className="flex flex-col gap-1">
        <h1 className="text-lg font-medium">{t("title")}</h1>
        {shop ? <p className="text-sm text-muted-foreground">{shop.name}</p> : null}
      </header>

      {sealed ? <Sent /> : null}

      <Shelf
        audit={held}
        products={products}
        visitId={visitId}
        outletId={visit.outletId}
        weightSetVersion={weights?.version ?? held?.weightSetVersion ?? 0}
        expected={expected}
        /*
          The currency an observation is filed under when no list covers the product. Taken from a
          list that *did* price something here, so the audit stays in one currency — the server
          refuses a mix (`CurrencyMismatch`), and a hard-coded fallback would be this device
          inventing one.
        */
        currency={[...expected.values()][0]?.currency ?? ""}
        editable={!sealed}
      />

      {!sealed && held ? <Seal audit={held} visitId={visitId} /> : null}
    </div>
  );
}

/**
 * The audit is sealed and queued (`BR-AUD-6`).
 *
 * <b>Queued, not sent.</b> The rep is offline more often than not, and the shell's pending count is
 * where "has the back office got it" is answered (`OFF-05`). Claiming *sent* here is a claim this
 * screen cannot make, and the indicator would contradict it a moment later.
 */
function Sent() {
  const t = useTranslations("Field.audit");

  return (
    <div className="flex flex-col gap-1 rounded-xl border border-border p-3" role="status">
      <p className="text-sm">{t("sent.title")}</p>
      <p className="text-sm text-muted-foreground">{t("sent.body")}</p>
    </div>
  );
}

/** The MSL, and what the rep found for each line. */
function Shelf({
  audit,
  products,
  visitId,
  outletId,
  weightSetVersion,
  expected,
  currency,
  editable,
}: {
  audit: LocalAudit | null;
  products: ReferenceProduct[];
  visitId: string;
  outletId: string;
  weightSetVersion: number;
  expected: Map<string, ResolvedPrice>;
  /** What to file an observation under when no list covers the product. */
  currency: string;
  editable: boolean;
}) {
  const t = useTranslations("Field.audit");
  const { db } = useSync();

  const answered = new Map(audit?.availability.map((line) => [line.productId, line.status]) ?? []);
  const counted = new Map(audit?.facings.map((line) => [line.productId, line.facings]) ?? []);
  const read = new Map(audit?.prices.map((line) => [line.productId, line.observed]) ?? []);

  /*
   * Every write goes through one queue, and the numeric fields are why.
   *
   * A tap is one write and cannot race itself. A *typed* field fires on every keystroke — `4.79` is
   * four writes — and each one opens its own Dexie transaction, so they can complete out of order:
   * the screen test caught `4.7` landing after `4.79` and standing as the rep's reading.
   *
   * The alternative was to hold the in-progress text in React state and write on blur, which is what
   * the order screen does with a quantity. It is rejected here because this screen promises
   * something the order screen does not: `OFF-01b`, every measurement durable as it is made, so a
   * phone that dies halfway down an aisle loses nothing. Chaining keeps both — each keystroke is
   * still written, and the last one still wins.
   */
  const writes = useRef<Promise<unknown>>(Promise.resolve());

  const queued = (work: () => Promise<unknown>) => {
    // Both arms, so one failed write does not wedge the chain for the rest of the audit.
    writes.current = writes.current.then(work, work);

    return writes.current;
  };

  /** The total facings on the shelf — blank means *not counted*, which skips the pillar. */
  async function countCategory(raw: string) {
    const now = new Date();
    const current = audit ?? (await draftFor(db, { visitId, outletId, weightSetVersion, now }));

    await putCategoryFacings(db, current.id, wholeOrNull(raw), now);
  }

  /*
   * The draft is created by the first answer, not by opening the screen.
   *
   * A rep who opens the audit step, sees the shelf and is called away leaves nothing behind — no
   * empty audit for the seal to refuse, and nothing for `BR-AUD-6`'s "one per visit" to trip over.
   * The same call the order screen makes about its first line, and for the same reason.
   */
  async function answer(productId: string, status: LocalAvailabilityStatus) {
    const now = new Date();
    const current =
      audit ?? (await draftFor(db, { visitId, outletId, weightSetVersion, now }));

    if (answered.get(productId) === status) {
      await clearAvailability(db, current.id, productId, now);

      return;
    }

    await putAvailability(db, current.id, productId, status, now);
  }

  /**
   * Counts facings for one product (`AUD-02`) — blank removes the count.
   *
   * Starts the draft on its own, like an availability answer: a rep may work the shelf by counting
   * first and ticking afterwards, and either order has to be the beginning of an audit.
   */
  async function count(productId: string, raw: string) {
    const now = new Date();
    const current = audit ?? (await draftFor(db, { visitId, outletId, weightSetVersion, now }));

    await putFacings(db, current.id, productId, wholeOrNull(raw), now);
  }

  /**
   * Records a shelf price against what the device expected (`AUD-03`).
   *
   * The expected price and its currency come from `expected`, resolved once when the screen loaded —
   * `BR-AUD-3` compares against the price for *that outlet and date*, and re-resolving at the seal
   * would judge the rep by a list republished since. A product no list covers still takes an
   * observation; it is not a compliance failure, and the reading is evidence of the gap.
   */
  async function readPrice(productId: string, raw: string) {
    const now = new Date();
    const current = audit ?? (await draftFor(db, { visitId, outletId, weightSetVersion, now }));
    const trimmed = raw.trim();

    /*
     * Emptying the box removes the reading. A value that is merely *not yet* an amount leaves what
     * is stored alone.
     *
     * The distinction matters because this fires on every keystroke: typing `4.79` passes through
     * `4.` on the way, which is not a decimal — treating that as "clear" made a rep wipe their own
     * reading mid-word, and it is what the screen test caught. Only an empty box is an instruction.
     */
    if (trimmed === "") {
      await putPrice(db, current.id, { productId, observed: null }, now);

      return;
    }

    if (!looksLikeAnAmount(trimmed)) return;

    const price = expected.get(productId) ?? null;

    await putPrice(
      db,
      current.id,
      {
        productId,
        observed: trimmed,
        expected: price?.amount ?? null,
        // The currency of the list that priced it; the shop's own when nothing did, so the audit
        // stays in one currency and the server's `CurrencyMismatch` never fires on our own doing.
        currencyCode: price?.currency ?? currency,
      },
      now,
    );
  }

  if (products.length === 0) {
    return (
      <p className="text-sm text-muted-foreground" role="status">
        {t("noMustStock")}
      </p>
    );
  }

  return (
    <section className="flex flex-col gap-2">
      <p className="text-sm text-muted-foreground" role="status">
        {t("progress", { done: answered.size, total: products.length })}
      </p>

      <ul aria-label={t("shelf")} className="flex flex-col gap-2">
        {products.map((product) => (
          <li
            key={product.id}
            className="flex flex-col gap-2 rounded-xl border border-border p-3 text-sm"
          >
            <span>{product.name}</span>

            <div className="flex flex-wrap gap-2">
              {ANSWERS.map((status) => {
                const chosen = answered.get(product.id) === status;

                return (
                  <Button
                    key={status}
                    type="button"
                    size="sm"
                    variant={chosen ? "default" : "outline"}
                    disabled={!editable}
                    /*
                      `aria-pressed` rather than a radio group: the three are a toggle, because
                      tapping the chosen one again un-answers the line. A radio has no way back to
                      having said nothing, and a rep who taps the wrong row needs one — every one of
                      these three is an assertion about the shelf, so leaving the wrong one standing
                      is worse than leaving the line blank.
                    */
                    aria-pressed={chosen}
                    aria-label={t(`answer.${status}For`, { product: product.name })}
                    onClick={() => void queued(() => answer(product.id, status))}
                  >
                    {t(`answer.${status}`)}
                  </Button>
                );
              })}
            </div>

            {/*
              The numbers sit under the answer because that is the order a rep works a shelf in:
              look, then count, then read the label. Both are optional — `BR-AUD-2` and `BR-AUD-3`
              are pillars in their own right, and the score renormalises over what was measured.
            */}
            <div className="flex flex-wrap items-center gap-3">
              <label className="flex items-center gap-2">
                <span className="text-muted-foreground">{t("facings")}</span>
                <input
                  inputMode="numeric"
                  className="w-20 rounded-md border border-border px-2 py-1 text-right"
                  disabled={!editable}
                  defaultValue={counted.get(product.id) ?? ""}
                  aria-label={t("facingsFor", { product: product.name })}
                  onChange={(event) => { const value = event.target.value; void queued(() => count(product.id, value)); }}
                />
              </label>

              <label className="flex items-center gap-2">
                <span className="text-muted-foreground">{t("shelfPrice")}</span>
                <input
                  // `inputMode`, never `type="number"` — a numeric input hands back a `number` on
                  // some browsers, and this value becomes minor units the server compares exactly.
                  inputMode="decimal"
                  className="w-24 rounded-md border border-border px-2 py-1 text-right"
                  disabled={!editable}
                  defaultValue={read.get(product.id) ?? ""}
                  aria-label={t("shelfPriceFor", { product: product.name })}
                  onChange={(event) => { const value = event.target.value; void queued(() => readPrice(product.id, value)); }}
                />
              </label>

              {/*
                What the device says it should cost, shown beside the box rather than pre-filled
                into it. Pre-filling would make "the rep confirmed the expected price" and "the rep
                did not look" the same record, on the one field `BR-AUD-3` judges compliance from.
              */}
              <span className="text-xs text-muted-foreground">
                {expected.has(product.id)
                  ? t("expected", {
                      amount: `${expected.get(product.id)!.amount} ${expected.get(product.id)!.currency}`,
                    })
                  : t("noExpected")}
              </span>
            </div>
          </li>
        ))}
      </ul>

      {/*
        The denominator, and its own row because it is a fact about the *shelf* rather than about any
        product on it (`BR-AUD-2`). Left blank the share-of-shelf pillar is skipped, not scored zero —
        which is why the hint says so rather than treating the box as one a rep forgot.
      */}
      <label className="flex flex-col gap-1 rounded-xl border border-border p-3 text-sm">
        <span>{t("categoryFacings")}</span>
        <span className="text-xs text-muted-foreground">{t("categoryFacingsHint")}</span>
        <input
          inputMode="numeric"
          className="mt-1 w-24 rounded-md border border-border px-2 py-1 text-right"
          disabled={!editable}
          defaultValue={audit?.categoryFacings ?? ""}
          aria-label={t("categoryFacings")}
          onChange={(event) => { const value = event.target.value; void queued(() => countCategory(value)); }}
        />
      </label>
    </section>
  );
}

/**
 * Sealing the audit and putting it in the outbox (`BR-AUD-6`, `OFF-04`).
 *
 * <b>An audit that measured nothing is refused here</b>, which the store also refuses and the server
 * refuses again (`Empty`). This exists so the rep is told *why* rather than watching a button do
 * nothing — the same three-layer arrangement the order's empty check has.
 *
 * <b>`BR-AUD-7`'s mandatory-question gate is not here</b>, and its absence is a dependency rather
 * than an omission: there is no questionnaire on this screen until 9c. When it arrives, the gate
 * belongs beside this refusal, because the rule is about *completing the step* and the server
 * deliberately does not re-check it — a form that gained a mandatory question after the rep answered
 * would otherwise refuse an audit for a question that did not exist when they worked the shelf.
 */
function Seal({ audit, visitId }: { audit: LocalAudit; visitId: string }) {
  const t = useTranslations("Field.audit");
  const router = useRouter();
  const { db } = useSync();

  const [sealing, setSealing] = useState(false);
  const [refused, setRefused] = useState<"empty" | "unexpected" | null>(null);

  const seal = async () => {
    setRefused(null);

    /*
     * <b>The store's rule, imported rather than repeated.</b> This used to read
     * `audit.availability.length === 0`, which was right when availability was all this screen
     * captured — and stayed behind when 9b widened `measured()` to count facings and prices too. A
     * rep who counted the shelf and read the labels was told to check a product for an audit the
     * store would have taken, and the browser is where that showed.
     *
     * Two layers still refuse an empty audit, deliberately: this one so the rep is told *why*, and
     * the store's so a double-tap cannot seal past a screen that is a moment behind. What they must
     * not do is disagree about which audits are empty.
     */
    if (!measured(audit)) {
      setRefused("empty");

      return;
    }

    setSealing(true);

    const sent = await sealAudit(db, audit.id, new Date());

    if (!sent) {
      // The store refused for a reason this screen already checked, so it is not a state a rep can
      // reach by working normally — a concurrent seal from another tab is the honest candidate.
      setSealing(false);
      setRefused("unexpected");

      return;
    }

    // Back to the visit: the audit step is what the rep came from and still has to tick, and the
    // rest of the call is there. The same call check-out and the order screen make.
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
        {sealing ? t("sealing") : t("seal")}
      </Button>
    </section>
  );
}

/**
 * The products this shop is required to stock (`BR-AUD-1`, `B2`).
 *
 * <b>Must-stock only.</b> `assortmentFor` answers "what may be sold here" and carries the MSL flag
 * with it; this filters to the flag, because `BR-AUD-1` drives availability from the MSL and the two
 * sets are different sizes. A product on the assortment but not the MSL is one the shop may sell and
 * has not promised to carry — scoring a shop for its absence would invent a commitment.
 *
 * Sorted by name, because a rep works a shelf by reading labels rather than by product id.
 */
async function mustStock(
  db: FieldKitDatabase,
  outletId: string,
  channelId: string,
): Promise<ReferenceProduct[]> {
  const assortment = await assortmentFor(db, outletId, channelId);
  const ids = [...assortment.entries()].filter(([, isMustStock]) => isMustStock).map(([id]) => id);

  const products = await db.products.bulkGet(ids);

  return products
    .filter((product): product is ReferenceProduct => product !== undefined)
    .sort((left, right) => left.name.localeCompare(right.name));
}

/**
 * A whole non-negative count, or null for "not measured".
 *
 * <b>Blank is null and not zero</b>, which is the distinction both `BR-AUD-2` and the facings lines
 * turn on: zero facings is a measurement, and no answer is the absence of one. Anything else a rep
 * can type — a decimal, a minus, letters — is also null, so a half-typed value never lands as a
 * count; the store refuses those again on its own terms.
 */
function wholeOrNull(raw: string): number | null {
  const trimmed = raw.trim();

  if (!/^\d+$/.test(trimmed)) return null;

  return Number(trimmed);
}

/**
 * The device's day as `YYYY-MM-DD`.
 *
 * The same shape and the same caveat the order screen carries: `BR-AUD-3` wants the *outlet's* day
 * and `OutletSnapshot` has no timezone, so this is the device's — the shop's for as long as the rep
 * is standing in it, and wrong only for a phone that has crossed a border or a rep working within an
 * hour of midnight. `timeZoneId` is the field this snapshot still wants.
 */
function businessDay(now: Date): string {
  const local = new Date(now.getTime() - now.getTimezoneOffset() * 60_000);

  return local.toISOString().slice(0, 10);
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
    <div className="flex flex-col gap-1" role="status">
      <h1 className="text-lg font-medium">{title}</h1>
      <p className="text-sm text-muted-foreground">{body}</p>
    </div>
  );
}
