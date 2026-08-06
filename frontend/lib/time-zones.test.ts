import { describe, expect, it } from "vitest";

import { zonesIncluding } from "@/lib/time-zones";

/** What the browser actually enumerates, so the tests are about the gap rather than about a stub. */
const known = Intl.supportedValuesOf("timeZone");

describe("zonesIncluding", () => {
  it("keeps a stored zone the browser does not enumerate", () => {
    // The bug this exists for. `UTC` is a zone the API accepts — `TimeZoneInfo.TryFindSystemTimeZoneById`
    // resolves it — and `Intl.supportedValuesOf` does not return it, so the required select rendered
    // empty and saving forced a different zone onto the outlet.
    expect(known).not.toContain("UTC");
    expect(zonesIncluding("UTC")).toContain("UTC");
  });

  it("puts the stored one first, where someone looking for it will see it", () => {
    expect(zonesIncluding("UTC")[0]).toBe("UTC");
  });

  it("does not duplicate a zone the browser already lists", () => {
    const zones = zonesIncluding("Europe/Bucharest");

    expect(zones.filter((zone) => zone === "Europe/Bucharest")).toHaveLength(1);
    expect(zones).toHaveLength(known.length);
  });

  it("offers the platform's list for a record that has no zone yet", () => {
    // A create form. Nothing stored, nothing to preserve.
    expect(zonesIncluding(null)).toEqual(known);
    expect(zonesIncluding(undefined)).toEqual(known);
    expect(zonesIncluding("")).toEqual(known);
  });

  it("is the platform's list and not a bundled one", () => {
    // A hard-coded set goes stale every time a country changes its rules, and the API validates
    // against the runtime's own database.
    expect(zonesIncluding(null).length).toBeGreaterThan(300);
    expect(zonesIncluding(null)).toContain("Europe/Bucharest");
  });
});
