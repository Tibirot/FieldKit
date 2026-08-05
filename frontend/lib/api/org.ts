import { apiDelete, apiGet, apiSend } from "@/lib/api/client";

/**
 * A node in the sales hierarchy (`ORG-01`).
 *
 * Depth is the tenant's choice and the labels are theirs too, so this is a tree of names and nothing
 * more — no "region" or "area" is baked in anywhere.
 */
export type OrgUnit = {
  id: string;
  name: string;
  parentId: string | null;
};

/**
 * A territory, and how many outlets are in it (`ORG-03`).
 *
 * The count comes from the server rather than the length of a list this screen fetched: the list
 * screen shows counts for territories whose outlets it has never asked for, and counting client-side
 * would mean fetching every membership to render a number.
 */
export type Territory = {
  id: string;
  name: string;
  orgUnitId: string;
  outletCount: number;
};

export type TerritoryWrite = {
  name: string;
  orgUnitId: string;
};

export const orgUnitsKey = (subject: string) => ["org-units", subject] as const;

export const territoriesKey = (subject: string, orgUnitId?: string) =>
  ["territories", subject, orgUnitId ?? "all"] as const;

export function fetchOrgUnits(accessToken: string, signal?: AbortSignal): Promise<OrgUnit[]> {
  return apiGet<OrgUnit[]>("/api/org/units", accessToken, signal);
}

export function fetchTerritories(
  accessToken: string,
  orgUnitId?: string,
  signal?: AbortSignal,
): Promise<Territory[]> {
  const query = orgUnitId ? `?orgUnitId=${encodeURIComponent(orgUnitId)}` : "";

  return apiGet<Territory[]>(`/api/org/territories${query}`, accessToken, signal);
}

export function createTerritory(accessToken: string, territory: TerritoryWrite): Promise<Territory> {
  return apiSend<Territory>("POST", "/api/org/territories", accessToken, territory);
}

export function updateTerritory(
  accessToken: string,
  id: string,
  territory: TerritoryWrite,
): Promise<Territory> {
  return apiSend<Territory>("PUT", `/api/org/territories/${id}`, accessToken, territory);
}

/**
 * Removes a territory.
 *
 * Refused with a `409` while it still holds outlets, rather than cascading. Every outlet in it would
 * otherwise silently become unassigned — and a territory's membership is a rep's offline scope
 * (`BR-ORG-3`), so that is a set of shops vanishing from somebody's device tomorrow morning.
 */
export function deleteTerritory(accessToken: string, id: string): Promise<void> {
  return apiDelete(`/api/org/territories/${id}`, accessToken);
}

/**
 * The org units by id, for naming a territory's parent without a second request per row.
 *
 * A map rather than a repeated `find`: the list renders one lookup per territory, and a linear scan
 * inside a render loop is the kind of thing that is fine until a tenant has four hundred units.
 */
export function byId(units: readonly OrgUnit[]): Map<string, OrgUnit> {
  return new Map(units.map((unit) => [unit.id, unit]));
}

/**
 * A unit's name with its ancestors, outermost first — "Romania / Muntenia / București".
 *
 * A flat list of leaf names is ambiguous the moment two regions each have a "North", which is the
 * normal case rather than the odd one. Guarded against a cycle because the path is drawn from data
 * the API returns, and a render loop that never ends is a worse failure than a truncated label.
 */
export function pathOf(unit: OrgUnit, units: Map<string, OrgUnit>): string {
  const names: string[] = [];
  const seen = new Set<string>();

  let current: OrgUnit | undefined = unit;

  while (current && !seen.has(current.id)) {
    seen.add(current.id);
    names.unshift(current.name);
    current = current.parentId ? units.get(current.parentId) : undefined;
  }

  return names.join(" / ");
}
