import type { PillarWeight, ScoreInputs } from "@/lib/audits/score";
import { Decimal, Money } from "@/lib/pricing/money";
import type {
  FieldKitDatabase,
  LocalAnswer,
  LocalAudit,
  LocalAvailabilityStatus,
  LocalPriceCheck,
} from "@/lib/sync/db";

/**
 * The audit a rep works at a shelf, and the moment they seal it (`AUD-01`, `OFF-01b`, `BR-AUD-6`) —
 * W11 slice 9a.
 *
 * <b>This is the store, not the screen.</b> What lives here has to be true whether or not anything
 * is rendering: a draft that survives a reload, a closed tab and an app update (`OFF-13`), and a
 * seal that puts the audit in the outbox in the same transaction that marks it sealed.
 *
 * <b>Why a draft is a device state and has no server equivalent.</b> There is no back-office
 * "enter an audit" screen and no live capture endpoint — `IAuditIngest` is the module's only write
 * path, and it takes an audit that already happened. So an audit lost before it is sealed is work
 * that existed nowhere else, which is what makes this a store rather than an unsent outbox payload.
 */

/** What a caller asks for. The device supplies the identity, the status and the clock. */
export type DraftRequest = {
  visitId: string;
  outletId: string;
  /**
   * The weighting this audit will be scored against (`BR-AUD-8`).
   *
   * Fixed when the draft starts rather than when it is sealed, and that is the whole point: a rep
   * who begins an audit before a re-weighting syncs and finishes it after must be scored on the
   * numbers they were shown. Read once, here, through `currentScoreWeightSet`; every later read goes
   * through `scoreWeightSet` with *this* version.
   */
  weightSetVersion: number;
  now: Date;
};

/**
 * The draft for a visit, starting one if there is none (`BR-AUD-6`).
 *
 * <b>At most one audit per visit</b>, which is the aggregate's rule server-side (`AlreadyAudited`)
 * and is enforced here by making "start" mean "get or start". A second draft would be the device
 * inventing a conflict the server would then have to refuse.
 */
export async function draftFor(
  db: FieldKitDatabase,
  request: DraftRequest,
): Promise<LocalAudit> {
  return db.transaction("rw", db.audits, async () => {
    const existing = await db.audits.where("visitId").equals(request.visitId).first();
    if (existing) return existing;

    const draft: LocalAudit = {
      id: crypto.randomUUID(),
      visitId: request.visitId,
      outletId: request.outletId,
      status: "draft",
      weightSetVersion: request.weightSetVersion,
      availability: [],
      facings: [],
      // Null, not zero. `BR-AUD-2` skips the share-of-shelf pillar without a total; a zero would
      // score the shop as having none of the category, which is a different and much worse claim.
      categoryFacings: null,
      prices: [],
      surveyFormId: null,
      answers: [],
      photos: [],
      capturedAtUtc: null,
      updatedAtUtc: request.now.toISOString(),
    };

    await db.audits.add(draft);

    return draft;
  });
}

/** The draft for a visit, or undefined. Never a sealed one — see {@link auditFor}. */
export function draft(
  db: FieldKitDatabase,
  visitId: string,
): Promise<LocalAudit | undefined> {
  return db.audits
    .where("visitId")
    .equals(visitId)
    .filter((candidate) => candidate.status === "draft")
    .first();
}

/**
 * The visit's audit whatever state it is in.
 *
 * A second query rather than a flag on {@link draft}, for the reason `orderFor` gives: "what may
 * still be edited" has to keep answering nothing once the audit is sealed, which is `BR-AUD-6` as
 * the store sees it — while "show me what I sent" is a different question. Conflating them makes a
 * sealed audit render as one nobody has started.
 */
export function auditFor(
  db: FieldKitDatabase,
  visitId: string,
): Promise<LocalAudit | undefined> {
  return db.audits.where("visitId").equals(visitId).first();
}

/**
 * Records how one MSL product was found, replacing any earlier answer for it (`AUD-01`).
 *
 * <b>Upsert, not append.</b> A rep who taps *Absent* and then finds the case behind a display has
 * corrected themselves, not measured twice — and the server refuses a product that appears twice in
 * one section (`DuplicateProduct`), so an append would build an audit that cannot be sent.
 *
 * Refuses a sealed audit rather than throwing: `BR-AUD-6` is append-only after sync, and a screen
 * that is a moment behind the store should be told no, not crash.
 */
export async function putAvailability(
  db: FieldKitDatabase,
  auditId: string,
  productId: string,
  status: LocalAvailabilityStatus,
  now: Date,
): Promise<LocalAudit | undefined> {
  return db.transaction("rw", db.audits, async () => {
    const current = await db.audits.get(auditId);
    if (!current || current.status !== "draft") return undefined;

    const others = current.availability.filter((line) => line.productId !== productId);

    return save(db, current, { availability: [...others, { productId, status }] }, now);
  });
}

/**
 * Un-answers one product.
 *
 * Worth having rather than making the rep pick a third value they do not mean: *Present*, *Absent*
 * and *OutOfStock* are all assertions about the shelf, and a rep who tapped the wrong row needs a
 * way back to having said nothing. An unanswered MSL line is simply not sent.
 */
export async function clearAvailability(
  db: FieldKitDatabase,
  auditId: string,
  productId: string,
  now: Date,
): Promise<LocalAudit | undefined> {
  return db.transaction("rw", db.audits, async () => {
    const current = await db.audits.get(auditId);
    if (!current || current.status !== "draft") return undefined;

    return save(
      db,
      current,
      { availability: current.availability.filter((line) => line.productId !== productId) },
      now,
    );
  });
}

/**
 * Seals the audit and queues it (`BR-AUD-6`, `OFF-04`).
 *
 * <b>One transaction, both writes</b> — the same bargain `submit` and `checkOut` make. The audit
 * becoming sealed and the mutation existing are one fact, and splitting them leaves a window where a
 * crash produces either an audit the rep believes was sent and never was, or a mutation for an audit
 * still showing as editable.
 *
 * <b>An audit that measured nothing is refused here rather than at the server.</b> `AuditRefusal`
 * has an `Empty` for exactly this, and letting it reach the wire would cost a rep a round trip — and
 * a *failed* outbox row that nothing retries — to be told something the device already knew.
 */
export async function seal(
  db: FieldKitDatabase,
  auditId: string,
  now: Date,
): Promise<LocalAudit | undefined> {
  return db.transaction("rw", db.audits, db.outbox, async () => {
    const current = await db.audits.get(auditId);
    if (!current || current.status !== "draft" || !measured(current)) return undefined;

    const sealed: LocalAudit = {
      ...current,
      status: "sealed",
      capturedAtUtc: now.toISOString(),
      updatedAtUtc: now.toISOString(),
    };

    await db.audits.put(sealed);

    // Enqueued inline rather than through `enqueue`, which opens its own write — that would put the
    // outbox row outside this transaction and reintroduce the window it exists to close.
    await db.outbox.add({
      mutationId: crypto.randomUUID(),
      type: "CapturedAudit",
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
 * Records how many facings one product has, or removes the count (`AUD-02`) — W11 slice 9b.
 *
 * <b>Upsert, and `null` removes.</b> A rep who counted and then recounted has corrected themselves,
 * and one who typed into the wrong row needs a way back to having counted nothing — the same shape
 * `putAvailability` and `clearAvailability` have, folded into one call because a facings count has a
 * natural empty and a status does not.
 *
 * <b>Zero is a count, not an absence.</b> A product with no facings on the shelf is a real
 * measurement and the numerator `BR-AUD-2` wants; leaving the box blank is the one that means
 * *not measured*. The server refuses a negative (`NegativeCount`) and this refuses it too.
 */
export async function putFacings(
  db: FieldKitDatabase,
  auditId: string,
  productId: string,
  facings: number | null,
  now: Date,
): Promise<LocalAudit | undefined> {
  return db.transaction("rw", db.audits, async () => {
    const current = await db.audits.get(auditId);
    if (!current || current.status !== "draft") return undefined;
    if (facings !== null && (!Number.isInteger(facings) || facings < 0)) return undefined;

    const others = current.facings.filter((line) => line.productId !== productId);

    return save(db, current, {
      facings: facings === null ? others : [...others, { productId, facings }],
    }, now);
  });
}

/**
 * Records the total facings in the category — share-of-shelf's denominator (`BR-AUD-2`).
 *
 * <b>One number for the whole audit, and `null` is its default.</b> Without it the pillar is skipped
 * rather than faked: the score renormalises over what *was* measured, so a rep who could not count
 * the shelf has said something true. Setting it back to null is how they take that back.
 */
export async function putCategoryFacings(
  db: FieldKitDatabase,
  auditId: string,
  categoryFacings: number | null,
  now: Date,
): Promise<LocalAudit | undefined> {
  return db.transaction("rw", db.audits, async () => {
    const current = await db.audits.get(auditId);
    if (!current || current.status !== "draft") return undefined;

    if (categoryFacings !== null
        && (!Number.isInteger(categoryFacings) || categoryFacings < 0)) {
      return undefined;
    }

    return save(db, current, { categoryFacings }, now);
  });
}

/**
 * Records the shelf price the rep read, against what the device expected (`AUD-03`) — W11 9b.
 *
 * <b>`expected` and `currencyCode` come from the caller, resolved once when the screen loaded.</b>
 * They are stored beside the observation rather than re-derived at the seal, because `BR-AUD-3`
 * compares against the price resolved *for that outlet and date* — and a list republished between
 * the rep reading the shelf edge and sealing would otherwise move the number they were judged by.
 *
 * <b>An observation with no expected price is still worth storing.</b> It is not a compliance
 * failure — the server takes a null expected and scores nothing — but it is a real reading, and
 * throwing it away would lose the one piece of evidence that the list has a gap.
 */
export async function putPrice(
  db: FieldKitDatabase,
  auditId: string,
  check: LocalPriceCheck | { productId: string; observed: null },
  now: Date,
): Promise<LocalAudit | undefined> {
  return db.transaction("rw", db.audits, async () => {
    const current = await db.audits.get(auditId);
    if (!current || current.status !== "draft") return undefined;

    const others = current.prices.filter((line) => line.productId !== check.productId);

    return save(db, current, {
      prices: check.observed === null ? others : [...others, check as LocalPriceCheck],
    }, now);
  });
}

/**
 * Says which questionnaire this audit is working (`AUD-04`) — W11 slice 9c.
 *
 * <b>Changing form discards the answers, and that is the honest behaviour.</b> An answer is filed
 * under a question *key*, and two forms can use the same key for different questions — carrying them
 * across would attach a rep's answer to a question they never read. `null` clears the survey
 * entirely, which is what a rep who opened the wrong form needs.
 */
export async function chooseSurvey(
  db: FieldKitDatabase,
  auditId: string,
  surveyFormId: string | null,
  now: Date,
): Promise<LocalAudit | undefined> {
  return db.transaction("rw", db.audits, async () => {
    const current = await db.audits.get(auditId);
    if (!current || current.status !== "draft") return undefined;
    if (current.surveyFormId === surveyFormId) return current;

    return save(db, current, { surveyFormId, answers: [] }, now);
  });
}

/**
 * Records one answer, replacing any earlier one for that question (`AUD-04`).
 *
 * <b>The question's text is stored with it</b>, not looked up at the seal: the form may be reworded
 * or the question dropped between the rep answering and the push landing, and an answer that needs
 * the form re-read to mean anything is one a reader cannot trust. The server makes the same call.
 *
 * <b>An empty value removes the answer.</b> A rep who typed into the wrong box needs a way back to
 * having answered nothing — and for `BR-AUD-7` an empty string is not an answer, so storing one
 * would let a mandatory question pass its own gate.
 */
export async function putAnswer(
  db: FieldKitDatabase,
  auditId: string,
  answer: LocalAnswer,
  now: Date,
): Promise<LocalAudit | undefined> {
  return db.transaction("rw", db.audits, async () => {
    const current = await db.audits.get(auditId);
    if (!current || current.status !== "draft") return undefined;

    // An answer with no form is uninterpretable — `MalformedAnswers` server-side — so it is refused
    // here rather than built into an audit that cannot be sent.
    if (current.surveyFormId === null) return undefined;

    const others = current.answers.filter((line) => line.questionKey !== answer.questionKey);
    const value = answer.value.trim();

    return save(
      db,
      current,
      { answers: value === "" ? others : [...others, { ...answer, value }] },
      now,
    );
  });
}

/**
 * The mandatory questions this audit has not answered (`BR-AUD-7`) — W11 slice 9c.
 *
 * <b>Enforced here and deliberately not on the server.</b> "Mandatory survey questions must be
 * answered before the audit step completes" is a rule about *completing a step*, which happens with
 * the rep looking at the form. Re-checking on arrival would test the answers against the
 * questionnaire as it reads **today** — so a form that gained a mandatory question after the rep
 * answered would refuse an audit for a question that did not exist when they worked the shelf, and
 * one that dropped a question would refuse an audit for an answer that was mandatory at the time.
 * `IAuditIngest` says exactly this.
 *
 * <b>Takes the questions rather than reading them</b>, so the rule stays pure: the caller holds the
 * form the rep is looking at, which is the one the answers belong to. An audit with no questionnaire
 * is a caller passing no questions — and *not* a check on `surveyFormId`, which is how this first
 * went wrong: an audit names no form until the rep answers something, so skipping the rule for a
 * null form skipped it for precisely the rep who had answered nothing.
 */
export function unanswered(
  audit: LocalAudit,
  questions: readonly { key: string; text: string; mandatory: boolean }[],
): string[] {
  const answered = new Set(audit.answers.map((answer) => answer.questionKey));

  return questions
    .filter((question) => question.mandatory && !answered.has(question.key))
    .map((question) => question.text);
}

async function save(
  db: FieldKitDatabase,
  current: LocalAudit,
  change: Partial<
    Pick<
      LocalAudit,
      "availability" | "facings" | "categoryFacings" | "prices" | "surveyFormId" | "answers"
    >
  >,
  now: Date,
): Promise<LocalAudit> {
  const updated: LocalAudit = {
    ...current,
    ...change,
    updatedAtUtc: now.toISOString(),
  };

  await db.audits.put(updated);

  return updated;
}

/**
 * The audit as `/sync/push` expects it (`vectors/sync/push.v1.json`).
 *
 * <b>`status` travels as a name</b> — `AvailabilityStatus` carries `JsonStringEnumConverter` on the
 * enum itself, so the server reads `"Present"` and not `0`. The local strings are already those
 * names, which is why this is a copy rather than a mapping: a lookup table here would be a second
 * place the two vocabularies have to agree.
 *
 * <b>The three empty fields are 9b's and 9c's, and are sent explicitly rather than omitted.</b>
 * `CapturedAudit` defaults `answers` and `photos` to null but takes `facings` and `prices` as
 * required lists, so an audit with no numbers is one that measured no facings and read no prices —
 * which is exactly true today and stops being true two slices from now. `categoryFacings` is null,
 * which `BR-AUD-2` reads as *not captured*: the share-of-shelf pillar is skipped rather than scored
 * zero, and that is the right answer for an audit that never offered to count it.
 */
function captured(audit: LocalAudit): Record<string, unknown> {
  return {
    auditId: audit.id,
    visitId: audit.visitId,
    capturedAtUtc: audit.capturedAtUtc,
    weightSetVersion: audit.weightSetVersion,
    categoryFacings: audit.categoryFacings,
    availability: audit.availability.map((line) => ({
      productId: line.productId,
      status: line.status,
    })),
    facings: audit.facings.map((line) => ({
      productId: line.productId,
      facings: line.facings,
    })),
    prices: audit.prices.map((line) => ({
      productId: line.productId,
      observedMinorUnits: minorUnits(line.observed, line.currencyCode),
      expectedMinorUnits:
        line.expected === null ? null : minorUnits(line.expected, line.currencyCode),
      currency: line.currencyCode,
    })),
    /*
     * Both or neither. `CapturedAudit` defaults them to null and the server refuses answers that
     * name no form (`MalformedAnswers`), so an audit with no survey sends nulls rather than a form
     * id with an empty list — which would claim the rep opened a questionnaire and answered nothing.
     */
    surveyFormId: audit.surveyFormId,
    answers:
      audit.surveyFormId === null
        ? null
        : audit.answers.map((answer) => ({
            questionKey: answer.questionKey,
            questionText: answer.questionText,
            value: answer.value,
          })),
    /*
     * References, never images (`AUD-05`, `B5`) — W11 slice 11.
     *
     * The pictures go to object storage on their own schedule and this push regularly wins the race,
     * so an audit routinely lands naming objects that do not exist yet. `CapturedPhoto` says so at
     * length: a reader shows a gap rather than an error.
     */
    photos: audit.photos.map((photo) => ({
      section: photo.section,
      objectKey: photo.objectKey,
    })),
  };
}

/**
 * The audit as `computeScore` reads it (`AUD-06`, `BR-AUD-5`) — W11 slice 10.
 *
 * <b>Built from `captured()`'s own numbers, not from the draft's decimals.</b> The server scores the
 * entries it stored, which are the minor units this device pushed; scoring the strings here instead
 * would give the rep a number derived from a different rounding than the one the server will use, and
 * `BR-AUD-5` requires the two to agree **exactly**. So the price lines go through the same
 * `minorUnits` the wire does — one conversion, one place to be wrong.
 *
 * <b>No tolerance, because the server passes none.</b> `Audit.Ingest` calls `PerfectStoreScore` with
 * the default `PriceToleranceMinorUnits = 0`, so a device that allowed a ban either way would show a
 * compliance percentage the server then contradicts. When a tolerance becomes configuration it has to
 * arrive here from the same place it arrives there.
 */
export function scoreInputsFor(
  audit: LocalAudit,
  weights: readonly PillarWeight[],
): ScoreInputs {
  return {
    availability: audit.availability.map((line) => ({ status: line.status })),
    facings: audit.facings.map((line) => ({ facings: line.facings })),
    categoryFacings: audit.categoryFacings,
    prices: audit.prices.map((line) => ({
      observedMinorUnits: minorUnits(line.observed, line.currencyCode),
      expectedMinorUnits: line.expected === null ? null : minorUnits(line.expected, line.currencyCode),
    })),
    weights,
  };
}

/**
 * A decimal amount as whole minor units — the shape `CapturedPrice` takes.
 *
 * <b>The only place in this module a decimal becomes a number, deliberately.</b> `local-order.ts`
 * makes the same argument about its own conversion: every arithmetic the rep sees happens in
 * `decimal.js`, and the value crosses `Number` exactly once, already scaled to an integer, at a
 * magnitude where the conversion is exact.
 *
 * <b>Rounded to the currency's minor units first, half-up.</b> `Money.round` is `BR-PRD-9`'s policy,
 * and going straight to `times(100)` would turn a stray third decimal into a fractional minor unit —
 * which `long` on the server truncates silently and in the shop's favour.
 */
function minorUnits(amount: string, currencyCode: string): number {
  const money = Money.of(amount, currencyCode).round();

  return money.amount.times(new Decimal(10).pow(money.minorUnits)).toNumber();
}

/**
 * Whether the audit measured anything at all.
 *
 * <b>Any of the three counts, which is wider than 9a's availability-only check.</b> The server's
 * `Empty` refusal is about an audit that recorded nothing, and a rep who counted facings and read
 * prices without ticking a single availability line has done real work — `BR-AUD-2` and `BR-AUD-3`
 * are pillars in their own right, and the score renormalises over the ones that were measured.
 *
 * <b>A category total alone does not count.</b> It is a denominator: without facings above it there
 * is no share to compute, and an audit carrying nothing but "the shelf has 40 facings on it" has
 * measured nothing about *this tenant's* products.
 */
export function measured(audit: LocalAudit): boolean {
  return (
    audit.availability.length > 0 ||
    audit.facings.length > 0 ||
    audit.prices.length > 0 ||
    // A questionnaire the rep worked is a measurement too (W11 slice 9c) — `AUD-04` is a whole
    // section of the audit, and a shop with nothing on the shelf can still have a form filled in
    // about the display, the fridge and the signage.
    audit.answers.length > 0 ||
    /*
     * And so is a photograph (W11 slice 11). `Audit.Check` counts them explicitly — "an audit that is
     * only a questionnaire, or only a photograph of a display, is real work" — so a device that
     * refused one would refuse an audit the server would have taken, which is the shape of bug 9b
     * shipped and 9c repeated.
     */
    audit.photos.length > 0
  );
}
