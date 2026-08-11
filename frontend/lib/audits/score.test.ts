import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";

import { describe, expect, it } from "vitest";

import {
  computeScore,
  SCORE_PILLARS,
  type AvailabilityLine,
  type FacingsLine,
  type PillarWeight,
  type PriceLine,
  type ScorePillar,
} from "@/lib/audits/score";

/**
 * The shared perfect-store vectors, run against the device mirror (`AUD-06`, `BR-AUD-5`,
 * `BR-AUD-12`) — W10 slice 5.
 *
 * The same files `PerfectStoreScoreVectorTests.cs` reads, from the same path. The hand-written file
 * says what the rules should be; the generated one is an oracle produced by the C# engine over a far
 * wider sweep than anyone would author — 400 cases against the hand-written 16, which is the whole
 * point of generating it.
 *
 * **This file is where `BR-AUD-5` stops being a claim.** Until now "the score computes identically
 * on device and server" was a sentence in a spec; from here it is a check that fails.
 */
type PillarExpectation = {
  pillar: ScorePillar;
  percentage: string | null;
  weight: string;
};

type ScoreVector = {
  name?: string;
  availability: AvailabilityLine[];
  facings: FacingsLine[];
  categoryFacings: number | null;
  prices: PriceLine[];
  weights: PillarWeight[];
  priceToleranceMinorUnits: number;
  expected: { score: string | null; pillars: PillarExpectation[] };
};

type VectorFile = { version: number; cases: ScoreVector[] };

function load(file: string): VectorFile {
  const path = fileURLToPath(new URL(`../../../vectors/audits/${file}`, import.meta.url));

  return JSON.parse(readFileSync(path, "utf8")) as VectorFile;
}

const handWritten = load("perfect-store.v1.json");
const generated = load("perfect-store.generated.v1.json");

/** Everything the format says is a decimal, so it must have arrived as a string. */
function decimalsOf(vector: ScoreVector, index: number): { path: string; value: unknown }[] {
  const label = vector.name ?? `case ${index}`;

  return [
    ...vector.weights.map((weight, at) => ({
      path: `${label}.weights[${at}].percentage`,
      value: weight.percentage as unknown,
    })),
    { path: `${label}.expected.score`, value: vector.expected.score as unknown },
    ...vector.expected.pillars.flatMap((pillar, at) => [
      { path: `${label}.expected.pillars[${at}].percentage`, value: pillar.percentage as unknown },
      { path: `${label}.expected.pillars[${at}].weight`, value: pillar.weight as unknown },
    ]),
  ];
}

/**
 * Every decimal is a JSON string, on both files.
 *
 * The rule `vectors/README.md` exists for, and it matters more here than anywhere: `JSON.parse`
 * turns a bare `82.86` into a float **before `computeScore` ever sees it**, so a file that dropped
 * the quotes would leave this suite comparing two rounding errors and passing. C# refuses the number
 * token in a converter; this is the same rule enforced from the other side.
 *
 * `null` is allowed — a skipped pillar and an unscoreable audit are answers, not gaps.
 */
describe.each([
  ["hand-written", handWritten],
  ["generated", generated],
])("perfect-store vectors (%s): format", (_label, file) => {
  it("carries every decimal as a string", () => {
    const offenders = file.cases
      .flatMap((vector, index) => decimalsOf(vector, index))
      .filter(({ value }) => value !== null && typeof value !== "string");

    expect(offenders).toEqual([]);
  });

  it("has cases to run", () => {
    // A file that failed to load, or a rename that left the glob matching nothing, would otherwise
    // be a green suite of zero assertions — which is the quiet failure the CI floor also guards.
    expect(file.cases.length).toBeGreaterThan(0);
  });
});

function run(vector: ScoreVector) {
  return computeScore({
    availability: vector.availability,
    facings: vector.facings,
    categoryFacings: vector.categoryFacings,
    prices: vector.prices,
    weights: vector.weights,
    priceToleranceMinorUnits: vector.priceToleranceMinorUnits,
  });
}

/**
 * Compares a result with its expectation.
 *
 * **By decimal value, not by string.** The generator writes `"70.00"` where a hand-written file says
 * `"70"`, and both mean seventy — comparing text would fail on a difference in spelling. `toFixed`
 * on both sides would be the same mistake one step later, since it fixes a scale the two files do
 * not share.
 *
 * The pillar breakdown is checked as well as the total: two engines can agree on a score while
 * disagreeing about how they reached it, and the breakdown is what `AUD-09` renders.
 */
function expectAgreement(vector: ScoreVector) {
  const result = run(vector);

  if (vector.expected.score === null) {
    expect(result.score).toBeNull();
  } else {
    expect(result.score).not.toBeNull();
    expect(result.score!.equals(vector.expected.score)).toBe(true);
  }

  expect(result.pillars.map((pillar) => pillar.pillar)).toEqual(
    vector.expected.pillars.map((pillar) => pillar.pillar),
  );

  vector.expected.pillars.forEach((expected, index) => {
    const actual = result.pillars[index];

    if (expected.percentage === null) {
      expect(actual.percentage).toBeNull();
    } else {
      expect(actual.percentage).not.toBeNull();
      expect(actual.percentage!.equals(expected.percentage)).toBe(true);
    }

    expect(actual.weight.equals(expected.weight)).toBe(true);
  });
}

describe("perfect-store vectors (hand-written)", () => {
  it.each(handWritten.cases.map((vector, index) => [vector.name ?? `case ${index}`, vector] as const))(
    "%s",
    (_name, vector) => expectAgreement(vector),
  );
});

describe("perfect-store vectors (generated)", () => {
  it.each(generated.cases.map((vector, index) => [index, vector] as const))(
    "case %i",
    (_index, vector) => expectAgreement(vector),
  );
});

describe("the mirror's own shape", () => {
  it("reports pillars in the order C# declares them", () => {
    // The C# side reads `Enum.GetValues<ScorePillar>()`; TypeScript has no enum to read, so the
    // order is written out in `SCORE_PILLARS` and could drift silently. Every vector carries the
    // order C# produced, which is what makes this checkable — asserted directly as well, so the
    // failure names the cause rather than arriving as 416 mismatched breakdowns.
    expect(SCORE_PILLARS).toEqual(["Availability", "ShareOfShelf", "PriceCompliance"]);

    expect(handWritten.cases[0].expected.pillars.map((pillar) => pillar.pillar)).toEqual([
      ...SCORE_PILLARS,
    ]);
  });

  it("defaults the price tolerance to zero when the caller omits it", () => {
    // The spec's own assumption — tenant-configurable, defaulting to 0 — and nothing configures it
    // yet. The vectors always send it explicitly, so the default is not otherwise exercised.
    const result = computeScore({
      availability: [],
      facings: [],
      categoryFacings: null,
      prices: [{ observedMinorUnits: 101, expectedMinorUnits: 100 }],
      weights: [{ pillar: "PriceCompliance", percentage: "100" }],
    });

    expect(result.score!.equals("0")).toBe(true);
  });
});
