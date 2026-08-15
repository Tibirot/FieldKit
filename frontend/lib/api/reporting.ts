import { apiGet } from "@/lib/api/client";

/**
 * Coverage: what the round promised, and how much of it was kept (`JRN-04`, `BR-JRN-6`).
 *
 * `percentage` is **null when nothing was planned**, not zero. A territory with no round has no
 * coverage; 0% would say a team failed every call it was never given, and those are different
 * conversations. The same distinction runs through every rate in this file.
 */
export type Coverage = {
  planned: number;
  notVisited: number;
  made: number;
  percentage: number | null;
};

/** Visits by outcome, and the strike rate they produce (`VIS-10`). */
export type VisitFigures = {
  productive: number;
  nonProductive: number;
  /** Checked in and not yet out. Outside the rate, because a rep mid-visit has not failed. */
  open: number;
  strikeRate: number | null;
};

/**
 * One pillar's average across the audits that **measured** it (`AUD-06`).
 *
 * `skipped` travels beside it because `BR-AUD-2` renormalises a skipped pillar away rather than
 * scoring it zero — "96%" from two audits out of forty is a pillar nobody could count.
 */
export type PillarFigures = {
  pillar: string;
  average: number | null;
  measured: number;
  skipped: number;
};

/**
 * Perfect store over the window (`AUD-09`).
 *
 * `comparable` is false when the window mixes weight-set versions: `BR-AUD-8` records the weighting
 * each audit was scored against, and an average across two of them is an average of two rulers. The
 * screen says so rather than hiding it, because a five-point movement across that boundary is not a
 * change in anybody's shops.
 */
export type PerfectStore = {
  audits: number;
  scored: number;
  averageScore: number | null;
  comparable: boolean;
  weightSetVersions: number[];
  pillars: PillarFigures[];
};

/**
 * Order value in one currency.
 *
 * A list, not a total: adding RON to EUR is not arithmetic, so the server splits and the screen
 * shows each. A tenant with one currency gets a one-element list.
 */
export type OrderValue = {
  currencyCode: string;
  net: number;
  tax: number;
  gross: number;
  orders: number;
};

/** Order capture over the window (`ORD-09`). */
export type OrderFigures = {
  orders: number;
  lines: number;
  linesPerOrder: number | null;
  rejected: number;
  cancelled: number;
  /** Orders the server re-priced and disagreed with — a pricing-data problem, not a sales one. */
  priceDisagreements: number;
  value: OrderValue[];
};

/**
 * The dashboard's four KPIs over one scope and one period.
 *
 * `outlets` is here because every figure below is unreadable without it: "0% coverage" over four
 * hundred shops and over none are different emergencies, and only this number tells them apart.
 */
export type ReportingSummary = {
  from: string;
  to: string;
  territoryId: string | null;
  outlets: number;
  coverage: Coverage;
  visits: VisitFigures;
  perfectStore: PerfectStore;
  orders: OrderFigures;
};

/** What the screen asks about. Both dates are inclusive, in UTC, and both are optional. */
export type ReportingWindow = {
  from?: string;
  to?: string;
  territoryId?: string;
};

export const summaryKey = (subject: string, window: ReportingWindow) =>
  ["reporting-summary", subject, window.from ?? "", window.to ?? "", window.territoryId ?? "all"] as const;

export function fetchSummary(
  accessToken: string,
  window: ReportingWindow,
  signal?: AbortSignal,
): Promise<ReportingSummary> {
  const query = new URLSearchParams();

  if (window.from) query.set("from", window.from);
  if (window.to) query.set("to", window.to);
  if (window.territoryId) query.set("territoryId", window.territoryId);

  const suffix = query.size > 0 ? `?${query}` : "";

  return apiGet<ReportingSummary>(`/api/reporting/summary${suffix}`, accessToken, signal);
}

/*
 * **There is deliberately no `monthOf(today)` helper here, and the first draft had one.**
 *
 * Omitting `from` and `to` makes the server answer for the month containing *its* today, in UTC —
 * which is the same clock every aggregate dates by. A browser computing the window instead has to
 * decide what "this month" means in a timezone the data is not stored in: in Bucharest at 02:00 on
 * the 1st, a window built through `toISOString()` starts on the previous month's last day. Wrong for
 * two hours a night, and invisible.
 *
 * The response echoes `from` and `to`, so the screen can say which period it is showing without ever
 * having decided one. A period picker is W12's explicit non-goal — "expensive to design before
 * anyone has read the numbers once" — and when one arrives it will send dates a person chose rather
 * than dates a clock guessed.
 */
