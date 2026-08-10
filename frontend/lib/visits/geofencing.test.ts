import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";

import { describe, expect, it } from "vitest";

import { assess, distanceMetres, type GeoPoint } from "@/lib/visits/geofencing";

/**
 * The shared geofence vectors, run against the device mirror (`VIS-01`, `VIS-02`) — W9 slice 3.
 *
 * The fourth rule this repository implements twice, and the first that is not about money. The
 * others get a second opinion — a price is recomputed server-side when an order is placed — and this
 * one does not: `IVisitIngest` stores the device's verdict unmodified, on purpose, so a mirror that
 * drifts writes exceptions that never happened and nothing downstream can tell.
 */
type VectorPoint = { latitude: number; longitude: number } | null;

type AssessmentVector = {
  name?: string;
  at: VectorPoint;
  outlet: VectorPoint;
  radiusMetres: number;
  presenceExpected: boolean;
  expected: { inside: boolean; distanceMetres: number | null; reasonRequired: boolean };
};

type GeofenceFile = { version: number; assessment: AssessmentVector[] };

function load(file: string): GeofenceFile {
  const path = fileURLToPath(new URL(`../../../vectors/visits/${file}`, import.meta.url));

  return JSON.parse(readFileSync(path, "utf8")) as GeofenceFile;
}

const handWritten = load("geofence.v1.json");
const generated = load("geofence.generated.v1.json");

/**
 * How far apart two correct implementations may land, in metres.
 *
 * A micron — matched to `GeofenceVectorTests.ToleranceMetres`, and the two are a pair: a tolerance
 * that differed between the languages would let one side accept what the other rejects, which is the
 * disagreement the file exists to detect.
 *
 * On the machine these expectations were generated on, .NET and V8 agreed to the last bit. This is
 * headroom for the runtimes nobody has measured — CI's Linux .NET, a rep's phone — where `sin`,
 * `cos` and `asin` come from different libraries and are not required to be correctly rounded.
 */
const TOLERANCE_METRES = 1e-6;

function point(value: VectorPoint): GeoPoint | null {
  return value === null ? null : { latitude: value.latitude, longitude: value.longitude };
}

function label(vector: AssessmentVector, index: number): string {
  return vector.name ?? `case ${index}`;
}

describe.each([
  ["hand-written", handWritten],
  ["generated", generated],
])("the %s geofence vectors", (_kind, file) => {
  it("is the version this suite was written against", () => {
    // A file whose cases changed meaning bumps its version, so a mirror running an older one fails
    // loudly rather than quietly proving yesterday's rule.
    expect(file.version).toBe(1);
  });

  it("carries coordinates as JSON numbers", () => {
    // The mirror image of the pricing files' format guard, and it is worth asserting for the same
    // reason. There, money must be a *string*, because `JSON.parse` would turn it into a float
    // before the engine saw it. Here the values genuinely are doubles on both sides — so a string
    // would mean somebody had made the mirror parse text, and the two engines would be comparing
    // their parsers rather than their geometry.
    const offenders: string[] = [];

    file.assessment.forEach((vector, index) => {
      for (const [path, value] of [
        [`${label(vector, index)}.at.latitude`, vector.at?.latitude],
        [`${label(vector, index)}.at.longitude`, vector.at?.longitude],
        [`${label(vector, index)}.outlet.latitude`, vector.outlet?.latitude],
        [`${label(vector, index)}.outlet.longitude`, vector.outlet?.longitude],
        [`${label(vector, index)}.radiusMetres`, vector.radiusMetres],
      ] as const) {
        if (value !== undefined && typeof value !== "number") offenders.push(String(path));
      }
    });

    expect(offenders).toEqual([]);
  });

  it.each(file.assessment.map((vector, index) => [label(vector, index), vector] as const))(
    "%s",
    (_name, vector) => {
      const assessment = assess(
        point(vector.at),
        point(vector.outlet),
        vector.radiusMetres,
        vector.presenceExpected,
      );

      // Exactly, both of them. These are the answer being recorded, and the whole reason the
      // generator keeps its cases clear of the radius boundary: a case sitting on it would have a
      // verdict two correct engines could legitimately split on.
      expect(assessment.inside).toBe(vector.expected.inside);
      expect(assessment.reasonRequired).toBe(vector.expected.reasonRequired);

      if (vector.expected.distanceMetres === null) {
        // Not the same as zero, and the file has cases for both: a visit with no measurable distance
        // and one taken at the pin are different records.
        expect(assessment.distanceMetres).toBeNull();
        return;
      }

      expect(assessment.distanceMetres).not.toBeNull();
      expect(assessment.distanceMetres!).toBeCloseTo(vector.expected.distanceMetres, 6);
      expect(Math.abs(assessment.distanceMetres! - vector.expected.distanceMetres)).toBeLessThan(
        TOLERANCE_METRES,
      );
    },
  );
});

describe("the distance itself", () => {
  it("is symmetric", () => {
    // A property the vector files cannot state, because each case names one direction. Haversine is
    // symmetric by construction; an implementation that subtracted in the wrong order somewhere
    // would still pass every vector and fail this.
    const shop = { latitude: 44.4638, longitude: 26.0946 };
    const away = { latitude: 44.4838, longitude: 26.1146 };

    expect(distanceMetres(shop, away)).toBeCloseTo(distanceMetres(away, shop), 9);
  });

  it("is zero for a point and itself, exactly", () => {
    // Structural rather than arithmetic: both deltas are exactly 0, so `sin(0)` is exactly 0 and no
    // library difference can reach this. It is the one distance worth asserting exact equality on.
    const shop = { latitude: 44.4638, longitude: 26.0946 };

    expect(distanceMetres(shop, shop)).toBe(0);
  });

  it("never returns NaN, even where the haversine exceeds one", () => {
    // NaN here would be the worst failure this function has: it compares `false` against every
    // radius, so a rep would be silently marked outside and asked to explain a shop they were
    // standing in.
    //
    // Rounding really does push the haversine above 1 near the antipodes — 1 + 2^-52 at the worst
    // point below. What the clamp then guards against does *not* happen: `Math.sqrt` of that value
    // rounds back to exactly 1 (ties-to-even), measured across 6.5 million antipodal pairs. So
    // deleting `Math.min(1, …)` breaks no test, and this assertion holds for a reason one layer
    // deeper than the clamp. Both implementations keep it as insurance for a future change to the
    // expression; neither this test nor the vector file pretends to exercise it.
    const worst = distanceMetres(
      { latitude: -87.5, longitude: -180 },
      { latitude: 87.5, longitude: 0 },
    );

    expect(Number.isNaN(worst)).toBe(false);
    expect(worst).toBeGreaterThan(20_000_000);
  });
});
