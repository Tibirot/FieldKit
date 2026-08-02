import { describe, expect, it } from "vitest";

import { routing } from "@/i18n/routing";

import { appShellEntries } from "./build-sw.mjs";

/**
 * `sw/index.js` reads its offline fallbacks back out of the precache manifest instead of hard-coding
 * them, so these entries *are* the contract between the build step and the worker. The regex below
 * is the same one the worker filters on — if one moves, this fails.
 */
const OFFLINE_URL = /^\/[^/]+\/offline$/;

const BUILD_ID = "test-build-id";

describe("app-shell precache entries", () => {
  const entries = appShellEntries(routing.locales, BUILD_ID);

  it("precaches an offline fallback for every locale the app routes", () => {
    expect(entries.map((entry) => entry.url).sort()).toEqual(
      routing.locales.map((locale) => `/${locale}/offline`).sort(),
    );
  });

  it("emits URLs in the shape the worker matches on", () => {
    for (const entry of entries) {
      expect(entry.url).toMatch(OFFLINE_URL);
    }
  });

  it("covers the default locale, which the worker uses for unprefixed paths", () => {
    expect(entries.map((entry) => entry.url)).toContain(`/${routing.defaultLocale}/offline`);
  });

  it("versions every entry by the build id, so a deploy invalidates the shell", () => {
    expect(entries.every((entry) => entry.revision === BUILD_ID)).toBe(true);
  });

  it("depends on no ordering — the worker looks entries up by locale", () => {
    const reversed = appShellEntries([...routing.locales].reverse(), BUILD_ID);

    expect(new Set(reversed.map((entry) => entry.url))).toEqual(
      new Set(entries.map((entry) => entry.url)),
    );
  });
});
