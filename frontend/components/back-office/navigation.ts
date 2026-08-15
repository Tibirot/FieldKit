/**
 * The back-office navigation, as the wireframes draw it.
 *
 * **Every destination is listed, including the ones that do not exist yet.** A nav showing only the
 * built screens would misrepresent the product; live links to nothing would lie. An item with no
 * `href` renders visibly disabled and says which week it arrives — honest about the shape of what is
 * coming without pretending it is here.
 *
 * **A screen the signed-in user cannot read is a different case, and is hidden rather than
 * disabled.** "Arrives in W7" is a fact about the product and worth showing everyone; "you may not
 * see this" is a fact about the caller, constant for their session, and no click will change it. A
 * disabled item there is a dead control that explains nothing — the pattern this codebase keeps
 * rejecting elsewhere.
 *
 * **A section now carries its screens** (W12½ slice 1). Until this slice the sidebar had one level
 * and the seventeen screens below it lived in five `*-actions.tsx` rows — so of the seventeen, six
 * were in the navigation and eleven were reachable only by landing on a section index and spotting
 * the right button. Nothing was unreachable; what was missing was a level. The rows still render and
 * still hold those links: this slice moves the *knowledge* into the model so the panel (slice 3) and
 * the CI gate (slice 2) have one place to read it, and deletes the rows in slice 4.
 *
 * See the UX build-scope note (`docs/ux/README.md`) for the decision and the delivery plan for the
 * weeks.
 */

/** Doubles as the message-catalog key and the icon lookup — one item, one name, no third mapping. */
export type NavKey =
  | "dashboard"
  | "journeys"
  | "visits"
  | "orders"
  | "outlets"
  | "products"
  | "territories"
  | "configuration"
  | "users";

/**
 * A screen inside a section, and globally unique for the same reason {@link NavKey} is: it keys
 * `Nav.screens.*` in the catalog and will key the panel's icon map, and a name that means one thing
 * under Outlets and another under Products is a name that gets looked up wrong exactly once.
 */
export type NavScreenKey =
  | "journeyPlans"
  | "callFrequency"
  | "workingCalendars"
  | "outletList"
  | "channels"
  | "customFields"
  | "outletImport"
  | "catalogue"
  | "classification"
  | "assortments"
  | "priceLists"
  | "promotions"
  | "orderMinimums"
  | "territoryList"
  | "scoreWeights"
  | "surveys"
  | "userList";

/** Which week ships an unbuilt screen. Keys into `Nav.soon`, so the badge translates. */
export type NavSoon = "week5" | "week6" | "week7" | "week9" | "week11" | "week12";

/**
 * What a caller must hold to be shown something: **any-of within a group, all-of across groups.**
 *
 * A plain list would have been enough for the sections — `["territory:read", "orgunit:read"]` means
 * either, because a page can hold sections with different permissions. Reading the five action rows
 * to move their links in here found two screens that a plain list **cannot express**: assortments
 * and order minimums are gated on `product:read` *and* `channel:read`, because each is organised by
 * channel and a reader without the channel list gets a selector with nothing in it and no way to
 * tell why. Conjunctions of disjunctions covers both cases and stays data — inspectable by a test,
 * unlike the predicate function that would also have worked.
 */
export type Requirement = readonly (readonly string[])[];

export type NavScreen = {
  readonly key: NavScreenKey;
  readonly href: string;
  readonly requires: Requirement;
};

/**
 * An item is either **built** — it has a route — or **scheduled**, and says which week ships it.
 *
 * A union rather than two optional fields, so "neither" cannot be expressed. That state would render
 * a dead item with no explanation, which is the one outcome this nav exists to avoid, and it is
 * better refused by the compiler than caught in review.
 */
export type NavItem =
  | {
      readonly key: NavKey;
      readonly href: string;
      readonly soon?: never;
      /**
       * The route prefix this item owns, when that is not its `href`.
       *
       * Two sections point at a screen *inside* themselves rather than at an index — `Journeys` at
       * frequencies, `Configuration` at the weights — because a nav item should go somewhere real.
       * The cost is that the obvious active test, "does the path start with the href", says no on
       * every other screen in the section: standing on the working calendar, Journeys went unlit.
       *
       * So highlighting asks a different question from navigating. Matched on a segment boundary
       * rather than a bare prefix, so a future `/journeys-archive` would not light this up.
       *
       * **Both this and `href` are on notice.** Once the rail selects a section and the panel's
       * first visible screen is where it lands (slice 4), the destination is derived from `screens`
       * and the prefix is the section's own — neither has to be stated, and the invariant test in
       * `navigation.test.ts` exists to keep them honest until then.
       */
      readonly section?: string;
      /**
       * Any one of these is enough to show the item.
       *
       * Any-of rather than all-of because a page can hold sections with different permissions:
       * someone who may read roles but not users still has a reason to open Users & roles, and the
       * section they cannot read refuses itself with the API's own words.
       *
       * Equivalent to the single-group {@link Requirement} `[permissions]`, and **derived** once the
       * sidebar asks {@link isSectionVisible} instead (slice 4). Keeping it for now means the
       * sidebar renders exactly what it rendered before this slice — which is the point of a slice
       * that changes no pixels. It is not the same answer in every case: someone holding
       * `channel:read` and not `outlet:read` sees no Outlets item today, and so cannot reach the
       * channel list at all.
       */
      readonly permissions: readonly string[];
      /** In the order the panel lists them. The first visible one is where the section lands. */
      readonly screens: readonly NavScreen[];
    }
  | {
      readonly key: NavKey;
      readonly href?: never;
      readonly section?: never;
      readonly soon: NavSoon;
      readonly permissions?: never;
      readonly screens?: never;
    };

/**
 * Whether `pathname` is `route` or inside it.
 *
 * The segment boundary is the whole of it: a bare `startsWith` would light `Journeys` up on
 * `/journeys-archive`, and equality alone would leave it dark on every screen but one.
 */
function covers(route: string, pathname: string): boolean {
  return pathname === route || pathname.startsWith(`${route}/`);
}

/**
 * Whether a path is inside an item's section.
 *
 * Exported for its own tests and for the sidebar.
 */
export function coversPath(item: NavItem, pathname: string): boolean {
  if (item.href === undefined) return false;

  return covers(item.section ?? item.href, pathname);
}

/**
 * Whether a caller holding `granted` satisfies a {@link Requirement}.
 *
 * **`granted` is called with exactly one argument, and that is load-bearing.** Passing it straight
 * to `some` — `anyOf.some(granted)` — hands it `(permission, index, array)`, which is harmless for a
 * single-parameter arrow and wrong for the predicate this actually ships with: `usePermissions().has`
 * is variadic and means *all of these*, so it was being asked whether the caller holds the
 * permission **and the number 0**. Always false. The panel rendered nothing at all, and slice 1's
 * tests passed throughout because their fake predicate took one parameter.
 */
export function permits(requires: Requirement, granted: (permission: string) => boolean): boolean {
  return requires.every((anyOf) => anyOf.some((permission) => granted(permission)));
}

/** The screens of a section the caller may actually open, in panel order. */
export function visibleScreens(
  item: NavItem,
  granted: (permission: string) => boolean,
): readonly NavScreen[] {
  return item.screens?.filter((screen) => permits(screen.requires, granted)) ?? [];
}

/**
 * Whether a section is worth drawing at all.
 *
 * **Any of its screens**, which is the composition the two honesty rules already imply one level
 * down: a scheduled section is a fact about the product and is shown to everyone, and a built one is
 * worth showing to anyone who can open at least one thing inside it. A section drawn with an empty
 * panel would be the dead control this file rejects everywhere else.
 */
export function isSectionVisible(
  item: NavItem,
  granted: (permission: string) => boolean,
): boolean {
  return item.soon !== undefined || visibleScreens(item, granted).length > 0;
}

/**
 * Which screen owns `pathname`, if any — **longest match wins**.
 *
 * The eleven record-detail routes have no screen of their own and belong to the one above them:
 * `/products/price-lists/{id}/scope` is the price-list screen still. Longest match rather than first
 * is load-bearing, because `/products` is a prefix of every screen in its own section and a
 * first-match scan would answer `catalogue` for all six.
 */
export function findScreen(
  pathname: string,
): { readonly item: NavItem; readonly screen: NavScreen } | undefined {
  let best: { item: NavItem; screen: NavScreen } | undefined;

  for (const group of NAVIGATION) {
    for (const item of group.items) {
      for (const screen of item.screens ?? []) {
        if (!covers(screen.href, pathname)) continue;
        if (best && best.screen.href.length >= screen.href.length) continue;

        best = { item, screen };
      }
    }
  }

  return best;
}

export type NavGroup = {
  /** Keys into `Nav.groups`, or null for the ungrouped items at the top. */
  readonly key: "masterData" | "admin" | null;
  readonly items: readonly NavItem[];
};

export const NAVIGATION: readonly NavGroup[] = [
  {
    key: null,
    items: [
      { key: "dashboard", soon: "week12" },
      // Points at frequencies rather than at a section index: it is the first journey screen that
      // exists, and the same reasoning that lands sign-in on Outlets applies — a nav item should go
      // somewhere real. It moves to the plan when the plan exists (W7 slice 10c).
      {
        key: "journeys",
        href: "/journeys/frequencies",
        section: "/journeys",
        permissions: ["journey:read"],
        // Everything a reader can reach here refuses its own write controls, so there is nothing to
        // hide behind a second permission — the reasoning `JourneyActions` already carried.
        screens: [
          { key: "journeyPlans", href: "/journeys", requires: [["journey:read"]] },
          { key: "callFrequency", href: "/journeys/frequencies", requires: [["journey:read"]] },
          { key: "workingCalendars", href: "/journeys/calendars", requires: [["journey:read"]] },
        ],
      },
      { key: "visits", soon: "week9" },
      { key: "orders", soon: "week11" },
    ],
  },
  {
    key: "masterData",
    items: [
      {
        key: "outlets",
        href: "/outlets",
        permissions: ["outlet:read"],
        /*
         * Four screens and four different permissions, which is why this section is the one that
         * proves the model is worth having. Channels are their own (`channel:write` is what the
         * importer pointedly does not hold, so a typo cannot mint "Modren Trade" as a permanent
         * classification); custom fields belong to Configuration, because maintaining outlets is not
         * the same authority as deciding what an outlet *is*; and the importer is a write.
         *
         * `/outlets/new` is deliberately absent. It is a create form reached from the list it adds
         * to, not a place — and the **New outlet** button that opens it is a write control, which
         * slice 4 has to re-home somewhere real rather than into a navigation panel.
         */
        screens: [
          { key: "outletList", href: "/outlets", requires: [["outlet:read"]] },
          { key: "channels", href: "/outlets/channels", requires: [["channel:read"]] },
          { key: "customFields", href: "/outlets/custom-fields", requires: [["config:read"]] },
          { key: "outletImport", href: "/outlets/import", requires: [["outlet:write"]] },
        ],
      },
      {
        key: "products",
        href: "/products",
        permissions: ["product:read"],
        /*
         * Ordered as the decisions are taken rather than as the action row happened to list them:
         * what a product *is*, then which shops must stock it, then what it costs, then what comes
         * off that. Two of the six need `channel:read` on top of `product:read` — see
         * {@link Requirement}.
         */
        screens: [
          { key: "catalogue", href: "/products", requires: [["product:read"]] },
          { key: "classification", href: "/products/classification", requires: [["product:read"]] },
          {
            key: "assortments",
            href: "/products/assortments",
            requires: [["product:read"], ["channel:read"]],
          },
          { key: "priceLists", href: "/products/price-lists", requires: [["product:read"]] },
          { key: "promotions", href: "/products/promotions", requires: [["product:read"]] },
          {
            key: "orderMinimums",
            href: "/products/order-minimums",
            requires: [["product:read"], ["channel:read"]],
          },
        ],
      },
      {
        key: "territories",
        href: "/territories",
        permissions: ["territory:read", "orgunit:read"],
        screens: [
          {
            key: "territoryList",
            href: "/territories",
            requires: [["territory:read", "orgunit:read"]],
          },
        ],
      },
    ],
  },
  {
    key: "admin",
    items: [
      // Points at the weights rather than a section index, for the reason `journeys` points at
      // frequencies: a nav item should go somewhere real, and this is the first screen of the
      // wireframe's "visit-workflow / audit builder" to exist (W10 slice 8). Surveys joined it in
      // slice 9b, which is what made `section` necessary.
      {
        key: "configuration",
        href: "/configuration/score-weights",
        section: "/configuration",
        permissions: ["config:read"],
        screens: [
          { key: "scoreWeights", href: "/configuration/score-weights", requires: [["config:read"]] },
          { key: "surveys", href: "/configuration/surveys", requires: [["config:read"]] },
        ],
      },
      {
        key: "users",
        href: "/users",
        permissions: ["user:read", "role:read"],
        screens: [{ key: "userList", href: "/users", requires: [["user:read", "role:read"]] }],
      },
    ],
  },
];

/** Where signing in lands. Outlets, because it is the first screen that exists (UX build scope). */
export const LANDING = "/outlets";
