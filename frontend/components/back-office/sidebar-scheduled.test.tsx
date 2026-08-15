// @vitest-environment jsdom

import { screen, within } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import type { AuthContextValue } from "@/components/auth-provider";
import type { NavGroup } from "@/components/back-office/navigation";
import { Sidebar } from "@/components/back-office/sidebar";
import { fetchIdentity } from "@/lib/api/identity";
import messages from "@/messages/en.json";
import { render } from "@/test/render";

/**
 * The rail's **disabled-item** behaviour, against a nav that still has one.
 *
 * **This file exists because W12 slice 6a built the last unbuilt screen.** Every item in the real
 * `NAVIGATION` now has a route, so the four tests that lived in `sidebar.test.tsx` and looked for a
 * scheduled item found none and failed together. Their own comments had predicted it — the badge
 * test was made to derive its subject rather than name one, and noted it "survives every screen
 * landing except the last".
 *
 * Deleting them was the wrong answer: the design is a promise about **future** weeks, not a fact
 * about this one, and W13 will schedule something again. So the nav is mocked with a fixture that
 * has one built section and one scheduled, and the assertions are exactly the ones that used to run
 * against the product's own data.
 *
 * The rest of `sidebar.test.tsx` still runs against the real `NAVIGATION`, where it belongs.
 */
/**
 * Built inside `vi.hoisted`, because `vi.mock`'s factory is lifted above every `const` in the file
 * and a plain top-level binding is not initialised when it runs.
 */
const nav = vi.hoisted(() => ({
  NAVIGATION: [
    {
      key: null,
      items: [
        {
          key: "dashboard",
          screens: [{ key: "overview", href: "/dashboard", requires: [["visit:read"]] }],
        },
        { key: "orders", soon: "week12" },
      ],
    },
    {
      key: "admin",
      items: [{ key: "users", screens: [{ key: "userList", href: "/users", requires: [["user:read"]] }] }],
    },
  ] as readonly NavGroup[],
}));

vi.mock("@/components/back-office/navigation", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/components/back-office/navigation")>()),
  NAVIGATION: nav.NAVIGATION,
}));

vi.mock("@/components/auth-provider", () => ({
  useAuth: () =>
    ({
      status: "authenticated",
      user: { access_token: "token", profile: { sub: "subject-a" } },
    }) as unknown as AuthContextValue,
}));

vi.mock("@/i18n/navigation", () => ({
  Link: ({ href, children, ...rest }: { href: string; children: React.ReactNode }) => (
    <a href={href} {...rest}>
      {children}
    </a>
  ),
  usePathname: () => "/dashboard",
}));

/** Signs the caller in with exactly these permissions and nothing else. */
function allow(...permissions: string[]) {
  vi.mocked(fetchIdentity).mockResolvedValue({
    subject: "subject-a",
    tenant: "fieldkit-dev",
    permissions,
  });
}

const SCHEDULED = messages.Nav.items.orders;
const WEEK = messages.Nav.soon.week12;

describe("<Sidebar> with a scheduled item", () => {
  it("does not offer a link to a screen that does not exist", () => {
    // The load-bearing assertion of the whole disabled-nav design. An `<a>` with no href, or a
    // button that does nothing, would look identical in a screenshot and be reachable by keyboard —
    // a person tabbing through the rail would land on it and press Enter for nothing.
    allow("visit:read", "user:read");

    render(<Sidebar workspace="fieldkit-dev" />);

    expect(screen.queryByRole("link", { name: SCHEDULED })).toBeNull();

    const item = screen.getByText(SCHEDULED).closest("[aria-disabled]");

    expect(item).not.toBeNull();
    expect(item!.hasAttribute("href")).toBe(false);
    expect((item as HTMLElement).tabIndex).toBeLessThan(0);
  });

  it("says when an unbuilt screen arrives, in text rather than only a tooltip", () => {
    // A `title` alone is invisible to a keyboard and to a screen reader. Somebody who cannot see the
    // hover state should still learn the screen is coming rather than broken.
    allow("visit:read");

    render(<Sidebar workspace="fieldkit-dev" />);

    const item = screen.getByText(SCHEDULED).closest("[aria-disabled]")!;

    expect(within(item as HTMLElement).getByText(WEEK)).toBeTruthy();
  });

  it("shows a scheduled screen to everyone, whatever they may read", () => {
    // The badge is about the product's shape, so it does not depend on the caller at all — unlike a
    // built item, which is hidden from somebody who may not read it.
    allow();

    render(<Sidebar workspace="fieldkit-dev" />);

    const item = screen.getByText(SCHEDULED).closest("[aria-disabled]")!;

    expect(within(item as HTMLElement).getByText(WEEK)).toBeTruthy();
  });

  it("keeps the ungrouped scheduled items when a caller may read nothing at all", async () => {
    /*
     * Every built item is hidden, so the group divider goes — but the block at the top has no group
     * and still holds a scheduled item, which is a fact about the product rather than about the
     * caller. A fix that dropped whole groups too eagerly would empty the rail entirely.
     */
    allow();

    render(<Sidebar workspace="fieldkit-dev" />);

    const item = (await screen.findByText(SCHEDULED)).closest("[aria-disabled]")!;

    expect(within(item as HTMLElement).getByText(WEEK)).toBeTruthy();
    expect(screen.queryByRole("separator")).toBeNull();
  });
});
