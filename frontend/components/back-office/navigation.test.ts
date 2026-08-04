import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";

import { describe, expect, it } from "vitest";

import { LANDING, NAVIGATION } from "@/components/back-office/navigation";
import { routing } from "@/i18n/routing";

/**
 * The navigation's promises, checked.
 *
 * The type already refuses an item that is neither built nor scheduled. What it cannot see is the
 * message catalog: a nav item added without translations renders the literal key
 * (`Nav.items.journeys`) in the sidebar, which looks like a bug in production and like nothing at
 * all in review.
 */
const catalogs = Object.fromEntries(
  routing.locales.map((locale) => [
    locale,
    JSON.parse(
      readFileSync(fileURLToPath(new URL(`../../messages/${locale}.json`, import.meta.url)), "utf8"),
    ) as { Nav: Record<string, Record<string, string>> },
  ]),
);

const items = NAVIGATION.flatMap((group) => group.items);

describe("back-office navigation", () => {
  it("names every destination the product will have, not just the built ones", () => {
    // The decision this encodes: a nav listing only what exists misrepresents the product, and live
    // links to nothing would lie. Both halves must be present for the disabled-item design to mean
    // anything — a nav that quietly became all-built would pass a weaker assertion.
    expect(items.filter((item) => item.href)).not.toHaveLength(0);
    expect(items.filter((item) => item.soon)).not.toHaveLength(0);
  });

  it("lands somewhere that exists", () => {
    // LANDING is where sign-in sends people. Pointing it at an unbuilt screen would put every new
    // session on a redirect loop through the guard.
    expect(items.some((item) => item.href === LANDING)).toBe(true);
  });

  it.each(routing.locales)("has a %s label for every item, group and week", (locale) => {
    const nav = catalogs[locale].Nav;

    for (const item of items) {
      expect(nav.items[item.key], `${locale}: Nav.items.${item.key}`).toBeTruthy();

      if (item.soon) {
        expect(nav.soon[item.soon], `${locale}: Nav.soon.${item.soon}`).toBeTruthy();
      }
    }

    for (const group of NAVIGATION) {
      if (group.key) {
        expect(nav.groups[group.key], `${locale}: Nav.groups.${group.key}`).toBeTruthy();
      }
    }
  });

  it("gives every item a distinct key, since the key is also the icon and the message", () => {
    expect(new Set(items.map((item) => item.key)).size).toBe(items.length);
  });
});
