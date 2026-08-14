import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";

import { describe, expect, it } from "vitest";

import { businessDay } from "@/lib/pricing/business-day";

/**
 * The device's half of `BR-PRD-6`, held to the C# engine by a shared file — W11½ R6b.
 *
 * **The first mirrored rule whose two implementations share no library.** Money is `decimal` against
 * `decimal.js`; the geofence is arithmetic both languages do natively. This is `Intl` against
 * `TimeZoneInfo`, over whatever zone database each runtime shipped — so agreement is inherited from
 * nothing and every case in the file is load-bearing.
 *
 * The suite runs in `Europe/Bucharest` (`vitest.config.ts`, deliberately non-UTC), which is why the
 * cases below name zones that are **not** the runner's: a rule that ignored its `timeZoneId`
 * argument would agree with half this file by coincidence.
 */
type DayVector = {
  name: string;
  at: string;
  timeZoneId: string;
  expected: string | null;
};

type BusinessDayFile = { version: number; cases: DayVector[] };

const vectors = JSON.parse(
  readFileSync(
    fileURLToPath(new URL("../../../vectors/pricing/business-day.v1.json", import.meta.url)),
    "utf8",
  ),
) as BusinessDayFile;

describe("the shared business-day vectors", () => {
  it("loads the file the C# engine reads", () => {
    // Guards the wiring, not the engine. If the path breaks or the file empties, the `it.each`
    // below silently becomes zero tests — a green suite that checked nothing.
    expect(vectors.version).toBe(1);
    expect(vectors.cases.length).toBeGreaterThanOrEqual(12);
    expect(new Set(vectors.cases.map((vector) => vector.name)).size).toBe(vectors.cases.length);
  });

  it("names zones this runtime is not already set to", () => {
    /*
     * The mirror of `BusinessDayVectorTests`'s non-vacuity check, from the side that can actually be
     * fooled. `Intl.DateTimeFormat` with no `timeZone` uses the *host's* — which here is
     * Europe/Bucharest — so an implementation that dropped the argument would still pass every
     * Bucharest case. This asserts the file does not consist only of those.
     */
    const elsewhere = vectors.cases.filter(
      (vector) => vector.timeZoneId !== "" && vector.timeZoneId !== "Europe/Bucharest",
    );

    expect(elsewhere.length).toBeGreaterThanOrEqual(4);
  });

  it.each(vectors.cases.map((vector) => [vector.name, vector] as const))(
    "%s",
    (_name, vector) => {
      expect(businessDay(new Date(vector.at), vector.timeZoneId)).toBe(vector.expected);
    },
  );
});

describe("the zone a device might not have", () => {
  it("declines an absent zone rather than silently using the phone's", () => {
    /*
     * **The guard the shared file cannot express**, because JSON has no `undefined`.
     *
     * `Intl.DateTimeFormat` with `timeZone: ""` throws, so the empty case in the vector file would
     * be caught by the `try` anyway. With `timeZone: undefined` it does **not** throw — it formats
     * in the *host's* zone, silently, which is the exact defect R6b removes.
     *
     * The type says `string`. A `ReferenceOutlet` written before W11½ R6a's version-20 upgrade has
     * the property absent, which is one migration between that impossibility and this function.
     */
    const absent = businessDay(new Date("2026-01-15T11:30:00Z"), undefined as unknown as string);

    expect(absent).toBeNull();

    // Named explicitly: the failure this prevents is *agreeing with the runner*, not throwing.
    expect(absent).not.toBe(businessDay(new Date("2026-01-15T11:30:00Z"), "Europe/Bucharest"));
  });

  it("declines a zone that is only whitespace", () => {
    expect(businessDay(new Date("2026-01-15T11:30:00Z"), "   ")).toBeNull();
  });
});
