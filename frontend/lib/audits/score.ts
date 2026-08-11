import { Decimal, type DecimalValue } from "@/lib/pricing/money";

/**
 * A pillar of the perfect-store score (`AUD-06`, `BR-AUD-4`).
 *
 * The same closed set `Configuration.Contracts.ScorePillar` carries, as **names** rather than
 * ordinals — a vector file that outlived a member being inserted should still mean what it said.
 */
export type ScorePillar = "Availability" | "ShareOfShelf" | "PriceCompliance";

/**
 * The order pillars are always reported in.
 *
 * It mirrors the C# enum's declaration order, which `PerfectStoreScore` reads with
 * `Enum.GetValues`. Written out here because TypeScript has no enum to read — and asserted against
 * the vectors, which carry the order C# produced.
 */
export const SCORE_PILLARS: readonly ScorePillar[] = [
  "Availability",
  "ShareOfShelf",
  "PriceCompliance",
];

export type AvailabilityStatus = "Present" | "Absent" | "OutOfStock";

export type AvailabilityLine = { status: AvailabilityStatus };

export type FacingsLine = { facings: number };

/**
 * One price check.
 *
 * `observedMinorUnits` and `expectedMinorUnits` are **integers**, so they are JSON numbers rather
 * than the strings `vectors/README.md` requires of decimals. Minor units are counts of the smallest
 * indivisible unit — there is no fraction of a ban to lose to a float, and `Number` holds them
 * exactly well past any shelf price.
 */
export type PriceLine = {
  observedMinorUnits: number;
  expectedMinorUnits: number | null;
};

/** What one pillar is worth. A decimal, so it arrives as a string. */
export type PillarWeight = { pillar: ScorePillar; percentage: string };

export type ScoreInputs = {
  availability: readonly AvailabilityLine[];
  facings: readonly FacingsLine[];
  categoryFacings: number | null;
  prices: readonly PriceLine[];
  weights: readonly PillarWeight[];
  priceToleranceMinorUnits?: number;
};

/** One pillar's contribution. `percentage` is null when the pillar was **skipped**. */
export type PillarScore = {
  pillar: ScorePillar;
  percentage: DecimalValue | null;
  weight: DecimalValue;
};

export type PerfectStoreResult = {
  score: DecimalValue | null;
  pillars: PillarScore[];
};

/** Where percentages are rounded. Two places, half-up — the policy `Decimal` is configured with. */
const DECIMALS = 2;

const HUNDRED = new Decimal(100);

/**
 * The perfect-store score — the device mirror of `PerfectStoreScore.cs` (`AUD-06`, `BR-AUD-5`).
 *
 * **Written to be compared with its C# counterpart by eye.** Same order of operations, same names,
 * same comments where the reasoning is the same — because the thing being maintained is the
 * *agreement*, and a mirror that is cleverer than its original is a mirror nobody can check. The
 * shared vectors are the machine-checked half of that; readability is the half a person does.
 *
 * **`decimal.js`, never a native `number`.** A share-of-shelf ratio is a division: `7 / 30` in
 * IEEE-754 is `0.23333333333333334`, and that fourth-decimal residue survives a weighted sum and
 * arrives at a supervisor as two systems disagreeing about one shelf. `BR-AUD-5` requires them to
 * agree exactly, and the `Decimal` imported here is the same configured clone money uses —
 * `precision: 34`, `ROUND_HALF_UP` — so there is one rounding policy on this device rather than two.
 *
 * **Skipped pillars are renormalised away, not scored zero** (W10 slice 0). Scoring the gap zero
 * treats "unknown" as "bad", which is the faking `BR-AUD-2` refuses.
 */
export function computeScore(inputs: ScoreInputs): PerfectStoreResult {
  const measured: Record<ScorePillar, DecimalValue | null> = {
    Availability: availabilityPercentage(inputs),
    ShareOfShelf: shareOfShelfPercentage(inputs),
    PriceCompliance: priceCompliancePercentage(inputs, inputs.priceToleranceMinorUnits ?? 0),
  };

  const pillars: PillarScore[] = SCORE_PILLARS.map((pillar) => ({
    pillar,
    percentage: measured[pillar],
    weight: weightOf(inputs, pillar),
  }));

  /*
   * The weighted mean, computed from the *rounded* pillar percentages.
   *
   * Rounding the parts before combining them is deliberate on both sides: `AUD-09` shows a
   * supervisor the breakdown beside the total, and a breakdown whose parts do not reconcile with the
   * whole is a support conversation every time. It also means the vectors can compare the
   * intermediate pillar values, so a mirror that rounded in a different place is caught rather than
   * absorbed.
   */
  const scored = pillars.filter((pillar) => pillar.percentage !== null);

  const weight = scored.reduce((sum, pillar) => sum.plus(pillar.weight), new Decimal(0));

  if (scored.length === 0 || weight.isZero()) return { score: null, pillars };

  const total = scored.reduce(
    (sum, pillar) => sum.plus(pillar.percentage!.times(pillar.weight)),
    new Decimal(0),
  );

  return { score: round(total.dividedBy(weight)), pillars };
}

/**
 * How much of the outlet's MSL was on the shelf (`AUD-01`).
 *
 * **Only `Present` counts.** Absent and out-of-stock mean opposite things to the business — a
 * listing gap versus a replenishment gap — which is why they are stored separately; from the shelf's
 * point of view the product was not there to sell.
 */
function availabilityPercentage(inputs: ScoreInputs): DecimalValue | null {
  // No availability checks is a skipped pillar, not a score of zero: the rep did not fail to find
  // the products, they were not asked to look.
  if (inputs.availability.length === 0) return null;

  const present = inputs.availability.filter((line) => line.status === "Present").length;

  return round(HUNDRED.times(present).dividedBy(inputs.availability.length));
}

/**
 * Own facings over the total category facings (`AUD-02`, `BR-AUD-2`).
 *
 * The denominator is the **captured** category total, never the sum of own facings — that would
 * always be ~100%, which is why `BR-AUD-2` makes the rep count it separately.
 *
 * Skipped in three ways, all of them "the rep could not measure it": no total, no facings, or a
 * total of zero — a category with nothing on the shelf, whose ratio is undefined rather than nought.
 */
function shareOfShelfPercentage(inputs: ScoreInputs): DecimalValue | null {
  const category = inputs.categoryFacings;

  if (category === null || category <= 0) return null;

  if (inputs.facings.length === 0) return null;

  const own = inputs.facings.reduce((sum, line) => sum.plus(line.facings), new Decimal(0));

  // Capped at 100: own facings above the category total is a miscount — usually the rep counted the
  // competitor shelf and forgot their own products in the total — and uncapped it drags the whole
  // score past 100, which means nothing to any consumer. The raw counts stay in the audit.
  return Decimal.min(HUNDRED, round(HUNDRED.times(own).dividedBy(category)));
}

/**
 * How many shelf prices matched the expected one (`AUD-03`, `BR-AUD-3`).
 *
 * **Only prices with an expectation count.** A product the device could resolve no price for is a
 * gap in somebody's price list, not a rep's failure — so it leaves the denominator as well as the
 * numerator, and an audit where nothing had an expected price skips the pillar entirely.
 */
function priceCompliancePercentage(
  inputs: ScoreInputs,
  toleranceMinorUnits: number,
): DecimalValue | null {
  const comparable = inputs.prices.filter((line) => line.expectedMinorUnits !== null);

  if (comparable.length === 0) return null;

  // Absolute, so charging under the expected price is as non-compliant as charging over — an
  // under-price is a margin leak and often an unauthorised promotion. Integers throughout, so plain
  // arithmetic is exact here; the decimals start at the division below.
  const compliant = comparable.filter(
    (line) => Math.abs(line.observedMinorUnits - line.expectedMinorUnits!) <= toleranceMinorUnits,
  ).length;

  return round(HUNDRED.times(compliant).dividedBy(comparable.length));
}

/**
 * What the weight set said a pillar is worth; zero when it named none.
 *
 * Zero rather than skipping it: a pillar the tenant weighted at nothing was *measured and
 * disregarded*, which is a tenant switching share-of-shelf off. A pillar nobody measured is skipped.
 * Both contribute nothing to the total, and only the second leaves the denominator.
 */
function weightOf(inputs: ScoreInputs, pillar: ScorePillar): DecimalValue {
  const found = inputs.weights.find((weight) => weight.pillar === pillar);

  return found ? new Decimal(found.percentage) : new Decimal(0);
}

/**
 * Half-up at two places.
 *
 * `toDecimalPlaces` with no rounding argument uses the constructor's configured mode, which is
 * `ROUND_HALF_UP` on this clone — the same policy `Money.Round` applies with
 * `MidpointRounding.AwayFromZero`. Passing the mode explicitly would be a second place for the two
 * to disagree.
 */
function round(value: DecimalValue): DecimalValue {
  return value.toDecimalPlaces(DECIMALS);
}
