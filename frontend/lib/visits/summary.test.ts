import { describe, expect, it } from "vitest";

import type { LocalVisit, LocalVisitStep } from "@/lib/sync/db";
import { minutesOnSite, unfinished } from "@/lib/visits/summary";

/**
 * The two facts a recap is made of (`VIS-09`) — W9 slice 10.
 *
 * Pure functions, so these are the cheap tests — but one of them replaced a copy that had already
 * been written twice, and the interesting cases are the ones neither copy had thought about.
 */
const CHECKED_IN = "2026-03-17T09:00:00.000Z";

function step(overrides: Partial<LocalVisitStep> & { order: number }): LocalVisitStep {
  return {
    stepId: `step-${overrides.order}`,
    type: "Task",
    mandatory: false,
    label: `Step ${overrides.order}`,
    notes: null,
    completedAtUtc: null,
    ...overrides,
  };
}

function visit(steps: LocalVisitStep[], overrides: Partial<LocalVisit> = {}): LocalVisit {
  return {
    id: "visit-1",
    outletId: "outlet-1",
    plannedVisitId: null,
    status: "inProgress",
    checkedInAtUtc: CHECKED_IN,
    checkInLatitude: null,
    checkInLongitude: null,
    checkInDistanceMetres: null,
    wasInsideGeofence: true,
    overrideReason: null,
    steps,
    checkedOutAtUtc: null,
    checkOutLatitude: null,
    checkOutLongitude: null,
    outcome: null,
    outcomeReason: null,
    ...overrides,
  };
}

describe("time in the shop", () => {
  it("counts from check-in to now while the visit is open", () => {
    // The case the sealed-record copy could not handle: it read `checkedOutAtUtc ?? checkedInAtUtc`
    // and therefore answered zero for every visit still being worked.
    const now = new Date("2026-03-17T09:18:40.000Z");

    expect(minutesOnSite(visit([]), now)).toBe(18);
  });

  it("counts to check-out once the visit is sealed, and ignores the clock after that", () => {
    const sealed = visit([], { checkedOutAtUtc: "2026-03-17T09:25:00.000Z", status: "checkedOut" });

    expect(minutesOnSite(sealed, new Date("2026-03-17T11:00:00.000Z"))).toBe(25);
  });

  it("floors rather than rounds, matching the server", () => {
    // `BR-VIS-5`: 18.9 reads as 18. A rep glancing at a number that disagrees with their watch by a
    // minute in the *generous* direction is the version of this that gets noticed.
    expect(minutesOnSite(visit([]), new Date("2026-03-17T09:18:59.000Z"))).toBe(18);
  });

  it("says zero rather than a negative when the device's clock moved backwards", () => {
    // A network time sync mid-visit is the ordinary cause, and "-3 minutes in the shop" is worse
    // than the small lie of zero.
    expect(minutesOnSite(visit([]), new Date("2026-03-17T08:57:00.000Z"))).toBe(0);
  });
});

describe("what is left undone", () => {
  it("lists the optional steps nobody did", () => {
    const open = unfinished(
      visit([
        step({ order: 1, label: "Shelf audit", mandatory: true }),
        step({ order: 2, label: "Fridge photo" }),
        step({ order: 3, label: "Chat to the owner" }),
      ]),
    );

    expect(open.map((entry) => entry.label)).toEqual(["Fridge photo", "Chat to the owner"]);
  });

  it("leaves the mandatory ones out, because another list already owns them", () => {
    // `BR-VIS-3` blocks check-out on mandatory steps and the check-out panel names them. Repeating
    // them here would put one fact in two places and make the lists look like they disagree.
    const open = unfinished(visit([step({ order: 1, label: "Shelf audit", mandatory: true })]));

    expect(open).toEqual([]);
  });

  it("is empty when everything optional is done", () => {
    const open = unfinished(
      visit([step({ order: 1, label: "Fridge photo", completedAtUtc: "2026-03-17T09:05:00.000Z" })]),
    );

    expect(open).toEqual([]);
  });
});
