import "fake-indexeddb/auto";

import { afterEach, beforeEach, describe, expect, it } from "vitest";

import {
  auditFor,
  chooseSurvey,
  clearAvailability,
  draft,
  draftFor,
  putAnswer,
  unanswered,
  putAvailability,
  putCategoryFacings,
  putFacings,
  putPrice,
  seal,
} from "@/lib/audits/local-audit";
import { closeDatabase, FieldKitDatabase } from "@/lib/sync/db";

/**
 * The audit as the device holds it (`AUD-01`, `BR-AUD-6`, `OFF-01b`) — W11 slice 9a.
 *
 * Against a real database, because every claim here is about a transaction: that a draft survives,
 * that an answer replaces rather than appends, and that sealing writes the audit and its outbox row
 * or neither.
 */
const NOW = new Date("2026-03-17T10:15:00.000Z");
const LATER = new Date("2026-03-17T10:20:00.000Z");

const REQUEST = {
  visitId: "visit-1",
  outletId: "outlet-1",
  weightSetVersion: 3,
  now: NOW,
};

let db: FieldKitDatabase;

beforeEach(() => {
  db = new FieldKitDatabase(`audit:${crypto.randomUUID()}`);
});

afterEach(async () => {
  await db.delete();
  closeDatabase();
});

describe("starting an audit", () => {
  it("records the weighting it will be scored against", async () => {
    // `BR-AUD-8`, and the one fact that cannot be recovered later: a re-weighting between the shelf
    // and the push would leave the server unable to say which numbers the rep saw.
    const started = await draftFor(db, REQUEST);

    expect(started.weightSetVersion).toBe(3);
    expect(started.status).toBe("draft");
    expect(started.capturedAtUtc).toBeNull();
  });

  it("returns the existing draft rather than starting a second", async () => {
    // One audit per visit, which is the aggregate's rule server-side (`AlreadyAudited`). A second
    // draft would be the device inventing a conflict the server would then have to refuse.
    const first = await draftFor(db, REQUEST);
    const second = await draftFor(db, { ...REQUEST, weightSetVersion: 9, now: LATER });

    expect(second.id).toBe(first.id);
    expect(second.weightSetVersion).toBe(3);
    expect(await db.audits.count()).toBe(1);
  });
});

describe("answering for a product", () => {
  it("replaces an earlier answer rather than appending", async () => {
    /*
     * A rep who taps *Absent* and then finds the case behind a display has corrected themselves, not
     * measured twice — and the server refuses a product appearing twice in one section
     * (`DuplicateProduct`), so an append would build an audit that cannot be sent.
     */
    const started = await draftFor(db, REQUEST);

    await putAvailability(db, started.id, "p-1", "Absent", NOW);
    const corrected = await putAvailability(db, started.id, "p-1", "Present", LATER);

    expect(corrected?.availability).toEqual([{ productId: "p-1", status: "Present" }]);
  });

  it("un-answers a line rather than making the rep pick a value they do not mean", async () => {
    // All three answers are assertions about the shelf. A rep who tapped the wrong row needs a way
    // back to having said nothing, and an unanswered line is simply not sent.
    const started = await draftFor(db, REQUEST);

    await putAvailability(db, started.id, "p-1", "OutOfStock", NOW);
    const cleared = await clearAvailability(db, started.id, "p-1", LATER);

    expect(cleared?.availability).toEqual([]);
  });

  it("refuses a sealed audit rather than throwing", async () => {
    // `BR-AUD-6` is append-only after sync. A screen a moment behind the store should be told no.
    const started = await draftFor(db, REQUEST);
    await putAvailability(db, started.id, "p-1", "Present", NOW);
    await seal(db, started.id, LATER);

    expect(await putAvailability(db, started.id, "p-2", "Present", LATER)).toBeUndefined();
    expect((await auditFor(db, "visit-1"))?.availability).toHaveLength(1);
  });
});

describe("counting facings", () => {
  it("keeps zero as a measurement and blank as the absence of one", async () => {
    /*
     * `BR-AUD-2`'s numerator. A product with no facings on the shelf is a real reading — it is what
     * an availability gap looks like in the share-of-shelf pillar — while an untouched box means the
     * rep did not count. Collapsing the two would turn every unvisited row into a zero.
     */
    const started = await draftFor(db, REQUEST);

    const counted = await putFacings(db, started.id, "p-1", 0, NOW);
    expect(counted?.facings).toEqual([{ productId: "p-1", facings: 0 }]);

    const cleared = await putFacings(db, started.id, "p-1", null, LATER);
    expect(cleared?.facings).toEqual([]);
  });

  it("replaces a count rather than appending", async () => {
    // The server refuses a product measured twice in one section (`DuplicateProduct`), so a rep who
    // recounts must overwrite — the same rule availability answers follow.
    const started = await draftFor(db, REQUEST);

    await putFacings(db, started.id, "p-1", 4, NOW);
    const recounted = await putFacings(db, started.id, "p-1", 6, LATER);

    expect(recounted?.facings).toEqual([{ productId: "p-1", facings: 6 }]);
  });

  it("refuses a negative or fractional count", async () => {
    // `NegativeCount` server-side, and a facing is one product's front on a shelf — there is no half
    // of one. Refused here so the audit cannot be built into a shape that will be rejected.
    const started = await draftFor(db, REQUEST);

    expect(await putFacings(db, started.id, "p-1", -1, NOW)).toBeUndefined();
    expect(await putFacings(db, started.id, "p-1", 2.5, NOW)).toBeUndefined();
    expect((await auditFor(db, "visit-1"))?.facings).toEqual([]);
  });

  it("keeps the category total null until it is counted", async () => {
    /*
     * `BR-AUD-2`'s denominator, and the distinction W10 slice 0 settled: without it share-of-shelf is
     * *skipped* and the score renormalises over what was measured. A zero would say the shop has none
     * of the category, which is a different claim and a much worse one.
     */
    const started = await draftFor(db, REQUEST);
    expect(started.categoryFacings).toBeNull();

    const counted = await putCategoryFacings(db, started.id, 40, NOW);
    expect(counted?.categoryFacings).toBe(40);

    const uncounted = await putCategoryFacings(db, started.id, null, LATER);
    expect(uncounted?.categoryFacings).toBeNull();
  });
});

describe("reading a shelf price", () => {
  it("stores the expected price beside the observation rather than re-deriving it", async () => {
    /*
     * `BR-AUD-3` compares against the price resolved for that outlet *and date*. Storing what the
     * device resolved is what stops a list republished between the shelf and the seal from moving
     * the number the rep is judged by — the same as-of-capture reasoning the server applies when it
     * refuses to re-resolve on arrival.
     */
    const started = await draftFor(db, REQUEST);

    const read = await putPrice(
      db,
      started.id,
      { productId: "p-1", observed: "4.79", expected: "4.50", currencyCode: "RON" },
      NOW,
    );

    expect(read?.prices).toEqual([
      { productId: "p-1", observed: "4.79", expected: "4.50", currencyCode: "RON" },
    ]);
  });

  it("keeps an observation for a product no list covers", async () => {
    // Not a compliance failure — the server scores nothing against a null expected — but a real
    // reading, and the only evidence that the price list has a gap here.
    const started = await draftFor(db, REQUEST);

    const read = await putPrice(
      db,
      started.id,
      { productId: "p-1", observed: "4.79", expected: null, currencyCode: "RON" },
      NOW,
    );

    expect(read?.prices[0].expected).toBeNull();
  });

  it("removes the reading when the rep clears the box", async () => {
    const started = await draftFor(db, REQUEST);

    await putPrice(
      db,
      started.id,
      { productId: "p-1", observed: "4.79", expected: "4.50", currencyCode: "RON" },
      NOW,
    );

    const cleared = await putPrice(db, started.id, { productId: "p-1", observed: null }, LATER);

    expect(cleared?.prices).toEqual([]);
  });
});

describe("the questionnaire", () => {
  const QUESTIONS = [
    { key: "chiller", text: "Is the chiller working?", mandatory: true },
    { key: "notes", text: "Anything else?", mandatory: false },
  ];

  it("refuses an answer that names no form", async () => {
    // `MalformedAnswers` server-side: an answer with no questionnaire behind it is uninterpretable,
    // so it is refused here rather than built into an audit that cannot be sent.
    const started = await draftFor(db, REQUEST);

    expect(
      await putAnswer(
        db,
        started.id,
        { questionKey: "chiller", questionText: "Is the chiller working?", value: "true" },
        NOW,
      ),
    ).toBeUndefined();
  });

  it("replaces an answer rather than appending, and an empty value removes it", async () => {
    /*
     * Two answers under one key is `MalformedAnswers` too — and an empty string is not an answer,
     * so storing one would let a mandatory question pass `BR-AUD-7`'s gate while saying nothing.
     */
    const started = await draftFor(db, REQUEST);
    await chooseSurvey(db, started.id, "form-1", NOW);

    await putAnswer(db, started.id, { questionKey: "chiller", questionText: "Q", value: "true" }, NOW);
    const changed = await putAnswer(
      db,
      started.id,
      { questionKey: "chiller", questionText: "Q", value: "false" },
      LATER,
    );

    expect(changed?.answers).toEqual([
      { questionKey: "chiller", questionText: "Q", value: "false" },
    ]);

    const cleared = await putAnswer(
      db,
      started.id,
      { questionKey: "chiller", questionText: "Q", value: "  " },
      LATER,
    );

    expect(cleared?.answers).toEqual([]);
  });

  it("discards the answers when the form changes", async () => {
    /*
     * An answer is filed under a question *key*, and two forms can use the same key for different
     * questions. Carrying them across would attach a rep's answer to a question they never read.
     */
    const started = await draftFor(db, REQUEST);
    await chooseSurvey(db, started.id, "form-1", NOW);
    await putAnswer(db, started.id, { questionKey: "chiller", questionText: "Q", value: "true" }, NOW);

    const moved = await chooseSurvey(db, started.id, "form-2", LATER);

    expect(moved?.surveyFormId).toBe("form-2");
    expect(moved?.answers).toEqual([]);
  });

  it("keeps the answers when the same form is chosen again", async () => {
    // A screen re-asserting the form it already set must not wipe a rep's work — and the screen does
    // exactly that when it chooses the tenant's only form on the rep's behalf.
    const started = await draftFor(db, REQUEST);
    await chooseSurvey(db, started.id, "form-1", NOW);
    await putAnswer(db, started.id, { questionKey: "chiller", questionText: "Q", value: "true" }, NOW);

    const again = await chooseSurvey(db, started.id, "form-1", LATER);

    expect(again?.answers).toHaveLength(1);
  });

  it("names the mandatory questions still unanswered (BR-AUD-7)", async () => {
    /*
     * Enforced on the device and deliberately not on the server: `IAuditIngest` would test the
     * answers against the questionnaire as it reads *today*, so a form that gained a mandatory
     * question after the rep answered would refuse an audit for a question that did not exist.
     *
     * Named rather than counted — "2 still needed" sends a rep back through a form hunting.
     */
    const started = await draftFor(db, REQUEST);
    await chooseSurvey(db, started.id, "form-1", NOW);

    expect(unanswered((await auditFor(db, "visit-1"))!, QUESTIONS)).toEqual([
      "Is the chiller working?",
    ]);

    await putAnswer(db, started.id, { questionKey: "chiller", questionText: "Q", value: "true" }, NOW);

    expect(unanswered((await auditFor(db, "visit-1"))!, QUESTIONS)).toEqual([]);
  });

  it("asks nothing of an audit with no questionnaire", async () => {
    // Most audits are a shelf and no form. `BR-AUD-7` has nothing to say about them, and a gate that
    // fired anyway would make every audit need a survey. "No questionnaire" is no questions — the
    // caller's call, because only the caller knows which form the rep is looking at.
    const started = await draftFor(db, REQUEST);

    expect(unanswered(started, [])).toEqual([]);
  });

  it("owes the mandatory questions even before the rep has named a form", async () => {
    /*
     * The hole this closes: an audit names no form until the first answer lands, and the rule used to
     * skip an audit whose `surveyFormId` was null. So the one rep it excused — the one who scrolled
     * past the questionnaire and answered nothing at all — was exactly the one `BR-AUD-7` is about.
     */
    const started = await draftFor(db, REQUEST);

    expect(started.surveyFormId).toBeNull();
    expect(unanswered(started, QUESTIONS)).toEqual(["Is the chiller working?"]);
  });
});

describe("sealing", () => {
  it("writes the audit and its outbox row in one transaction", async () => {
    /*
     * The two are one fact. Split, a crash produces either an audit the rep believes was sent and
     * never was, or a mutation for an audit still showing as editable.
     */
    const started = await draftFor(db, REQUEST);
    await putAvailability(db, started.id, "p-1", "Present", NOW);

    const sealed = await seal(db, started.id, LATER);

    expect(sealed?.status).toBe("sealed");
    expect(sealed?.capturedAtUtc).toBe(LATER.toISOString());

    const queued = (await db.outbox.toArray())[0];

    expect(queued.type).toBe("CapturedAudit");
    expect(queued.subjectId).toBe(started.id);
    expect(queued.status).toBe("pending");
  });

  it("sends the shape the server reads, with the status as a name", async () => {
    /*
     * `AvailabilityStatus` carries `JsonStringEnumConverter` on the enum itself, so the server reads
     * `"Present"` and not `0`. An ordinal here would be read as `Present` whatever the rep tapped —
     * silent, and wrong in the direction that flatters the shop.
     *
     * `categoryFacings` is null, which `BR-AUD-2` reads as *not captured*: the share-of-shelf pillar
     * is skipped rather than scored zero, which is right for an audit that never offered to count it.
     */
    const started = await draftFor(db, REQUEST);
    await putAvailability(db, started.id, "p-1", "OutOfStock", NOW);

    await seal(db, started.id, LATER);

    expect((await db.outbox.toArray())[0].payload).toEqual({
      auditId: started.id,
      visitId: "visit-1",
      capturedAtUtc: LATER.toISOString(),
      weightSetVersion: 3,
      categoryFacings: null,
      availability: [{ productId: "p-1", status: "OutOfStock" }],
      facings: [],
      prices: [],
      // Both null: `CapturedAudit` reads a null form with null answers as an audit that had no
      // survey step, and the server refuses answers naming no form (`MalformedAnswers`).
      surveyFormId: null,
      answers: null,
    });
  });

  it("sends prices as whole minor units, rounded half-up first", async () => {
    /*
     * `CapturedPrice` takes `long` minor units, not a decimal — the discipline `BR-PRD-8`/`BR-PRD-9`
     * already impose, applied to the one field `BR-AUD-3` judges compliance from.
     *
     * `4.795` is the case worth pinning: rounded half-up to the currency's minor units it is `4.80`
     * and therefore `480`. Multiplying first would give `479.5`, which `long` truncates to `479` —
     * silently, and in the shop's favour.
     */
    const started = await draftFor(db, REQUEST);

    await putPrice(
      db,
      started.id,
      { productId: "p-1", observed: "4.795", expected: "4.50", currencyCode: "RON" },
      NOW,
    );

    await seal(db, started.id, LATER);

    expect((await db.outbox.toArray())[0].payload).toMatchObject({
      prices: [
        {
          productId: "p-1",
          observedMinorUnits: 480,
          expectedMinorUnits: 450,
          currency: "RON",
        },
      ],
    });
  });

  it("sends the category total it was given, including none", async () => {
    // `BR-AUD-2`: null reaches the server as *not captured*, and the share-of-shelf pillar is
    // skipped rather than scored zero.
    const started = await draftFor(db, REQUEST);
    await putFacings(db, started.id, "p-1", 6, NOW);
    await putCategoryFacings(db, started.id, 40, NOW);

    await seal(db, started.id, LATER);

    expect((await db.outbox.toArray())[0].payload).toMatchObject({
      categoryFacings: 40,
      facings: [{ productId: "p-1", facings: 6 }],
    });
  });

  it("seals an audit that only counted facings", async () => {
    /*
     * 9a refused anything without an availability answer, which was right when availability was the
     * only thing this screen captured. `BR-AUD-2` and `BR-AUD-3` are pillars in their own right, so
     * a rep who counted the shelf and read the labels without ticking a row has done real work — and
     * the score renormalises over the pillars that were measured.
     */
    const started = await draftFor(db, REQUEST);
    await putFacings(db, started.id, "p-1", 6, NOW);

    const sealed = await seal(db, started.id, LATER);

    expect(sealed?.status).toBe("sealed");
    expect(await db.outbox.count()).toBe(1);
  });

  it("sends the form and the answers, or a pair of nulls", async () => {
    /*
     * Both or neither. `CapturedAudit` reads a null form with null answers as an audit that had no
     * survey step, and the server refuses answers naming no form — so an audit with a form and no
     * answers must not become a form id with an empty list, which would claim the rep opened a
     * questionnaire and answered nothing.
     */
    const started = await draftFor(db, REQUEST);
    await chooseSurvey(db, started.id, "form-1", NOW);
    await putAnswer(
      db,
      started.id,
      { questionKey: "chiller", questionText: "Is the chiller working?", value: "true" },
      NOW,
    );

    await seal(db, started.id, LATER);

    expect((await db.outbox.toArray())[0].payload).toMatchObject({
      surveyFormId: "form-1",
      answers: [
        { questionKey: "chiller", questionText: "Is the chiller working?", value: "true" },
      ],
    });
  });

  it("seals an audit that is only a questionnaire", async () => {
    // `AUD-04` is a section of the audit in its own right: a shop with nothing on the shelf can
    // still have a form filled in about the display, the fridge and the signage.
    const started = await draftFor(db, REQUEST);
    await chooseSurvey(db, started.id, "form-1", NOW);
    await putAnswer(db, started.id, { questionKey: "chiller", questionText: "Q", value: "true" }, NOW);

    expect((await seal(db, started.id, LATER))?.status).toBe("sealed");
  });

  it("still refuses one that carries only a category total", async () => {
    // A denominator with no numerator above it. There is no share to compute, and an audit saying
    // only "the shelf has 40 facings" has measured nothing about this tenant's products.
    const started = await draftFor(db, REQUEST);
    await putCategoryFacings(db, started.id, 40, NOW);

    expect(await seal(db, started.id, LATER)).toBeUndefined();
    expect(await db.outbox.count()).toBe(0);
  });

  it("refuses an audit that measured nothing", async () => {
    // The server refuses it too (`Empty`). Letting it reach the wire would cost a rep a round trip —
    // and a `failed` outbox row nothing retries — to be told something the device already knew.
    const started = await draftFor(db, REQUEST);

    expect(await seal(db, started.id, LATER)).toBeUndefined();
    expect(await db.outbox.count()).toBe(0);
    expect((await auditFor(db, "visit-1"))?.status).toBe("draft");
  });

  it("refuses to seal twice", async () => {
    const started = await draftFor(db, REQUEST);
    await putAvailability(db, started.id, "p-1", "Present", NOW);
    await seal(db, started.id, LATER);

    expect(await seal(db, started.id, LATER)).toBeUndefined();
    expect(await db.outbox.count()).toBe(1);
  });

  it("stops answering `draft` once sealed, while still answering `auditFor`", async () => {
    /*
     * The distinction `orderFor` was written for. "What may still be edited" has to go quiet at the
     * seal — that is `BR-AUD-6` as the store sees it — while "show me what I sent" must not, or a
     * screen bound to the first tells a rep who has just finished that they never started.
     */
    const started = await draftFor(db, REQUEST);
    await putAvailability(db, started.id, "p-1", "Present", NOW);
    await seal(db, started.id, LATER);

    expect(await draft(db, "visit-1")).toBeUndefined();
    expect((await auditFor(db, "visit-1"))?.id).toBe(started.id);
  });
});
