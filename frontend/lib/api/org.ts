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

export type OrgUnitWrite = {
  name: string;
  /** Null makes it a root. */
  parentId: string | null;
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

export function createOrgUnit(accessToken: string, unit: OrgUnitWrite): Promise<OrgUnit> {
  return apiSend<OrgUnit>("POST", "/api/org/units", accessToken, unit);
}

/**
 * Renames and reparents in one call, because they are one edit on one screen.
 *
 * Splitting them would make "rename this team and move it under the new region" two requests that
 * can half-succeed — which the API avoided, and this should not reintroduce.
 */
export function updateOrgUnit(
  accessToken: string,
  id: string,
  unit: OrgUnitWrite,
): Promise<OrgUnit> {
  return apiSend<OrgUnit>("PUT", `/api/org/units/${id}`, accessToken, unit);
}

/**
 * Removes an org unit.
 *
 * Refused rather than cascaded while it still has children, staffed positions, or territories.
 * Deleting a region should not silently take its areas, the people in them, and the outlets those
 * territories made visible — the admin says what happens to each.
 */
export function deleteOrgUnit(accessToken: string, id: string): Promise<void> {
  return apiDelete(`/api/org/units/${id}`, accessToken);
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

/** A unit and how deep it sits, in the order a tree is read. */
export type OrgUnitRow = { unit: OrgUnit; depth: number };

/**
 * Flattens the hierarchy into rows, parents before their children.
 *
 * A flat list sorted by name puts a team next to a country and says nothing about which contains
 * which — and depth is the whole point of `ORG-01`, where the levels are the tenant's own.
 *
 * **Siblings are sorted here, not inherited from the argument.** The API happens to return units
 * ordered by name, but a tree whose sibling order depends on the order a caller assembled its input
 * is a tree that reorders itself when something upstream changes for unrelated reasons.
 *
 * **Orphans are appended rather than dropped.** A unit whose parent this caller cannot see, or which
 * arrived out of order, is still a unit; losing it from the screen would look like data missing
 * rather than a tree it could not place. The visited set makes a cycle finite for the same reason
 * `pathOf` guards against one: this walks data the API returns.
 */
export function treeOf(units: readonly OrgUnit[]): OrgUnitRow[] {
  const children = new Map<string | null, OrgUnit[]>();

  for (const unit of units) {
    const key = unit.parentId;

    children.set(key, [...(children.get(key) ?? []), unit]);
  }

  const rows: OrgUnitRow[] = [];
  const visited = new Set<string>();

  const byName = (left: OrgUnit, right: OrgUnit) => left.name.localeCompare(right.name);

  const walk = (parentId: string | null, depth: number) => {
    for (const unit of [...(children.get(parentId) ?? [])].sort(byName)) {
      if (visited.has(unit.id)) continue;

      visited.add(unit.id);
      rows.push({ unit, depth });
      walk(unit.id, depth + 1);
    }
  };

  walk(null, 0);

  for (const unit of units) {
    if (!visited.has(unit.id)) rows.push({ unit, depth: 0 });
  }

  return rows;
}

/**
 * Whether `candidate` is inside `unit`'s own subtree.
 *
 * Used to keep a unit out of its own parent picker: choosing a descendant would make a cycle, which
 * the API refuses — better to not offer the choice than to explain it afterwards, since unlike a
 * name collision this one is never what somebody meant.
 */
export function isDescendantOf(
  candidate: OrgUnit,
  unitId: string,
  units: Map<string, OrgUnit>,
): boolean {
  const seen = new Set<string>();

  let current: OrgUnit | undefined = candidate;

  while (current && !seen.has(current.id)) {
    if (current.id === unitId) return true;

    seen.add(current.id);
    current = current.parentId ? units.get(current.parentId) : undefined;
  }

  return false;
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

/**
 * A rep's coverage of a territory over a period (`ORG-04`).
 *
 * **`userId` is the Keycloak subject**, not the IAM row's id. A user profile has both, they are
 * different strings, and sending the wrong one is refused as "No such user in this tenant" — which
 * reads like a missing user rather than a mismatched identifier.
 */
export type RepAssignment = {
  id: string;
  territoryId: string;
  userId: string;
  /** Null when the directory no longer resolves the subject — the assignment still stands. */
  displayName: string | null;
  /** ISO `YYYY-MM-DD`, which is also what a native date input holds. */
  from: string;
  /** Null means open-ended: until further notice. */
  to: string | null;
  /**
   * Whether today falls inside the period.
   *
   * The server's answer, resolved in the *calling* user's timezone. Computing it here would use the
   * browser's, and the two disagree for anyone travelling — which is most of a sales organisation.
   */
  isCurrent: boolean;
};

export type RepAssignmentWrite = {
  userId: string;
  from: string;
  to: string | null;
};

export const assignmentsKey = (subject: string, territoryId: string) =>
  ["assignments", subject, territoryId] as const;

export function fetchAssignments(
  accessToken: string,
  territoryId: string,
  signal?: AbortSignal,
): Promise<RepAssignment[]> {
  return apiGet<RepAssignment[]>(
    `/api/org/territories/${territoryId}/assignments`,
    accessToken,
    signal,
  );
}

export function createAssignment(
  accessToken: string,
  territoryId: string,
  assignment: RepAssignmentWrite,
): Promise<RepAssignment> {
  return apiSend<RepAssignment>(
    "POST",
    `/api/org/territories/${territoryId}/assignments`,
    accessToken,
    assignment,
  );
}

export function updateAssignment(
  accessToken: string,
  id: string,
  assignment: RepAssignmentWrite,
): Promise<RepAssignment> {
  return apiSend<RepAssignment>("PUT", `/api/org/assignments/${id}`, accessToken, assignment);
}

export function deleteAssignment(accessToken: string, id: string): Promise<void> {
  return apiDelete(`/api/org/assignments/${id}`, accessToken);
}

/**
 * An outlet in a territory, named through the Outlets contract (`ORG-05`).
 *
 * Every field but the id is nullable because the membership is the fact Organization owns — an
 * outlet that no longer resolves keeps its row rather than disappearing, which would make a
 * territory quietly smaller than the count on the list screen says.
 */
export type TerritoryOutlet = {
  outletId: string;
  code: string | null;
  name: string | null;
  isOpen: boolean | null;
};

export const territoryOutletsKey = (subject: string, territoryId: string) =>
  ["territory-outlets", subject, territoryId] as const;

export function fetchTerritoryOutlets(
  accessToken: string,
  territoryId: string,
  signal?: AbortSignal,
): Promise<TerritoryOutlet[]> {
  return apiGet<TerritoryOutlet[]>(
    `/api/org/territories/${territoryId}/outlets`,
    accessToken,
    signal,
  );
}

/**
 * Puts outlets in a territory.
 *
 * A list rather than one at a time, because that is the shape of the decision — an admin carving up
 * a region assigns a handful at once. Idempotent, and refused as a set: one unknown id rejects the
 * whole request rather than half-applying.
 */
export function assignOutlets(
  accessToken: string,
  territoryId: string,
  outletIds: string[],
): Promise<void> {
  return apiSend<void>(
    "POST",
    `/api/org/territories/${territoryId}/outlets`,
    accessToken,
    { outletIds },
  );
}

export function removeOutlet(
  accessToken: string,
  territoryId: string,
  outletId: string,
): Promise<void> {
  return apiDelete(`/api/org/territories/${territoryId}/outlets/${outletId}`, accessToken);
}
