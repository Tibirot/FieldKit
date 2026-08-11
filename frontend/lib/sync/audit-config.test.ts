import "fake-indexeddb/auto";

import { afterEach, describe, expect, it } from "vitest";

import { Decimal } from "@/lib/pricing/money";

import { closeDatabase, FieldKitDatabase } from "./db";
import {
  applyScoreWeightChanges,
  applySurveyChanges,
  currentScoreWeightSet,
  SCORE_WEIGHTS,
  scoreWeightSet,
  SURVEYS,
  surveyForm,
  surveyForms,
  watermark,
} from "./reference";

/**
 * Survey forms and perfect-store weightings on the device (`OFF-03`) — W10 slice 7.
 *
 * Two reference stores, applied by the same transactional helper as every other. What is worth
 * testing is not the plumbing — `db.test.ts` covers that — but the two things specific to these:
 * that a weighting is looked up **by version** rather than by recency, and that its percentages
 * survive the round trip as strings a decimal can read.
 */
function freshDatabase(): FieldKitDatabase {
  return new FieldKitDatabase(`test:${crypto.randomUUID()}`);
}

function form(id: string, name: string, rowVersion: number) {
  return {
    id,
    name,
    questions: [
      { order: 1, key: "chiller_lit", text: "Is the chiller lit?", type: "Boolean", mandatory: true, options: [] },
      {
        order: 2,
        key: "quality",
        text: "Facing quality?",
        type: "SingleChoice",
        mandatory: false,
        options: ["Good", "Poor"],
      },
    ],
    rowVersion,
  };
}

function weighting(id: string, version: number, rowVersion: number, availability = "33.34") {
  return {
    id,
    version,
    publishedAtUtc: "2026-04-06T09:00:00+00:00",
    weights: [
      { pillar: "Availability", percentage: availability },
      { pillar: "ShareOfShelf", percentage: "33.33" },
      { pillar: "PriceCompliance", percentage: "33.33" },
    ],
    rowVersion,
  };
}

function page<T>(upserts: T[], cursor: number) {
  return { upserts, tombstones: [], cursor };
}

afterEach(() => {
  closeDatabase();
});

describe("survey forms on the device", () => {
  it("stores a form whole and finds it by the id an audit names", async () => {
    const db = freshDatabase();

    await applySurveyChanges(db, page([form("f1", "Chiller compliance", 7)], 7));

    const stored = await surveyForm(db, "f1");

    expect(stored?.name).toBe("Chiller compliance");

    // The questions travel inside the form and are read with it — a device holding four of five
    // would ask a rep less than the tenant configured.
    expect(stored?.questions).toHaveLength(2);
    expect(stored?.questions[1].options).toEqual(["Good", "Poor"]);

    expect(await watermark(db, SURVEYS)).toBe(7);
  });

  it("lists forms by name, which is what an administrator picks one by", async () => {
    const db = freshDatabase();

    await applySurveyChanges(
      db,
      page([form("f1", "Zebra crossing", 1), form("f2", "Apple display", 2)], 2),
    );

    expect((await surveyForms(db)).map((each) => each.name)).toEqual([
      "Apple display",
      "Zebra crossing",
    ]);
  });

  it("drops a form the server tombstoned", async () => {
    // An administrator deleted it. The device that then opens an audit naming it is in the same
    // state as one that has never synced — which is why an audit carries each question's text.
    const db = freshDatabase();

    await applySurveyChanges(db, page([form("f1", "Chiller compliance", 1)], 1));

    await applySurveyChanges(db, {
      upserts: [],
      tombstones: [{ id: "f1", rowVersion: 2 }],
      cursor: 2,
    });

    expect(await surveyForm(db, "f1")).toBeUndefined();
  });
});

describe("perfect-store weightings on the device", () => {
  it("keeps every published version, not only the newest", async () => {
    /*
     * The property that separates this store from every other reference table. An audit records the
     * version it was scored against (`BR-AUD-8`), so a device with a queued audit from last week
     * still has to be able to show the rep what it scored.
     */
    const db = freshDatabase();

    await applyScoreWeightChanges(db, page([weighting("w1", 3, 10), weighting("w2", 4, 11)], 11));

    expect(await db.scoreWeights.count()).toBe(2);

    expect((await scoreWeightSet(db, 3))?.id).toBe("w1");
    expect((await scoreWeightSet(db, 4))?.id).toBe("w2");
  });

  it("finds a weighting by version rather than by recency", async () => {
    // The lookup an audit's breakdown uses. Asking for "the newest" would silently restate what the
    // rep saw yesterday the moment a re-weighting synced overnight.
    const db = freshDatabase();

    await applyScoreWeightChanges(
      db,
      page([weighting("w1", 3, 10, "70.00"), weighting("w2", 4, 11, "10.00")], 11),
    );

    const scored = await scoreWeightSet(db, 3);

    expect(scored?.weights[0].percentage).toBe("70.00");
  });

  it("answers with the newest for a *new* audit, and with nothing when none is published", async () => {
    /*
     * The one place "the latest" is the right question, asked at capture time only — the version it
     * returns is written onto the audit, and every later read goes through `scoreWeightSet`.
     *
     * Undefined is a real state: a device can hold a rep's whole round before an administrator has
     * ever opened the weights screen.
     */
    const db = freshDatabase();

    expect(await currentScoreWeightSet(db)).toBeUndefined();

    // Applied out of version order on purpose — `orderBy("version").last()` is what makes this
    // answer 4 rather than "whatever arrived last".
    await applyScoreWeightChanges(db, page([weighting("w2", 4, 11), weighting("w1", 3, 10)], 11));

    expect((await currentScoreWeightSet(db))?.version).toBe(4);
  });

  it("carries percentages as strings a decimal can read exactly", async () => {
    /*
     * `BR-AUD-5` at its narrowest point. The wire sends `"33.34"`; if anything in this path turned
     * it into a `number`, the device's scorer would start from a value that has already been through
     * IEEE-754 and would disagree with the server in the fourth decimal place.
     *
     * Asserted on the *type* as well as the value, because a round-tripped `33.34` would compare
     * equal by `==` and still be wrong.
     */
    const db = freshDatabase();

    await applyScoreWeightChanges(db, page([weighting("w1", 3, 10)], 10));

    const stored = await scoreWeightSet(db, 3);
    const availability = stored!.weights[0].percentage;

    expect(typeof availability).toBe("string");
    expect(new Decimal(availability).equals("33.34")).toBe(true);

    // The sum is exact in decimal and is not in float64: 33.34 + 33.33 + 33.33 is 100 here and
    // 100.00000000000001 with native numbers. This is the assertion a `number` would fail.
    const total = stored!.weights.reduce(
      (sum, weight) => sum.plus(weight.percentage),
      new Decimal(0),
    );

    expect(total.equals("100")).toBe(true);
  });

  it("advances its own watermark independently of the surveys'", async () => {
    // Separate cursors and separate transactions: a device that got one page of a pull keeps it
    // whatever happened to the other.
    const db = freshDatabase();

    await applySurveyChanges(db, page([form("f1", "Chiller compliance", 4)], 4));
    await applyScoreWeightChanges(db, page([weighting("w1", 3, 9)], 9));

    expect(await watermark(db, SURVEYS)).toBe(4);
    expect(await watermark(db, SCORE_WEIGHTS)).toBe(9);
  });
});
