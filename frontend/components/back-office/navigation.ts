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

/** Which week ships an unbuilt screen. Keys into `Nav.soon`, so the badge translates. */
export type NavSoon = "week5" | "week6" | "week7" | "week9" | "week11" | "week12";

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
       */
      readonly section?: string;
      /**
       * Any one of these is enough to show the item.
       *
       * Any-of rather than all-of because a page can hold sections with different permissions:
       * someone who may read roles but not users still has a reason to open Users & roles, and the
       * section they cannot read refuses itself with the API's own words.
       */
      readonly permissions: readonly string[];
    }
  | {
      readonly key: NavKey;
      readonly href?: never;
      readonly section?: never;
      readonly soon: NavSoon;
      readonly permissions?: never;
    };

/**
 * Whether a path is inside an item's section.
 *
 * Exported for its own tests and for the sidebar. The segment boundary is the whole of it: a bare
 * `startsWith` would light `Journeys` up on `/journeys-archive`, and equality alone would leave it
 * dark on every screen but one.
 */
export function coversPath(item: NavItem, pathname: string): boolean {
  if (item.href === undefined) return false;

  const section = item.section ?? item.href;

  return pathname === section || pathname.startsWith(`${section}/`);
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
      },
      { key: "visits", soon: "week9" },
      { key: "orders", soon: "week11" },
    ],
  },
  {
    key: "masterData",
    items: [
      { key: "outlets", href: "/outlets", permissions: ["outlet:read"] },
      { key: "products", href: "/products", permissions: ["product:read"] },
      { key: "territories", href: "/territories", permissions: ["territory:read", "orgunit:read"] },
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
      },
      { key: "users", href: "/users", permissions: ["user:read", "role:read"] },
    ],
  },
];

/** Where signing in lands. Outlets, because it is the first screen that exists (UX build scope). */
export const LANDING = "/outlets";
