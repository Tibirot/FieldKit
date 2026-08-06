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
 * A person at the outlet — the store manager, the buyer.
 *
 * **Personal data** under [B8](../../../docs/product/decisions-and-assumptions.md). Only the name is
 * required; the rest is how to reach them and any of it may simply not be known yet. Sent and
 * returned as a whole list, never patched — an empty list is how every contact is erased.
 */
export type OutletContact = {
  name: string;
  role: string | null;
  phone: string | null;
  email: string | null;
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

  /**
   * Always sent, even unchanged.
   *
   * The API replaces the list wholesale, so an omitted `contacts` is an emptied one — this is a
   * PUT, and leaving a field out of a PUT means clearing it. A form that renders the outlet but
   * forgets to send this part back deletes every contact on it, silently, on any save.
   */
  contacts: OutletContact[];
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
  contacts: OutletContact[];
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

/**
 * Moves an outlet through its lifecycle (`OUT-04`).
 *
 * **Its own call, not a field on `updateOutlet`** — and that is the API's design rather than a
 * convenience here. "This store is shut" is a different decision from "the name was spelled wrong",
 * and a status that rides along on the edit form is one a careless typo fix can change as a side
 * effect ([spec §F4](../../../docs/product/12-outlets-master-data.md)).
 *
 * A reason is required to close and optional otherwise; the server decides that, and refuses with
 * the problem keyed to `reason`.
 */
export function changeOutletStatus(
  accessToken: string,
  id: string,
  change: { status: OutletStatus; reason: string | null },
): Promise<OutletDetail> {
  return apiSend<OutletDetail>("POST", `/api/outlets/${id}/status`, accessToken, change);
}

/**
 * One transition in an outlet's life, as recorded (`OUT-04`).
 *
 * `from` is null on the first entry — the outlet's creation. The trail always has that entry, so
 * "nothing here" can never be mistaken for "the history was lost".
 */
export type OutletStatusChange = {
  from: OutletStatus | null;
  to: OutletStatus;
  reason: string | null;
  changedAtUtc: string;
  changedBy: string | null;
};

/**
 * The append-only trail.
 *
 * Read-only by design: the API has no way to write, edit or delete an entry, because an audit log
 * with a write path is one that can be arranged after the fact. Worth reading in full rather than
 * paged — an outlet has a handful of transitions in its life, and the first one is the one somebody
 * scrolls to.
 */
export function fetchOutletStatusHistory(
  accessToken: string,
  id: string,
  signal?: AbortSignal,
): Promise<OutletStatusChange[]> {
  return apiGet<OutletStatusChange[]>(`/api/outlets/${id}/status-history`, accessToken, signal);
}

export const outletStatusHistoryKey = (subject: string, id: string) =>
  ["outlet-status-history", subject, id] as const;

