import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";

import { describe, expect, it } from "vitest";

import {
  coversPath,
  findScreen,
  isSectionVisible,
  LANDING,
  landingFor,
  NAVIGATION,
  permits,
  visibleScreens,
} from "@/components/back-office/navigation";
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
const screens = items.flatMap((item) => item.screens ?? []);

/** Everything, so a `has` predicate can be built from a set of what to withhold. */
const ALL_PERMISSIONS = [...new Set(screens.flatMap((screen) => screen.requires.flat()))];

const holding = (...granted: readonly string[]) => (permission: string) =>
  granted.includes(permission);

const holdingAllBut = (...withheld: readonly string[]) =>
  holding(...ALL_PERMISSIONS.filter((permission) => !withheld.includes(permission)));

describe("back-office navigation", () => {
  it("names every destination the product will have, not just the built ones", () => {
    // The decision this encodes: a nav listing only what exists misrepresents the product, and live
    // links to nothing would lie. Both halves must be present for the disabled-item design to mean
    // anything — a nav that quietly became all-built would pass a weaker assertion.
    expect(items.filter((item) => item.screens)).not.toHaveLength(0);
    expect(items.filter((item) => item.soon)).not.toHaveLength(0);
  });

  it("lands somewhere that exists", () => {
    // LANDING is where sign-in sends people. Pointing it at an unbuilt screen would put every new
    // session on a redirect loop through the guard. Asked of the screens now that a section has no
    // `href` of its own — which is the same question, one level down.
    expect(screens.some((screen) => screen.href === LANDING)).toBe(true);
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

  it("stays lit on every screen of a section", () => {
    /*
     * The bug `section` used to exist for, kept because the property outlived the field. `Journeys`
     * pointed at frequencies and `Configuration` at the weights, because a nav item should go
     * somewhere real — so standing on the working calendar or on a survey, "does the path start with
     * the href" said no and the section went dark. A user reading the rail to answer "where am I"
     * got no answer.
     *
     * Slice 4 deleted `section` rather than fixing it again: highlighting asks the screens, which is
     * what it always meant, and a prefix that can disagree with the screens no longer exists to.
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
    // A scheduled item has no screens, so nothing can be inside it. That used to need a guard
    // against an undefined `href`; now it falls out of the shape, and this pins the behaviour rather
    // than the guard.
    const scheduled = items.find((item) => item.soon !== undefined)!;

    expect(coversPath(scheduled, "/outlets")).toBe(false);
    expect(coversPath(scheduled, "/")).toBe(false);
  });

  it("sends the rail to the first screen the caller may open", () => {
    /*
     * What `href` used to say, derived — and the derivation is not the same answer for everybody,
     * which is the whole reason to derive it. Outlets' first screen is the outlet list; somebody
     * holding only `channel:read` has no outlet list, and lands on Channels.
     */
    const outlets = items.find((item) => item.key === "outlets")!;

    expect(landingFor(outlets, holdingAllBut())).toBe("/outlets");
    expect(landingFor(outlets, holding("channel:read"))).toBe("/outlets/channels");
    expect(landingFor(outlets, holding())).toBeUndefined();
  });

  it("never sends the rail somewhere the panel would not list", () => {
    /*
     * The invariant that replaces slice 1's `href`-is-one-of-its-screens check. That one held a
     * second copy of the destination honest; there is no second copy now, so what is worth pinning
     * is the composition: everything the rail draws has a destination, and every destination is a
     * screen of the section it belongs to.
     */
    for (const item of items) {
      if (!isSectionVisible(item, holdingAllBut())) continue;
      if (item.soon) continue;

      const landing = landingFor(item, holdingAllBut());

      expect(landing, `${item.key} is drawn with nowhere to go`).toBeDefined();
      expect(item.screens.some((screen) => screen.href === landing)).toBe(true);
    }
  });
});

describe("the screens inside a section", () => {
  /*
   * **"Covers every back-office route the app serves" used to live here, and moved out in slice 2.**
   *
   * It is now `scripts/check-reachability.mjs`, beside the two edge checks it belongs with, and it
   * is not duplicated back into this file. Two implementations of one rule drift, and they drift
   * quietly in the direction of agreeing with whichever was edited last — which is the failure this
   * whole gate exists to catch, arriving through the gate.
   *
   * What stays here is everything about the model *itself*: its keys, its catalog, its permissions,
   * and which screen owns a path. What left is the one assertion about the model's edge with the
   * router, which is a different question and now has its own name on the PR.
   */
  it("names every screen in both catalogs", () => {
    // The same hazard `Nav.items` has: a screen added without a translation renders the literal key
    // in the panel, which next-intl returns rather than throwing (the W11½ R5 lesson).
    for (const locale of routing.locales) {
      for (const screen of screens) {
        expect(
          catalogs[locale].Nav.screens[screen.key],
          `${locale}: Nav.screens.${screen.key}`,
        ).toBeTruthy();
      }
    }
  });

  it("gives every screen a distinct key and a distinct route", () => {
    expect(new Set(screens.map((screen) => screen.key)).size).toBe(screens.length);
    expect(new Set(screens.map((screen) => screen.href)).size).toBe(screens.length);
  });

  it("still lands where the item says it does", () => {
    /*
     * The invariant that keeps `href` honest until slice 4 derives it. An item's own destination has
     * to be one of its screens, or the sidebar opens a screen the panel will not list — the failure
     * mode of moving a link into a model while another copy still points at the old one.
     */
    for (const item of items) {
      if (item.href === undefined) continue;

      expect(
        item.screens.some((screen) => screen.href === item.href),
        `${item.key}: ${item.href} is not one of its screens`,
      ).toBe(true);
    }
  });

  it("asks for both permissions where the screen needs both", () => {
    /*
     * The finding that decided the shape of `Requirement`, pinned so a later tidy cannot flatten it
     * back to a list. Assortments and order minimums are organised **by channel**: a reader without
     * `channel:read` gets a selector with nothing in it and no way to tell why, which is why
     * `ProductActions` gated them on both.
     */
    const products = items.find((item) => item.key === "products")!;

    expect(visibleScreens(products, holding("product:read")).map((screen) => screen.key)).toEqual([
      "catalogue",
      "classification",
      "priceLists",
      "promotions",
    ]);

    expect(
      visibleScreens(products, holding("product:read", "channel:read")).map((screen) => screen.key),
    ).toHaveLength(6);
  });

  it("never leaves a screen ungated", () => {
    /*
     * `permits` reads an empty requirement as satisfied — `every` over nothing is true — which is
     * the correct answer for the operator and the wrong one for this model. A screen written with
     * `requires: []` would be shown to everybody, silently, and look exactly like a screen somebody
     * had thought about. There is nothing in the back office that everybody may read, so the rule is
     * simply that the list is never empty.
     */
    expect(permits([], holding())).toBe(true);

    for (const screen of screens) {
      expect(screen.requires.length, `${screen.key} is gated on nothing`).toBeGreaterThan(0);
      for (const anyOf of screen.requires) {
        expect(anyOf.length, `${screen.key} has an empty permission group`).toBeGreaterThan(0);
      }
    }
  });

  it("asks the predicate about one permission at a time", () => {
    /*
     * **The regression test for a bug every other test in this file was blind to** (slice 3).
     *
     * `permits` used to pass its predicate straight to `some`, which supplies `(element, index,
     * array)`. Every fake in this file is a one-parameter arrow, so the extra arguments fell on the
     * floor and nine assertions agreed the model was fine.
     *
     * The predicate the app actually ships is `usePermissions().has`, which is **variadic and means
     * all-of** — so it was asked whether the caller holds `"product:read"` *and* `0` *and*
     * `["product:read"]`, and answered no. Every screen was hidden from everyone; the panel rendered
     * nothing at all and did so silently, which is how it survived a suite that was green.
     *
     * So this fake is variadic on purpose, in the shape of the real one. A single-argument fake
     * cannot fail here, which is precisely what went wrong.
     */
    const variadic = (...required: readonly string[]) =>
      required.every((permission) => permission === "product:read");

    expect(permits([["product:read"]], variadic)).toBe(true);
    expect(permits([["product:read"], ["channel:read"]], variadic)).toBe(false);
  });

  it("reads a group as any-of and the groups as all-of", () => {
    // Territories is the any-of case — either permission opens it, because the page holds sections
    // with different ones.
    expect(permits([["territory:read", "orgunit:read"]], holding("orgunit:read"))).toBe(true);
    expect(permits([["product:read"], ["channel:read"]], holding("product:read"))).toBe(false);
    expect(permits([], holding())).toBe(true);
  });

  it("hides a section only when every screen in it is hidden", () => {
    const outlets = items.find((item) => item.key === "outlets")!;
    const scheduled = items.find((item) => item.soon !== undefined)!;

    // Four screens, four different permissions: holding any one of them is a reason to draw Outlets.
    expect(isSectionVisible(outlets, holding("channel:read"))).toBe(true);
    expect(isSectionVisible(outlets, holdingAllBut("outlet:read"))).toBe(true);
    expect(isSectionVisible(outlets, holding())).toBe(false);

    // A scheduled section is a fact about the product, so it is shown to everyone — the rule the
    // sidebar already applies one level up, unchanged by having screens below it.
    expect(isSectionVisible(scheduled, holding())).toBe(true);
  });

  it("gives a record-detail route to the screen above it, not to the section index", () => {
    /*
     * Longest match rather than first, and this is what it buys: `/products` covers every route in
     * its own section, so a first-match scan answers `catalogue` for all of them and the panel
     * highlights the wrong row on five screens out of six.
     */
    expect(findScreen("/products/price-lists/019ff1e1/scope")?.screen.key).toBe("priceLists");
    expect(findScreen("/products/promotions/019ff1e1/tiers")?.screen.key).toBe("promotions");
    expect(findScreen("/products/classification/tax-classes/019ff1e1/rates")?.screen.key).toBe(
      "classification",
    );
    expect(findScreen("/outlets/019ff1e1/assortment")?.screen.key).toBe("outletList");
    expect(findScreen("/configuration/surveys/new")?.screen.key).toBe("surveys");

    // And a section index still resolves to itself rather than to whichever screen sorts first.
    expect(findScreen("/products")?.screen.key).toBe("catalogue");
    expect(findScreen("/outlets")?.screen.key).toBe("outletList");
  });

  it("answers nothing for a route no section owns", () => {
    // The field app and the sign-in screen are outside this navigation entirely; a breadcrumb built
    // on a non-answer here would read "undefined / undefined".
    expect(findScreen("/field")).toBeUndefined();
    expect(findScreen("/login")).toBeUndefined();
    expect(findScreen("/journeys-archive")).toBeUndefined();
  });
});
