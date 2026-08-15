import { apiGet } from "@/lib/api/client";

/**
 * How a visit came out (`VIS-05`).
 *
 * Null while the rep is still in the shop — a visit has no outcome until check-out, and "open" is a
 * state rather than a third outcome. The dashboard makes the same distinction with its counts.
 */
export type VisitOutcome = "Productive" | "NonProductive";

/** `CheckedIn` while the rep is there, `CheckedOut` once it is sealed (`BR-VIS-4`). */
export type VisitStatus = "CheckedIn" | "CheckedOut";

/**
 * How the visit reached this server.
 *
 * `Live` is a rep working online; `Device` arrived through `/sync/push` after being captured
 * offline. Null for a visit stored before W9 recorded it. Worth showing, because it is half of
 * "captured on Tuesday, drained on Friday" — the other half being `recordedAtUtc`.
 */
export type VisitSource = "Live" | "Device";

/** A visit as the back office reads it (`VIS-10`). */
export type Visit = {
  id: string;
  outletId: string;
  userId: string;
  plannedVisitId: string | null;
  status: VisitStatus;
  checkedInAtUtc: string;
  checkInLatitude: number | null;
  checkInLongitude: number | null;
  checkInDistanceMetres: number | null;
  /** False when the rep was elsewhere — never a refusal, always a record (`BR-VIS-2`). */
  wasInsideGeofence: boolean;
  /** What the rep typed to explain being elsewhere. Present exactly when the geofence failed. */
  geofenceOverrideReason: string | null;
  checkedOutAtUtc: string | null;
  checkOutLatitude: number | null;
  checkOutLongitude: number | null;
  outcome: VisitOutcome | null;
  outcomeReason: string | null;
  timeOnSiteSeconds: number | null;
  source: VisitSource | null;
  /** When this server first stored it — days after `checkedInAtUtc` for an offline visit. */
  recordedAtUtc: string | null;
};

/** What the rep was asked to do at one step (`VIS-03`). */
export type VisitStepType =
  | "Audit"
  | "Order"
  | "Survey"
  | "Task"
  | "Photo"
  | "Note"
  | "Signature";

/** `Pending` until the rep marks it done. An optional step may stay pending forever (`BR-VIS-3`). */
export type VisitStepStatus = "Pending" | "Completed";

/**
 * One step, **as the rep worked it** rather than as the workflow defines it now.
 *
 * The label and the type are copies taken at check-in: re-reading the current workflow would
 * re-describe a sealed visit under rules that may have been republished since, which is the same
 * bargain `SurveyAnswerEntry` makes with its question text.
 */
export type VisitStep = {
  id: string;
  order: number;
  type: VisitStepType;
  mandatory: boolean;
  label: string;
  status: VisitStepStatus;
  completedAtUtc: string | null;
  notes: string | null;
};

/**
 * A visit with its steps.
 *
 * `openMandatorySteps` is what stood between the rep and the door (`BR-VIS-3`). On a checked-out
 * visit it is always empty — check-out refuses otherwise — so on this screen it is a fact about a
 * visit still in progress.
 */
export type VisitDetail = {
  visit: Visit;
  steps: VisitStep[];
  openMandatorySteps: VisitStep[];
};

export const visitsKey = (subject: string, outletId?: string, userId?: string) =>
  ["visits", subject, outletId ?? "all", userId ?? "all"] as const;

export const visitKey = (subject: string, id: string) => ["visit", subject, id] as const;

export function fetchVisit(
  accessToken: string,
  id: string,
  signal?: AbortSignal,
): Promise<VisitDetail> {
  return apiGet<VisitDetail>(`/api/visits/${id}`, accessToken, signal);
}

/**
 * Recent visits, newest first.
 *
 * **Bounded by the server at 200**, whatever the filter — a ceiling rather than a page size (W12
 * slice 5a). There is no cursor and no date window yet: the screen shows what was recorded lately,
 * and a window lands when a reader asks for one rather than being guessed at now.
 */
export function fetchVisits(
  accessToken: string,
  filter: { outletId?: string; userId?: string } = {},
  signal?: AbortSignal,
): Promise<Visit[]> {
  const query = new URLSearchParams();

  if (filter.outletId) query.set("outletId", filter.outletId);
  if (filter.userId) query.set("userId", filter.userId);

  const suffix = query.size > 0 ? `?${query}` : "";

  return apiGet<Visit[]>(`/api/visits${suffix}`, accessToken, signal);
}
