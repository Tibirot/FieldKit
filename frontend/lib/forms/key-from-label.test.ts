import { describe, expect, it } from "vitest";

import { keyFromLabel } from "@/lib/forms/key-from-label";

/**
 * Moved out of `field-definition-browser.test.tsx` when the survey editor became the second caller
 * (W10 slice 9a). The function decides an identifier that is immutable the moment it is saved, so it
 * is tested where it lives rather than beside whichever screen happened to need it first.
 */
describe("keyFromLabel", () => {
  it("folds diacritics rather than dropping them", () => {
    // This product ships in Romanian. Mapping `ț` to an underscore would produce `suprafa_a` —
    // a key nobody would have chosen, and one that is immutable the moment it is saved.
    expect(keyFromLabel("Suprafață de raft")).toBe("suprafata_de_raft");
  });

  it("never produces a key the server would refuse", () => {
    expect(keyFromLabel("3G coverage")).toBe("g_coverage");
    expect(keyFromLabel("  Chiller  count!  ")).toBe("chiller_count");
    expect(keyFromLabel("???")).toBe("");
    expect(keyFromLabel("x".repeat(80))).toHaveLength(60);
  });
});
