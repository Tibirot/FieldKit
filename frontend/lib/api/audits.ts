import { apiGet } from "@/lib/api/client";
import type { ScorePillar } from "@/lib/api/score-weights";

/** How the rep found one MSL product on the shelf (`AUD-01`, `BR-AUD-1`). */
export type AvailabilityStatus = "Present" | "Absent" | "OutOfStock";

/**
 * One product's availability.
 *
 * `Absent` and `OutOfStock` mean opposite things to the business — a listing gap versus a
 * replenishment gap — and the same thing to the shelf, which is why the score treats both as a miss
 * and this record keeps them apart.
 */
export type AvailabilityLine = { productId: string; status: AvailabilityStatus };

export type FacingsLine = { productId: string; facings: number };

/**
 * One price check, with the delta the server computed.
 *
 * `deltaMinorUnits` is observed minus expected, positive when the shop charges over, and **null when
 * nothing was expected**. It is deliberately not a compliance verdict: `BR-AUD-3`'s tolerance is
 * tenant configuration, and the score is where that judgement is applied.
 */
export type PriceLine = {
  productId: string;
  observedMinorUnits: number;
  expectedMinorUnits: number | null;
  deltaMinorUnits: number | null;
  currency: string;
};

export type AnswerLine = {
  order: number;
  questionKey: string;
  /** The question **as it was asked**, not as it reads now — a re-worded form must not rewrite history. */
  questionText: string;
  value: string;
};

/**
 * One photograph.
 *
 * `objectKey` may point at nothing: the upload is a separate step from the JSON push and usually
 * later, so a client renders a gap and never an error.
 */
export type PhotoLine = { section: string; objectKey: string };

/**
 * One pillar's contribution to the score (`AUD-06`).
 *
 * `percentage` is **null when the pillar was skipped** — not measured, so renormalised out of the
 * score rather than counted as zero (`BR-AUD-2`). A screen shows "not measured", never a 0.
 */
export type ScoredPillarLine = {
  pillar: ScorePillar;
  percentage: number | null;
  weight: number;
};

/**
 * An audit, as it was stored (`AUD-09`).
 *
 * `weightSetVersion` is the whole reason this is safe to display: `BR-AUD-8` records which weighting
 * scored it, because a re-weighting afterwards cannot be undone and the numbers a rep was shown must
 * stay recoverable.
 */
export type Audit = {
  id: string;
  visitId: string;
  outletId: string;
  userId: string;
  capturedAtUtc: string;
  weightSetVersion: number;
  /** The share-of-shelf denominator. Null means the rep could not count the aisle — a real answer. */
  categoryFacings: number | null;
  availability: AvailabilityLine[];
  facings: FacingsLine[];
  prices: PriceLine[];
  surveyFormId: string | null;
  answers: AnswerLine[];
  photos: PhotoLine[];
  /** Null when nothing could be scored. Not zero — a zero is a claim about a shop somebody looked at. */
  score: number | null;
  scoredPillars: ScoredPillarLine[];
};

export const auditKey = (subject: string, visitId: string) => ["audit", subject, visitId] as const;

/**
 * The audit worked during a visit, or **null when there was none**.
 *
 * The endpoint answers 404 for both "no audit" and "no such visit" — Audit cannot tell them apart
 * without reading Visit's schema, and the difference is not one this reader can act on. A visit with
 * no audit is ordinary, so the 404 is translated to null here rather than thrown: the screen renders
 * a sentence, not an error.
 */
export async function fetchAudit(
  accessToken: string,
  visitId: string,
  signal?: AbortSignal,
): Promise<Audit | null> {
  return apiGet<Audit>(`/api/visits/${visitId}/audit`, accessToken, signal).catch((error: unknown) => {
    if (error instanceof Error && "status" in error && error.status === 404) return null;

    throw error;
  });
}
