import { existsSync, readFileSync } from "node:fs";
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
 *
 * Assertions read `package-lock.json`, not `node_modules`: the lockfile is the artifact that proves
 * *every* copy in the tree is patched (a nested `next/node_modules/postcss@8.4.31` is exactly what
 * this change removed), and it doesn't depend on install state — `sharp` is an optional dependency
 * and can legitimately be absent.
 */

const here = (p: string) => fileURLToPath(new URL(p, import.meta.url));
const readJson = (p: string) => JSON.parse(readFileSync(here(p), "utf8"));

const lockfile = readJson("./package-lock.json") as {
  packages: Record<string, { version?: string; dependencies?: Record<string, string>; optionalDependencies?: Record<string, string> }>;
};

/** `"8.5.25"` → `[8, 5, 25]`, for comparison without pulling in a semver dependency. */
function parseVersion(version: string): number[] {
  return version.split("-")[0].split(".").map(Number);
}

function atLeast(actual: string, minimum: string): boolean {
  // A prerelease sorts below its release (0.35.0-rc.1 < 0.35.0). Unreachable through `^` ranges,
  // which don't match prereleases, but treat it as failing rather than silently passing.
  if (actual.includes("-") && parseVersion(actual).join() === parseVersion(minimum).join()) {
    return false;
  }
  const a = parseVersion(actual);
  const m = parseVersion(minimum);
  for (let i = 0; i < Math.max(a.length, m.length); i++) {
    const diff = (a[i] ?? 0) - (m[i] ?? 0);
    if (diff !== 0) return diff > 0;
  }
  return true;
}

/** Every resolved copy of `name` anywhere in the tree, keyed by its lockfile path. */
function resolvedCopies(name: string): [string, string][] {
  const pattern = new RegExp(`(?:^|/)node_modules/${name}$`);
  return Object.entries(lockfile.packages)
    .filter(([path, entry]) => pattern.test(path) && entry.version)
    .map(([path, entry]) => [path, entry.version!]);
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
  const next = lockfile.packages["node_modules/next"];

  it.each(Object.entries(NEXT_DECLARATIONS_WHEN_OVERRIDDEN))(
    "next still declares %s the way it did when the override was added",
    (path, expected) => {
      const [field, name] = path.split(".") as ["dependencies" | "optionalDependencies", string];
      const actual = next?.[field]?.[name];

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
    "every resolved copy of %s is patched",
    (name, minimum) => {
      const copies = resolvedCopies(name);
      expect(copies.length, `no ${name} found in the lockfile`).toBeGreaterThan(0);

      const vulnerable = copies
        .filter(([, version]) => !atLeast(version, minimum))
        .map(([path, version]) => `${path}@${version}`);

      expect(
        vulnerable,
        `below the patched ${minimum} — the override is not reaching every copy.`,
      ).toEqual([]);
    },
  );

  it("has an override for every package the guard covers", () => {
    const overrides = readJson("./package.json").overrides ?? {};
    expect(Object.keys(overrides).sort()).toEqual(Object.keys(MINIMUM_PATCHED).sort());
  });
});

/**
 * `sharp` is the half of this the build cannot check: Next loads it only at runtime for image
 * optimization, so `next build` never touches it and a green CI proves nothing about the 0.34→0.35
 * bump. This round-trips a real encode/decode to catch the likely failure — a native/ABI break —
 * though it can't prove every API Next calls is unchanged.
 *
 * Skipped when the optional dependency isn't installed, rather than failing a legitimate local tree
 * — but never in CI. A skip there would mean the one check that exercises sharp silently switched
 * itself off (an `--omit=optional` added to speed up the install would do it) and left a green build
 * behind. The lockfile assertion above doesn't cover this: it proves sharp is *recorded*, not that
 * it *runs*.
 */
const sharpInstalled = existsSync(here("./node_modules/sharp/package.json"));

if (!sharpInstalled && process.env.CI) {
  throw new Error(
    "sharp is not installed, so the override smoke test cannot run. In CI that is a failure, not a " +
      "skip — did the install step start omitting optional dependencies?",
  );
}

describe.skipIf(!sharpInstalled)("sharp (overridden to ^0.35.0)", () => {
  it("loads its native binding and round-trips an image", async () => {
    const sharp = (await import("sharp")).default;

    const png = await sharp({
      create: { width: 4, height: 4, channels: 3, background: { r: 255, g: 0, b: 0 } },
    })
      .png()
      .toBuffer();

    const resized = await sharp(png).resize(2, 2).png().toBuffer();
    const meta = await sharp(resized).metadata();

    expect(meta.width).toBe(2);
    expect(meta.height).toBe(2);
    expect(meta.format).toBe("png");
  });
});
