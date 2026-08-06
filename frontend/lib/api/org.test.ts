import { describe, expect, it } from "vitest";

import { byId, isDescendantOf, pathOf, treeOf, type OrgUnit } from "@/lib/api/org";

const unit = (id: string, name: string, parentId: string | null = null): OrgUnit => ({
  id,
  name,
  parentId,
});

const ROMANIA = unit("ro", "Romania");
const SOUTH = unit("south", "Bucharest & South", "ro");
const TEAM = unit("team", "Team North", "south");
const MOLDOVA = unit("mol", "Moldova", "ro");

describe("treeOf", () => {
  it("puts a parent before its children, deepening as it goes", () => {
    // A flat list sorted by name puts a team next to a country and says nothing about which contains
    // which — and depth is the whole point of ORG-01, where the levels are the tenant's own.
    //
    // Deliberately shuffled: siblings are sorted here rather than inherited from the argument, so a
    // tree does not reorder itself when something upstream changes for unrelated reasons.
    expect(treeOf([TEAM, ROMANIA, MOLDOVA, SOUTH]).map((row) => [row.unit.name, row.depth])).toEqual([
      ["Romania", 0],
      ["Bucharest & South", 1],
      ["Team North", 2],
      ["Moldova", 1],
    ]);
  });

  it("keeps a unit whose parent is not in the list", () => {
    // A parent this caller cannot see, or one that arrived out of order. Dropping the child would
    // look like data missing rather than a tree the screen could not place.
    const orphan = unit("orphan", "Detached", "nowhere");

    expect(treeOf([ROMANIA, orphan]).map((row) => row.unit.name)).toEqual(["Romania", "Detached"]);
  });

  it("terminates on a cycle instead of recursing forever", () => {
    // This walks data the API returns. A render loop that never ends is a worse failure than a tree
    // drawn slightly wrong.
    const a = unit("a", "A", "b");
    const b = unit("b", "B", "a");

    expect(treeOf([a, b])).toHaveLength(2);
  });

  it("has nothing to draw for an empty hierarchy", () => {
    expect(treeOf([])).toEqual([]);
  });
});

describe("isDescendantOf", () => {
  it("finds a child, a grandchild, and the unit itself", () => {
    // All three would make a cycle if chosen as a parent, so all three stay out of the picker.
    const units = byId([ROMANIA, SOUTH, TEAM, MOLDOVA]);

    expect(isDescendantOf(SOUTH, "ro", units)).toBe(true);
    expect(isDescendantOf(TEAM, "ro", units)).toBe(true);
    expect(isDescendantOf(ROMANIA, "ro", units)).toBe(true);
  });

  it("does not find a sibling or an unrelated branch", () => {
    const units = byId([ROMANIA, SOUTH, TEAM, MOLDOVA]);

    expect(isDescendantOf(MOLDOVA, "south", units)).toBe(false);
    expect(isDescendantOf(ROMANIA, "south", units)).toBe(false);
  });
});

describe("pathOf", () => {
  it("names every ancestor, outermost first", () => {
    // "North" alone is ambiguous the moment two regions each have one, which is the normal case.
    expect(pathOf(TEAM, byId([ROMANIA, SOUTH, TEAM]))).toBe("Romania / Bucharest & South / Team North");
  });
});
