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

export const visitsKey = (subject: string, outletId?: string, userId?: string) =>
  ["visits", subject, outletId ?? "all", userId ?? "all"] as const;

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
