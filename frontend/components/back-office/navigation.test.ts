import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";

import { describe, expect, it } from "vitest";

import { coversPath, LANDING, NAVIGATION } from "@/components/back-office/navigation";
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

  it("stays lit on every screen of a section it points into", () => {
    /*
     * The bug `section` exists for, and it was live before this: `Journeys` points at frequencies
     * and `Configuration` at the weights, because a nav item should go somewhere real — so standing
     * on the working calendar or on a survey, "does the path start with the href" said no and the
     * section went dark. A user reading the sidebar to answer "where am I" got no answer.
     */
    const journeys = items.find((item) => item.key === "journeys")!;
    const configuration = items.find((item) => item.key === "configuration")!;

    expect(coversPath(journeys, "/journeys/frequencies")).toBe(true);
    expect(coversPath(journeys, "/journeys/calendars")).toBe(true);
    expect(coversPath(journeys, "/journeys")).toBe(true);

    expect(coversPath(configuration, "/configuration/score-weights")).toBe(true);
    expect(coversPath(configuration, "/configuration/surveys")).toBe(true);
    expect(coversPath(configuration, "/configuration/surveys/019ff1e1-cdfc-71e6")).toBe(true);
  });

  it("does not light a section up for a route that merely starts with its name", () => {
    // Why the match is on a segment boundary rather than a bare prefix. No such route exists today,
    // which is exactly when this is cheap to get right.
    const journeys = items.find((item) => item.key === "journeys")!;

    expect(coversPath(journeys, "/journeys-archive")).toBe(false);
    expect(coversPath(journeys, "/journeysomething/frequencies")).toBe(false);
  });

  it("leaves an unbuilt item unlit wherever you stand", () => {
    // A scheduled item has no route, so nothing can be inside it. Without the guard the section
    // would fall back to an undefined href and throw on the template literal.
    const scheduled = items.find((item) => item.soon !== undefined)!;

    expect(coversPath(scheduled, "/outlets")).toBe(false);
    expect(coversPath(scheduled, "/")).toBe(false);
  });

  it("keeps every section a real prefix of the href that points into it", () => {
    // Otherwise the item highlights on screens it cannot reach and stays dark on the one it opens —
    // a `section` typo the type system cannot see, because both are strings.
    for (const item of items) {
      if (item.href === undefined || item.section === undefined) continue;

      expect(coversPath(item, item.href), `${item.key}: ${item.section} vs ${item.href}`).toBe(true);
    }
  });
});
