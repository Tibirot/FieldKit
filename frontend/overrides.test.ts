import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";

import { describe, expect, it } from "vitest";

/**
 * Guards the `overrides` in package.json — the transitive-CVE fixes documented in
 * docs/architecture/16-security.md §6.
 *
 * An override is a standing deviation from what a maintainer declared, and the usual failure mode
 * is that nobody removes it: the CVE gets fixed upstream, the override keeps silently clamping the
 * dependency, and a later legitimate major (postcss 9, sharp 0.36) is quietly held back with no
 * error anywhere. npm applies an override over a dependent's declared range without complaint.
 *
 * So the removal condition is a test rather than a comment. If Next changes what it declares — for
 * any reason, in either direction — this fails and forces someone to re-check whether the override
 * is still needed.
 */

const here = (p: string) => fileURLToPath(new URL(p, import.meta.url));
const readJson = (p: string) => JSON.parse(readFileSync(here(p), "utf8"));

/** `"8.5.25"` → `[8, 5, 25]`, for comparison without pulling in a semver dependency. */
function parseVersion(version: string): number[] {
  return version.split("-")[0].split(".").map(Number);
}

function atLeast(actual: string, minimum: string): boolean {
  const a = parseVersion(actual);
  const m = parseVersion(minimum);
  for (let i = 0; i < Math.max(a.length, m.length); i++) {
    const diff = (a[i] ?? 0) - (m[i] ?? 0);
    if (diff !== 0) return diff > 0;
  }
  return true;
}

/**
 * What Next declared when each override was introduced (checked 2026-08 against next@16.2.12,
 * which was also next@latest). These are the specs that make the patched versions unreachable:
 * an exact pin admits nothing else, and `^0.34.5` on a 0.x line stops before 0.35.0.
 */
const NEXT_DECLARATIONS_WHEN_OVERRIDDEN = {
  "dependencies.postcss": "8.4.31",
  "optionalDependencies.sharp": "^0.34.5",
} as const;

/** Lowest version that clears every advisory for the package (16-security.md §6). */
const MINIMUM_PATCHED = {
  postcss: "8.5.18", // GHSA-r28c-9q8g-f849 (also covers -6g55- and -qx2v-)
  sharp: "0.35.0", // GHSA-f88m-g3jw-g9cj
} as const;

describe("dependency overrides", () => {
  const next = readJson("./node_modules/next/package.json");

  it.each(Object.entries(NEXT_DECLARATIONS_WHEN_OVERRIDDEN))(
    "next still declares %s the way it did when the override was added",
    (path, expected) => {
      const [field, name] = path.split(".");
      const actual = next[field]?.[name];

      expect(
        actual,
        `next now declares ${name} as "${actual}" instead of "${expected}". That's the signal to ` +
          `re-evaluate: if the new spec admits a patched version, delete the override from ` +
          `package.json, regenerate the lockfile (docs/engineering/frontend-toolchain.md), and ` +
          `drop the row from docs/architecture/16-security.md §6.`,
      ).toBe(expected);
    },
  );

  it.each(Object.entries(MINIMUM_PATCHED))(
    "%s resolves to a patched version everywhere",
    (name, minimum) => {
      const installed = readJson(`./node_modules/${name}/package.json`).version;

      expect(
        atLeast(installed, minimum),
        `${name}@${installed} is below the patched ${minimum} — the override is not taking effect.`,
      ).toBe(true);
    },
  );

  it("has an override for every package the guard covers", () => {
    const overrides = readJson("./package.json").overrides ?? {};
    expect(Object.keys(overrides).sort()).toEqual(Object.keys(MINIMUM_PATCHED).sort());
  });
});
