"use client";

import { useTranslations } from "next-intl";
import { useRef, useState } from "react";

import { useSync } from "@/components/sync/sync-provider";
import { Button } from "@/components/ui/button";
import { useRouter } from "@/i18n/navigation";
import {
  auditFor,
  chooseSurvey,
  clearAvailability,
  draftFor,
  measured,
  putAnswer,
  putAvailability,
  putCategoryFacings,
  putFacings,
  putPrice,
  scoreInputsFor,
  seal as sealAudit,
  unanswered,
} from "@/lib/audits/local-audit";
import {
  computeScore,
  SCORE_PILLARS,
  type PillarScore,
  type ScorePillar,
} from "@/lib/audits/score";
import { expectedPrices } from "@/lib/orders/pricing";
import { looksLikeAnAmount } from "@/lib/api/price-lists";
import type { ResolvedPrice } from "@/lib/pricing/price-resolver";
import type {
  FieldKitDatabase,
  LocalAudit,
  LocalAvailabilityStatus,
  ReferenceProduct,
  ReferenceSurveyForm,
  ReferenceSurveyQuestion,
} from "@/lib/sync/db";
import { useLive } from "@/lib/sync/live";
import {
  assortmentFor,
  currentScoreWeightSet,
  outlet as heldOutlet,
  scoreWeightSet,
  surveyForms,
} from "@/lib/sync/reference";
import { visit as heldVisit } from "@/lib/visits/local-visit";

/** The three answers, in the order a rep meets them at a shelf. */
const ANSWERS: readonly LocalAvailabilityStatus[] = ["Present", "Absent", "OutOfStock"];

/**
 * How a multi-choice answer's options are joined into one string (`AUD-04`).
 *
 * A unit separator rather than a comma, because an option may contain one: "Front, centre" and two
 * chosen options would be the same value otherwise, and the server stores what it is given. Nothing
 * a rep can type reaches this — the options come from the form.
 */
const MULTI_CHOICE_SEPARATOR = "";

/**
 * One chain for every write this screen makes.
 *
 * <b>A typed field fires on every keystroke</b> — `4.79` is four writes, each opening its own Dexie
 * transaction, and they can complete out of order: a screen test caught `4.7` landing after `4.79`
 * and standing as the rep's reading. Taps cannot race themselves, but they share the chain so that a
 * tap and a keystroke cannot either.
 *
 * <b>One queue for the whole screen, not one per section.</b> The shelf and the questionnaire write
 * to the same audit row, so two chains would race each other exactly as the keystrokes did.
 *
 * The alternative — hold the in-progress text in React state and write on blur, as the order screen
 * does with a quantity — is rejected because this screen promises `OFF-01b`: every measurement
 * durable as it is made, so a phone that dies halfway down an aisle loses nothing.
 */
type Queued = (work: () => Promise<unknown>) => Promise<unknown>;

function useWrites(): Queued {
  const writes = useRef<Promise<unknown>>(Promise.resolve());

  return (work: () => Promise<unknown>) => {
    // Both arms, so one failed write does not wedge the chain for the rest of the audit.
    writes.current = writes.current.then(work, work);

    return writes.current;
  };
}

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

  // One chain for the whole screen — see `useWrites`. Created here and handed down, because the
  // shelf and the questionnaire write to the same audit row and two chains would race each other.
  const queued = useWrites();

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

  /*
   * The tenant's questionnaires (`AUD-04`) — W11 slice 9c.
   *
   * All of them, because nothing in the model says which one applies at this shop: a workflow step
   * carries a type and a label and no form id. With one the screen uses it; with several it asks.
   */
  const forms = useLive(async () => await surveyForms(db), [], [db]);

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
        queued={queued}
      />

      <Survey
        audit={held}
        forms={forms}
        onStart={() =>
          draftFor(db, {
            visitId,
            outletId: visit.outletId,
            weightSetVersion: weights?.version ?? held?.weightSetVersion ?? 0,
            now: new Date(),
          })
        }
        editable={!sealed}
        queued={queued}
      />

      {held ? <Score audit={held} /> : null}

      {!sealed && held ? (
        <Seal
          audit={held}
          visitId={visitId}
          /*
            `BR-AUD-7`'s gate needs the questions **as the rep is looking at them**, which is the form
            this screen is presenting — not the one the audit names. This used to read
            `held.surveyFormId`, and an audit names no form until the rep answers something: a rep who
            scrolled past the questionnaire and tapped Finish sealed with every mandatory question
            unanswered and nothing said.
          */
          questions={workingForm(forms, held)?.questions ?? []}
        />
      ) : null}
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
  queued,
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
  queued: Queued;
}) {
  const t = useTranslations("Field.audit");
  const { db } = useSync();

  const answered = new Map(audit?.availability.map((line) => [line.productId, line.status]) ?? []);
  const counted = new Map(audit?.facings.map((line) => [line.productId, line.facings]) ?? []);
  const read = new Map(audit?.prices.map((line) => [line.productId, line.observed]) ?? []);


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
 * <b>`BR-AUD-7`'s mandatory-question gate is here too</b>, and here *only*: the rule is about
 * completing the step, which happens on this screen with the rep looking at the form, and the server
 * deliberately does not re-check it — a form that gained a mandatory question after the rep answered
 * would otherwise refuse an audit for a question that did not exist when they worked the shelf.
 */
function Seal({
  audit,
  visitId,
  questions,
}: {
  audit: LocalAudit;
  visitId: string;
  questions: readonly ReferenceSurveyQuestion[];
}) {
  const t = useTranslations("Field.audit");
  const router = useRouter();
  const { db } = useSync();

  const [sealing, setSealing] = useState(false);
  const [refused, setRefused] = useState<"empty" | "mandatory" | "unexpected" | null>(null);

  /*
   * `BR-AUD-7`, and this is the only place it is enforced (W11 slice 9c).
   *
   * "Mandatory survey questions must be answered before the audit step completes" is a rule about
   * *completing a step*, which happens here with the rep looking at the form. `IAuditIngest`
   * deliberately does not re-check it: the server would test the answers against the questionnaire
   * as it reads **today**, so a form that gained a mandatory question after the rep answered would
   * refuse an audit for a question that did not exist when they worked the shelf.
   *
   * Named rather than counted, because "2 questions still need answering" sends a rep back through a
   * form looking for which two.
   */
  const outstanding = unanswered(audit, questions);

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
    /*
     * <b>Read fresh, not from the prop.</b> `audit` is the last value the live query emitted, and the
     * first tap on a shelf is *two* writes — the draft, then the answer. Between them the screen holds
     * a real audit that has measured nothing, and the Finish button is already on it. A rep who
     * answered and tapped straight through was told to check a product they had just checked, and had
     * to tap again for a seal the store would have taken.
     *
     * It surfaced as a suite flake rather than in a browser: under load the second emission lags far
     * enough that the test's own click lands inside the gap. A phone under load is the same machine.
     *
     * Two layers still refuse an empty audit — this one so the rep is told *why*, and the store's
     * inside the transaction. What they must not do is disagree about which audits are empty, and a
     * snapshot one render old is exactly how they came to.
     */
    const current = (await auditFor(db, visitId)) ?? audit;

    if (!measured(current)) {
      setRefused("empty");

      return;
    }

    // `BR-AUD-7`. Checked at the seal rather than as each answer is given: a rep works a form out of
    // order, and refusing an answer because an earlier one is missing is the screen arguing with them.
    if (unanswered(current, questions).length > 0) {
      setRefused("mandatory");

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

  /*
   * A refusal outlives the tap, but not its cause (found in a browser, W11 slice 9c).
   *
   * "Answer the questions listed above before finishing" sat there after the rep answered, pointing
   * at a list that had just disappeared — the rule was satisfied and the screen still complained.
   * Both refusals are about a state the rep can leave, so both are re-read rather than remembered;
   * `unexpected` is not, because nothing on this screen tells us it has passed.
   */
  const stale =
    (refused === "mandatory" && outstanding.length === 0) || (refused === "empty" && measured(audit));

  return (
    <section className="flex flex-col gap-2">
      {refused && !stale ? (
        <p className="text-sm text-destructive" role="alert">
          {t(`refusal.${refused}`)}
        </p>
      ) : null}

      {/*
        Shown before the rep taps, and named. `BR-AUD-7` is a rule they can satisfy, so the useful
        thing is *which* questions — a count sends them back through the form hunting. The button
        stays pressable for the same reason the order minimum's does: a control that cannot be
        pressed says nothing about why.
      */}
      {outstanding.length > 0 ? (
        <div className="text-sm text-muted-foreground" role="status">
          <p>{t("stillNeeded")}</p>
          {/* Named, because this screen has several live regions and a bare "status" is not one a
              rep — or a test — can ask for by name. The two lists above it are named the same way. */}
          <ul className="list-disc pl-5" aria-label={t("stillNeeded")}>
            {outstanding.map((question) => (
              <li key={question}>{question}</li>
            ))}
          </ul>
        </div>
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
 * What this audit scores, while the rep is still standing there (`AUD-06`, `BR-AUD-4`) — W11 slice 10.
 *
 * <b>The point of showing it here is that the rep can still act on it.</b> A perfect-store score that
 * first appears in a report next week tells somebody else how the shop was; a score at the shelf tells
 * the rep which pillar is short while they can still count the facing they skipped or fetch the case
 * from the back.
 *
 * <b>Scored against the weighting the audit names, never the newest</b> (`BR-AUD-8`). The version is
 * fixed when the draft starts, and a re-weighting that syncs mid-round must not restate what the rep
 * was shown — nor disagree with the number the server will store against that same version.
 *
 * <b>Skipped is not zero, and it says so.</b> `BR-AUD-2`'s renormalisation is invisible in a total, so
 * a rep seeing 80 has no way to know whether share-of-shelf was excellent or never measured. The
 * pillar rows carry that, which is the same breakdown `AUD-09` shows a supervisor.
 */
function Score({ audit }: { audit: LocalAudit }) {
  const t = useTranslations("Field.audit");
  const { db } = useSync();

  /*
   * By version, not "the newest" — `scoreWeightSet` rather than `currentScoreWeightSet`.
   *
   * `undefined` while reading and null when the device does not hold that version, which is a real
   * state rather than an error: a draft started before the weights arrived carries version 0, and a
   * device can hold an audit whose weight set was never pulled. Both are "no score yet", said rather
   * than rendered as a confident zero.
   */
  const weights = useLive(
    async () => (await scoreWeightSet(db, audit.weightSetVersion)) ?? null,
    undefined,
    [db, audit.weightSetVersion],
  );

  if (weights === undefined) return null;

  // A weighting naming a pillar this build cannot compute is treated exactly like one the device does
  // not hold — see `scorable`.
  const scorable = weights === null ? null : scorableWeights(weights.weights);

  if (scorable === null) {
    return (
      <section className="flex flex-col gap-1 rounded-xl border border-border p-3" role="status">
        <h2 className="text-sm font-medium">{t("score.title")}</h2>
        <p className="text-sm text-muted-foreground">{t("score.noWeighting")}</p>
      </section>
    );
  }

  const result = computeScore(scoreInputsFor(audit, scorable));

  return (
    <section className="flex flex-col gap-2 rounded-xl border border-border p-3" role="status">
      <div className="flex items-baseline justify-between">
        <h2 className="text-sm font-medium">{t("score.title")}</h2>
        <p className="text-2xl font-medium tabular-nums" aria-label={t("score.title")}>
          {/*
            Null is "nothing scoreable yet", not nought. An audit whose every pillar was skipped has
            no weighted mean to take, and printing 0 would tell a rep they had failed a shop they have
            not finished measuring.
          */}
          {result.score === null ? t("score.none") : t("score.value", { score: result.score.toFixed(2) })}
        </p>
      </div>

      <ul className="flex flex-col gap-1" aria-label={t("score.pillars")}>
        {result.pillars.map((pillar) => (
          <Pillar key={pillar.pillar} pillar={pillar} />
        ))}
      </ul>
    </section>
  );
}

/**
 * The weighting narrowed to the pillars this build can compute, or **null if it names any it cannot**.
 *
 * `ReferenceScoreWeight.pillar` is a `string` on purpose — the device stores what the server
 * published, including a pillar added after this build shipped. `ScorePillar` is a closed set, so
 * something has to give at this boundary, and the choice is between showing a number and showing
 * nothing.
 *
 * <b>Nothing wins.</b> Dropping an unknown pillar would change the denominator the weighted mean is
 * taken over, so the device would show a confident score the server then contradicts — and `BR-AUD-5`
 * exists precisely to stop the two disagreeing. A rep told "this cannot be scored on your phone" is
 * told something true; a rep told "72%" when the back office will say 61% is not.
 */
function scorableWeights(weights: readonly { pillar: string; percentage: string }[]) {
  const known = (pillar: string): pillar is ScorePillar =>
    (SCORE_PILLARS as readonly string[]).includes(pillar);

  if (!weights.every((weight) => known(weight.pillar))) return null;

  return weights.map((weight) => ({
    pillar: weight.pillar as ScorePillar,
    percentage: weight.percentage,
  }));
}

/** One pillar's row: what it scored, or that it was not measured, and what it is worth. */
function Pillar({ pillar }: { pillar: PillarScore }) {
  const t = useTranslations("Field.audit");

  return (
    <li className="flex items-baseline justify-between gap-2 text-sm">
      <span className="text-muted-foreground">{t(`score.pillar.${pillar.pillar as ScorePillar}`)}</span>
      <span className="tabular-nums">
        {pillar.percentage === null
          ? t("score.skipped")
          : t("score.pillarValue", {
              percentage: pillar.percentage.toFixed(2),
              weight: pillar.weight.toFixed(0),
            })}
      </span>
    </li>
  );
}

/**
 * The questionnaire this screen is putting in front of the rep, or null for none (`AUD-04`).
 *
 * <b>One function because two callers must not disagree.</b> `Survey` renders these questions and
 * `Seal` gates on them (`BR-AUD-7`); a rule enforced against a different set than the one on screen
 * either refuses a question the rep was never asked, or — the way it failed first — lets a rep who
 * scrolled past the questionnaire seal with every mandatory question blank.
 *
 * <b>Auto-choosing the only form is not a shortcut.</b> Nothing in the model says which form applies
 * here: a workflow step carries a type and a label and no form id, and `ISurveyForms` is tenant-wide.
 * With one form there is no choice to make, and a dropdown at every shop offering a single option is
 * a tap that teaches a rep nothing. With several the device has no basis for choosing and says so by
 * asking — and until the rep answers, `BR-AUD-7` has nothing to gate on, which is the honest reading
 * of a model that cannot say which questionnaire this shop was owed. A form-per-channel
 * configuration is the fix, and it is a Configuration change rather than a device one.
 */
function workingForm(
  forms: ReferenceSurveyForm[],
  audit: LocalAudit | null,
): ReferenceSurveyForm | null {
  const chosen = forms.find((form) => form.id === audit?.surveyFormId) ?? null;

  if (chosen !== null) return chosen;

  // `audit?.surveyFormId ?? null` and not `=== null`: before the first tap there is no audit at all,
  // and a rep who works the questionnaire first — the fridge before the shelf — would otherwise find
  // no questions and no way to make any appear.
  return forms.length === 1 && (audit?.surveyFormId ?? null) === null ? forms[0] : null;
}

/**
 * The questionnaire, if this tenant has one (`AUD-04`, `BR-AUD-7`) — W11 slice 9c.
 *
 * <b>Which form, and whether to ask, is `workingForm`'s call</b> — see it for why. With none this
 * renders nothing at all, which is the ordinary case and a legitimate audit: a picker whose only
 * option is "None" is a control that says nothing.
 *
 * <b>Mandatory questions are gated at the seal, not here</b> (`BR-AUD-7`). A rep works a form out of
 * order — the fridge before the shelf, the photo last — and refusing to record an answer because an
 * earlier one is missing would be the screen arguing with them mid-audit.
 */
function Survey({
  audit,
  forms,
  onStart,
  editable,
  queued,
}: {
  audit: LocalAudit | null;
  forms: ReferenceSurveyForm[];
  onStart: () => Promise<LocalAudit>;
  editable: boolean;
  queued: Queued;
}) {
  const t = useTranslations("Field.audit");
  const { db } = useSync();

  if (forms.length === 0) return null;

  const answers = new Map(audit?.answers.map((answer) => [answer.questionKey, answer.value]) ?? []);
  const working = workingForm(forms, audit);

  async function choose(formId: string | null) {
    const current = audit ?? (await onStart());

    await chooseSurvey(db, current.id, formId, new Date());
  }

  async function answer(question: ReferenceSurveyQuestion, value: string) {
    const current = audit ?? (await onStart());

    // The form is set first when the screen chose it for the rep — an answer with no form is
    // refused by the store, and by the server as `MalformedAnswers`.
    if (current.surveyFormId === null && working) {
      await chooseSurvey(db, current.id, working.id, new Date());
    }

    await putAnswer(
      db,
      current.id,
      { questionKey: question.key, questionText: question.text, value },
      new Date(),
    );
  }

  return (
    <section className="flex flex-col gap-2">
      <h2 className="text-sm font-medium">{t("survey")}</h2>

      {forms.length > 1 ? (
        <label className="flex flex-col gap-1 text-sm">
          <span className="text-muted-foreground">{t("chooseSurvey")}</span>
          <select
            className="rounded-md border border-border bg-transparent px-2 py-1"
            disabled={!editable}
            value={audit?.surveyFormId ?? ""}
            aria-label={t("chooseSurvey")}
            onChange={(event) => {
              const value = event.target.value;
              void queued(() => choose(value === "" ? null : value));
            }}
          >
            <option value="">{t("noSurvey")}</option>
            {forms.map((form) => (
              <option key={form.id} value={form.id}>
                {form.name}
              </option>
            ))}
          </select>
        </label>
      ) : null}

      {working === null ? null : (
        <ul aria-label={t("questions")} className="flex flex-col gap-2">
          {[...working.questions]
            .sort((left, right) => left.order - right.order)
            .map((question) => (
              <li
                key={question.key}
                className="flex flex-col gap-2 rounded-xl border border-border p-3 text-sm"
              >
                <span>
                  {question.text}
                  {question.mandatory ? (
                    <span className="text-muted-foreground"> {t("required")}</span>
                  ) : null}
                </span>

                <Question
                  question={question}
                  value={answers.get(question.key) ?? ""}
                  editable={editable}
                  onAnswer={(value) => void queued(() => answer(question, value))}
                />
              </li>
            ))}
        </ul>
      )}
    </section>
  );
}

/**
 * One question's control, by type.
 *
 * <b>Every type stores a string</b>, which is the wire's shape and the server's argument: five
 * nullable columns of which four are always null is the alternative. `Boolean` is `"true"`/`"false"`
 * and `MultiChoice` joins its options — a reader that cares about the type finds it on the question.
 *
 * <b>`Photo` renders as nothing yet</b>, and says so rather than pretending. `OFF-08` is slices
 * 11–13; a text box under a question asking for a picture would collect prose no report can read,
 * which is exactly what `SurveyQuestionType.Photo`'s own comment warns against.
 */
function Question({
  question,
  value,
  editable,
  onAnswer,
}: {
  question: ReferenceSurveyQuestion;
  value: string;
  editable: boolean;
  onAnswer: (value: string) => void;
}) {
  const t = useTranslations("Field.audit");
  const label = t("answerTo", { question: question.text });

  if (question.type === "Boolean") {
    return (
      <div className="flex gap-2">
        {["true", "false"].map((option) => (
          <Button
            key={option}
            type="button"
            size="sm"
            variant={value === option ? "default" : "outline"}
            disabled={!editable}
            aria-pressed={value === option}
            aria-label={t(option === "true" ? "yesTo" : "noTo", { question: question.text })}
            // Tapping the chosen one again un-answers it, as the shelf's three do — and here it also
            // matters for `BR-AUD-7`, which counts an unanswered mandatory question.
            onClick={() => onAnswer(value === option ? "" : option)}
          >
            {t(option === "true" ? "yes" : "no")}
          </Button>
        ))}
      </div>
    );
  }

  if (question.type === "SingleChoice") {
    return (
      <select
        className="rounded-md border border-border bg-transparent px-2 py-1"
        disabled={!editable}
        value={value}
        aria-label={label}
        onChange={(event) => onAnswer(event.target.value)}
      >
        <option value="">{t("noAnswer")}</option>
        {question.options.map((option) => (
          <option key={option} value={option}>
            {option}
          </option>
        ))}
      </select>
    );
  }

  if (question.type === "MultiChoice") {
    // Joined with a separator the options themselves cannot contain, so the value round-trips: a
    // comma would make "Front, centre" indistinguishable from two chosen options.
    const chosen = value === "" ? [] : value.split(MULTI_CHOICE_SEPARATOR);

    return (
      <div className="flex flex-col gap-1">
        {question.options.map((option) => (
          <label key={option} className="flex items-center gap-2">
            <input
              type="checkbox"
              className="size-4"
              disabled={!editable}
              checked={chosen.includes(option)}
              aria-label={t("optionOf", { option, question: question.text })}
              onChange={(event) =>
                onAnswer(
                  (event.target.checked
                    ? [...chosen, option]
                    : chosen.filter((each) => each !== option)
                  ).join(MULTI_CHOICE_SEPARATOR),
                )
              }
            />
            {option}
          </label>
        ))}
      </div>
    );
  }

  if (question.type === "Photo") {
    return (
      <p className="text-xs text-muted-foreground" role="status">
        {t("photoLater")}
      </p>
    );
  }

  return (
    <input
      // `Number` gets a numeric keypad and nothing else: the value crosses as a string either way,
      // and `type="number"` would hand back a `number` on some browsers.
      inputMode={question.type === "Number" ? "decimal" : "text"}
      className="rounded-md border border-border px-2 py-1"
      disabled={!editable}
      defaultValue={value}
      aria-label={label}
      onChange={(event) => onAnswer(event.target.value)}
    />
  );
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
