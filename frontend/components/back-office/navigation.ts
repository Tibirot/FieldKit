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
       * Any one of these is enough to show the item.
       *
       * Any-of rather than all-of because a page can hold sections with different permissions:
       * someone who may read roles but not users still has a reason to open Users & roles, and the
       * section they cannot read refuses itself with the API's own words.
       */
      readonly permissions: readonly string[];
    }
  | { readonly key: NavKey; readonly href?: never; readonly soon: NavSoon; readonly permissions?: never };

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
      { key: "journeys", soon: "week7" },
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
    items: [{ key: "users", href: "/users", permissions: ["user:read", "role:read"] }],
  },
];

/** Where signing in lands. Outlets, because it is the first screen that exists (UX build scope). */
export const LANDING = "/outlets";
