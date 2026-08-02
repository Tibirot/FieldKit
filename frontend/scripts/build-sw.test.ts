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
  const entries = appShellEntries(routing.locales, routing.defaultLocale, BUILD_ID);

  it("precaches an offline fallback for every locale the app routes", () => {
    expect(entries.map((entry) => entry.url).sort()).toEqual(
      routing.locales.map((locale) => `/${locale}/offline`).sort(),
    );
  });

  it("puts the default locale first — the worker uses it for unprefixed paths", () => {
    expect(entries[0].url).toBe(`/${routing.defaultLocale}/offline`);
  });

  it("emits URLs in the shape the worker matches on", () => {
    for (const entry of entries) {
      expect(entry.url).toMatch(OFFLINE_URL);
    }
  });

  it("versions every entry by the build id, so a deploy invalidates the shell", () => {
    expect(entries.every((entry) => entry.revision === BUILD_ID)).toBe(true);
  });

  it("is stable regardless of the order locales are configured in", () => {
    const reversed = appShellEntries([...routing.locales].reverse(), routing.defaultLocale, BUILD_ID);

    expect(reversed[0].url).toBe(`/${routing.defaultLocale}/offline`);
    expect(new Set(reversed.map((entry) => entry.url))).toEqual(
      new Set(entries.map((entry) => entry.url)),
    );
  });
});
