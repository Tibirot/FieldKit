import { apiGet, apiSend } from "@/lib/api/client";

/**
 * A pillar of the perfect-store score (`AUD-06`, `BR-AUD-4`).
 *
 * The names Configuration's own `ScorePillar` carries, as a union rather than a `string`: a screen
 * renders one control per pillar, and the compiler is what makes "you forgot share of shelf" a
 * build failure rather than a missing row nobody notices.
 */
export type ScorePillar = "Availability" | "ShareOfShelf" | "PriceCompliance";

/** The order the screen shows them in — the server's own, so a breakdown reads the same everywhere. */
export const SCORE_PILLARS: readonly ScorePillar[] = [
  "Availability",
  "ShareOfShelf",
  "PriceCompliance",
];

/**
 * What one pillar is worth.
 *
 * `percentage` is a **number** here and not the string the device stores. This is the authoring
 * surface: an administrator types into a control, the value round-trips as JSON, and nothing on this
 * screen does arithmetic that a float could spoil — the sum is checked by the *server*, exactly, in
 * `decimal`. The device is where the string matters, because that is where the score is computed.
 */
export type ScoreWeight = { pillar: ScorePillar; percentage: number };

/** One version of the tenant's weighting, as stored. */
export type ScoreWeightSet = {
  id: string;
  version: number;
  isPublished: boolean;
  publishedAtUtc: string | null;
  weights: ScoreWeight[];
};

const WEIGHTS = "/api/config/score-weights";

export function scoreWeightsKey(subject: string): readonly unknown[] {
  return ["score-weights", subject];
}

export function fetchScoreWeights(
  accessToken: string,
  signal?: AbortSignal,
): Promise<ScoreWeightSet[]> {
  return apiGet<ScoreWeightSet[]>(WEIGHTS, accessToken, signal);
}

/**
 * Drafts a new version.
 *
 * No version number is sent: the server assigns `Max + 1`. A client that could name its own could
 * name one a sealed audit already points at (`BR-AUD-8`).
 */
export function draftScoreWeights(
  accessToken: string,
  weights: ScoreWeight[],
): Promise<ScoreWeightSet> {
  return apiSend<ScoreWeightSet>("POST", WEIGHTS, accessToken, { weights });
}

/** Replaces a **draft's** weights. A published version answers 409 `config.weights.alreadyPublished`. */
export function setScoreWeights(
  accessToken: string,
  version: number,
  weights: ScoreWeight[],
): Promise<ScoreWeightSet> {
  return apiSend<ScoreWeightSet>("PUT", `${WEIGHTS}/${version}`, accessToken, { weights });
}

/** Freezes a version. One-way — this is the request `BR-AUD-8` rests on. */
export function publishScoreWeights(
  accessToken: string,
  version: number,
): Promise<ScoreWeightSet> {
  return apiSend<ScoreWeightSet>("POST", `${WEIGHTS}/${version}/publish`, accessToken, {});
}

/**
 * What the weights add up to, as typed.
 *
 * <b>In hundredths, as integers.</b> The server checks the sum in `decimal` and refuses anything but
 * exactly 100 — no tolerance, because `33.33 × 3` is exactly `99.99` there. A screen comparing
 * `33.34 + 33.33 + 33.33` to `100` in float64 compares `100.00000000000001`, and would refuse a set
 * the server accepts: a rule invented by the client, on the one input a tenant expressing thirds
 * actually types.
 *
 * <b>Each value is rounded before it is added, not only the total</b>, and that is the half worth
 * explaining. The column is `numeric(5,2)`, so `33.335` becomes `33.34` on the way in — the screen
 * has to sum what the server will *store*, not what was typed. Rounding only at the end would agree
 * with the typed numbers and disagree with the row.
 */
export function sumInHundredths(weights: readonly { percentage: number }[]): number {
  return weights.reduce((total, weight) => total + Math.round(weight.percentage * 100), 0);
}

/** The whole point of the sum: exactly 100, and this is the one number the screen gates on. */
export const REQUIRED_HUNDREDTHS = 10_000;
