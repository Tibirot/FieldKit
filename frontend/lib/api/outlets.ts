import { apiGet } from "@/lib/api/client";

/** The lifecycle an outlet moves through (`OUT-04`). Sent and received by name, never by ordinal. */
export type OutletStatus = "Active" | "Inactive" | "Closed";

/** A territory, as the outlet list needs it: enough to label and link, nothing more. */
export type TerritorySummary = {
  id: string;
  name: string;
};

/**
 * An outlet as the back office sees it.
 *
 * A partial view of what `/api/outlets` returns on purpose — address, coordinates, contacts and
 * custom fields all come down too, and the list screen shows none of them. Typing only what is read
 * keeps the compiler honest about which columns exist rather than which happen to be in the payload.
 */
export type Outlet = {
  id: string;
  code: string;
  name: string;
  channelId: string;
  channelName: string;
  segment: string | null;
  banner: string | null;
  status: OutletStatus;
  /** Null until someone decides who covers this shop — an ordinary state, not a failure (`BR-OUT-1`). */
  territory: TerritorySummary | null;
};

export function fetchOutlets(accessToken: string, signal?: AbortSignal): Promise<Outlet[]> {
  return apiGet<Outlet[]>("/api/outlets", accessToken, signal);
}

/**
 * The cache key for the outlet base.
 *
 * Keyed by the signed-in subject, which is the part that matters: a bare `["outlets"]` would serve
 * one tenant's rows to the next person to sign in on the same browser, because the cache outlives a
 * sign-out. This makes that structurally impossible rather than something a sign-out handler has to
 * remember to clear — and the subject comes off the session already, so it costs no round trip.
 */
export const outletsKey = (subject: string) => ["outlets", subject] as const;
