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
 * **A section carries its screens** (W12½ slices 1–4). The sidebar used to have one level, and the
 * seventeen screens below it lived in four `*-actions.tsx` rows — so of the seventeen, six were in
 * the navigation and eleven were reachable only by landing on a section index and spotting the right
 * button. Nothing was unreachable; what was missing was a level. Slice 1 moved the knowledge here,
 * slice 2 made CI enforce it, slice 3 built the panel, and slice 4 deleted the rows and the three
 * fields on this type that duplicated what the screens already said.
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
export type NavSoon = "week12";

/**
 * What a caller must hold to be shown something: **any-of within a group, all-of across groups.**
 *
 * A plain list would have been enough for the sections — `["territory:read", "orgunit:read"]` means
 * either, because a page can hold sections with different permissions. Reading the four action rows
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
      readonly soon?: never;
      /**
       * In the order the panel lists them. **The first visible one is where the section lands**, and
       * their routes are collectively what the section owns.
       *
       * Three fields went when this became the only one (W12½ slice 4). `href` said where the item
       * navigates, `section` said which prefix it highlights on, and `permissions` said who may see
       * it — all three now answered by the screens, and all three able to disagree with them.
       *
       * They were not redundant by accident. `Journeys` pointed at call frequency and
       * `Configuration` at the weights, each with a comment that **a nav item should go somewhere
       * real**, because neither section had an index worth landing on; `section` existed only
       * because that made the obvious highlight test wrong. Both were the missing second level,
       * worked around. With the level built, the workaround has nothing left to do.
       */
      readonly screens: readonly NavScreen[];
    }
  | {
      readonly key: NavKey;
      readonly soon: NavSoon;
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
 * Whether a path is inside a section — **now asked of its screens**, which is what it always meant.
 *
 * A scheduled section has none, so nothing can be inside it: the guard that used to be needed
 * against an undefined `href` falls out of the shape instead of being remembered.
 */
export function coversPath(item: NavItem, pathname: string): boolean {
  return (item.screens ?? []).some((screen) => covers(screen.href, pathname));
}

/**
 * Where the rail sends somebody who picks this section: **its first screen they may open.**
 *
 * First-visible rather than first, because the two differ and the difference is the point of the
 * permission model — somebody holding `channel:read` and not `outlet:read` lands on Channels, which
 * is the only Outlets screen they have. Undefined when they may open none, which is exactly when
 * {@link isSectionVisible} says not to draw the item at all.
 */
export function landingFor(
  item: NavItem,
  granted: (permission: string) => boolean,
): string | undefined {
  return visibleScreens(item, granted)[0]?.href;
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
        // Everything a reader can reach here refuses its own write controls, so there is nothing to
        // hide behind a second permission — the reasoning `JourneyActions` already carried.
        screens: [
          { key: "journeyPlans", href: "/journeys", requires: [["journey:read"]] },
          { key: "callFrequency", href: "/journeys/frequencies", requires: [["journey:read"]] },
          { key: "workingCalendars", href: "/journeys/calendars", requires: [["journey:read"]] },
        ],
      },
      { key: "visits", soon: "week12" },
      { key: "orders", soon: "week12" },
    ],
  },
  {
    key: "masterData",
    items: [
      {
        key: "outlets",
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
        screens: [
          { key: "scoreWeights", href: "/configuration/score-weights", requires: [["config:read"]] },
          { key: "surveys", href: "/configuration/surveys", requires: [["config:read"]] },
        ],
      },
      {
        key: "users",
        screens: [{ key: "userList", href: "/users", requires: [["user:read", "role:read"]] }],
      },
    ],
  },
];

/** Where signing in lands. Outlets, because it is the first screen that exists (UX build scope). */
export const LANDING = "/outlets";
