import { apiGet, apiSend } from "@/lib/api/client";

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

/**
 * One page of a list, and enough to draw a pager around it.
 *
 * Mirrors the API's envelope exactly, down to `page` and `pageSize` carrying the same names the
 * query string uses — so a request and its response never describe the same thing two ways.
 */
export type PagedList<T> = {
  items: T[];
  total: number;
  page: number;
  pageSize: number;
};

/**
 * What a caller asks a list for.
 *
 * Every field optional, because the screen builds this from URL search params and an absent one
 * means "the server's default" rather than a value this app has to invent. Territory is absent on
 * purpose: it is resolved after the page is fetched (`ORG-05`), so the database cannot filter or
 * sort by it.
 */
export type OutletQuery = {
  search?: string;
  channelId?: string;
  status?: OutletStatus;
  sort?: OutletSort;
  descending?: boolean;
  page?: number;
  pageSize?: number;
};

/** What an outlet list may be ordered by — a closed set, matching the API's enum by name. */
export type OutletSort = "Code" | "Name" | "Channel" | "Status";

export function fetchOutlets(
  accessToken: string,
  query: OutletQuery,
  signal?: AbortSignal,
): Promise<PagedList<Outlet>> {
  const params = new URLSearchParams();

  // Only what was asked for. Sending `search=` for an empty box would make the server escape and
  // match an empty pattern rather than skip the filter — the same query, needlessly narrower to plan.
  for (const [key, value] of Object.entries(query)) {
    if (value !== undefined && value !== "") params.set(key, String(value));
  }

  const suffix = params.size > 0 ? `?${params}` : "";

  return apiGet<PagedList<Outlet>>(`/api/outlets${suffix}`, accessToken, signal);
}

/**
 * The cache key for the outlet base.
 *
 * Keyed by the signed-in subject, which is the part that matters: a bare `["outlets"]` would serve
 * one tenant's rows to the next person to sign in on the same browser, because the cache outlives a
 * sign-out. This makes that structurally impossible rather than something a sign-out handler has to
 * remember to clear — and the subject comes off the session already, so it costs no round trip.
 *
 * The query is part of the key too, so page 2 and a search for "cluj" are separate cache entries
 * rather than one entry the next request overwrites. That is also what makes going back to page 1
 * instant instead of a refetch.
 */
export const outletsKey = (subject: string, query: OutletQuery = {}) =>
  ["outlets", subject, query] as const;

/** A structured address. Postal code and city are what territory rules key off (`ORG-07`). */
export type Address = {
  street: string | null;
  city: string | null;
  postalCode: string | null;
  countryCode: string | null;
};

export type Coordinates = {
  latitude: number;
  longitude: number;
};

/**
 * What the form sends.
 *
 * `code` is absent from the update shape because an outlet's code is not editable — it is the
 * tenant's own identifier, already written into every territory membership and every import file
 * that references the shop.
 */
export type OutletWrite = {
  name: string;
  channelId: string;
  segment: string | null;
  banner: string | null;
  timeZoneId: string;
  address: Address | null;
  location: Coordinates | null;
  customFields: Record<string, unknown>;
};

export type CreateOutlet = OutletWrite & { code: string };

export function fetchOutlet(
  accessToken: string,
  id: string,
  signal?: AbortSignal,
): Promise<OutletDetail> {
  return apiGet<OutletDetail>(`/api/outlets/${id}`, accessToken, signal);
}

/** The whole outlet, as the edit form needs it — the list only types the columns it renders. */
export type OutletDetail = Outlet & {
  timeZoneId: string;
  address: Address | null;
  location: Coordinates | null;
  customFields: Record<string, unknown>;
};

export const outletKey = (subject: string, id: string) => ["outlet", subject, id] as const;

export function createOutlet(accessToken: string, outlet: CreateOutlet): Promise<OutletDetail> {
  return apiSend<OutletDetail>("POST", "/api/outlets", accessToken, outlet);
}

export function updateOutlet(
  accessToken: string,
  id: string,
  outlet: OutletWrite,
): Promise<OutletDetail> {
  return apiSend<OutletDetail>("PUT", `/api/outlets/${id}`, accessToken, outlet);
}
