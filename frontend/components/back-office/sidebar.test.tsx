// @vitest-environment jsdom

import { screen, within } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import type { AuthContextValue } from "@/components/auth-provider";
import { NAVIGATION } from "@/components/back-office/navigation";
import { Sidebar } from "@/components/back-office/sidebar";
import { fetchIdentity } from "@/lib/api/identity";
import { render } from "@/test/render";

// The nav asks the API what this caller may do, which needs somebody signed in to ask about.
vi.mock("@/components/auth-provider", () => ({
  useAuth: () =>
    ({
      status: "authenticated",
      user: { access_token: "token", profile: { sub: "subject-a" } },
    }) as unknown as AuthContextValue,
}));

// next-intl's navigation needs Next's router context, which a bare jsdom render does not have.
// Stubbed to plain DOM equivalents so the assertions below are about the sidebar's own decisions —
// which element type, which attributes — rather than about Next's routing.
vi.mock("@/i18n/navigation", () => ({
  Link: ({ href, children, ...rest }: { href: string; children: React.ReactNode }) => (
    <a href={href} {...rest}>
      {children}
    </a>
  ),
  usePathname: () => "/outlets",
}));

/** Signs the caller in with exactly these permissions and nothing else. */
function allow(...permissions: string[]) {
  vi.mocked(fetchIdentity).mockResolvedValue({
    subject: "subject-a",
    tenant: "fieldkit-dev",
    permissions,
  });
}

describe("<Sidebar>", () => {
  it("links to what is built", async () => {
    render(<Sidebar workspace="fieldkit-dev" />);

    // Awaited, because a built item waits for the API's answer about who is asking. Pending counts
    // as denied, so the nav grows into place rather than showing links and taking them away.
    const outlets = await screen.findByRole("link", { name: /outlets/i });

    // Plain DOM assertions rather than jest-dom matchers: one fewer dependency, and nothing here
    // is expressive enough to need them.
    expect(outlets.getAttribute("href")).toBe("/outlets");
    expect(outlets.getAttribute("aria-current")).toBe("page");
  });

  it("hides a screen this caller may not read", async () => {
    // A fact about the caller, not about the product — constant for their session, and no click will
    // change it. So it is hidden rather than disabled: "arrives in W7" is worth showing everyone,
    // "you may not see this" is a dead control that explains nothing.
    allow("outlet:read");

    render(<Sidebar workspace="fieldkit-dev" />);

    await screen.findByRole("link", { name: /outlets/i });

    expect(screen.queryByRole("link", { name: /territories/i })).toBeNull();
    expect(screen.queryByRole("link", { name: /users/i })).toBeNull();
  });

  it("drops a group's divider once nothing in it is visible", async () => {
    /*
     * Found in the browser signed in as `rep`, who holds no user or role permissions: "Users &
     * roles" was correctly hidden and the "ADMIN" heading stayed, labelling an empty stretch of
     * sidebar. A heading over nothing says a section exists and then refuses to say what is in it,
     * which is the same dead label the items are filtered to avoid.
     *
     * **The heading is a rule on the rail** (slice 4) — "MASTER DATA" does not fit in 68px, and
     * abbreviating it would invent a label nobody chose. The grouping is still worth showing, so it
     * is shown rather than said, and named for screen readers who are not short of room. The
     * property is unchanged: no group, no divider.
     */
    allow("outlet:read");

    render(<Sidebar workspace="fieldkit-dev" />);

    await screen.findByRole("link", { name: /outlets/i });

    expect(screen.queryByRole("separator", { name: "Admin" })).toBeNull();

    // The group that still has something in it keeps its divider, so this cannot pass by dropping
    // every divider.
    expect(screen.getByRole("separator", { name: "Master data" })).toBeTruthy();
  });

  it("keeps the ungrouped scheduled items when a caller may read nothing at all", async () => {
    // Every built item is hidden, so both dividers go — but the block at the top has no group and
    // holds only scheduled screens, which are a fact about the product rather than about the caller.
    // A fix that dropped whole groups too eagerly would empty the rail entirely.
    allow();

    render(<Sidebar workspace="fieldkit-dev" />);

    /*
     * Found by the item rather than by its badge. Three sections are scheduled for W12 now — the
     * dashboard, and the two whose W9 and W11 badges had expired — so `getByTitle("W12")` matches
     * several and throws. The badge is not a unique handle and never was; it only looked like one
     * while exactly one item wore it.
     */
    const dashboard = (await screen.findByText("Dashboard")).closest("[aria-disabled]")!;

    expect(within(dashboard as HTMLElement).getByText("W12")).toBeTruthy();
    expect(screen.queryByRole("separator")).toBeNull();
  });

  it("shows a page holding a section this caller may read", async () => {
    // Users & roles holds two sections behind different permissions. Someone who may read roles has
    // a reason to open it, and the section they may not read refuses itself in the API's own words —
    // which is what the demo walk actually saw in tenant B.
    allow("role:read");

    render(<Sidebar workspace="fieldkit-dev" />);

    expect(await screen.findByRole("link", { name: /users/i })).toBeTruthy();
  });

  it("shows a scheduled screen to everyone, whatever they may read", async () => {
    // The disabled-with-a-week-badge item is about the product's shape, so it does not depend on the
    // caller at all.
    allow();

    // Whichever item is still scheduled, rather than a hard-coded one. This test named Journeys and
    // W7 until W7 built it — so shipping the screen the badge was advertising broke the test that
    // proved badges work. Derived, it survives every screen landing except the last.
    const scheduled = NAVIGATION.flatMap((group) => group.items).find((item) => item.soon);

    expect(scheduled).toBeDefined();

    render(<Sidebar workspace="fieldkit-dev" />);

    const messages = (await import("@/messages/en.json")).default;

    // Scoped to the item, not looked up by its badge — several sections can share a week, and the
    // badge stopped being a unique handle the moment two did.
    const item = screen.getByText(messages.Nav.items[scheduled!.key]).closest("[aria-disabled]")!;

    expect(within(item as HTMLElement).getByText(messages.Nav.soon[scheduled!.soon!])).toBeTruthy();
  });

  it("does not offer a link to a screen that does not exist", () => {
    // The load-bearing assertion of the whole disabled-nav design. A `<a>` with no href, or a
    // button that does nothing, would look identical in a screenshot and be reachable by keyboard —
    // a person tabbing through the sidebar would land on Dashboard and press Enter for nothing.
    render(<Sidebar workspace="fieldkit-dev" />);

    expect(screen.queryByRole("link", { name: /dashboard/i })).toBeNull();

    const dashboard = screen.getByText("Dashboard").closest("[aria-disabled]");

    expect(dashboard).not.toBeNull();
    expect(dashboard!.hasAttribute("href")).toBe(false);
    expect((dashboard as HTMLElement).tabIndex).toBeLessThan(0);
  });

  it("says when an unbuilt screen arrives, in text rather than only a tooltip", () => {
    // A `title` alone is invisible to a keyboard and to a screen reader. Someone who cannot see the
    // hover state should still learn that Dashboard is coming rather than broken.
    render(<Sidebar workspace="fieldkit-dev" />);

    const dashboard = screen.getByText("Dashboard").closest("[aria-disabled]")!;

    expect(within(dashboard as HTMLElement).getByText("W12")).toBeTruthy();
  });

  it("names the workspace, and says so plainly when there is not one", () => {
    /*
     * The rail has no room for a tenant name beside the mark, so it moved into the mark's tooltip
     * and its screen-reader text (slice 4). Dropping it outright would have taken away the one thing
     * on screen that says which tenant you are looking at — a real hazard in a product whose entire
     * isolation story is per-tenant, and the reason this asserts on **both** carriers rather than
     * only the visible one: a `title` alone is invisible to a screen reader, which is the mistake
     * the disabled-item design already refuses one component over.
     */
    const { rerender } = render(<Sidebar workspace="fieldkit-dev" />);

    expect(screen.getByTitle("fieldkit-dev")).toBeTruthy();
    expect(screen.getByText(/fieldkit-dev/)).toBeTruthy();

    rerender(<Sidebar workspace={null} />);

    expect(screen.getByTitle("no workspace")).toBeTruthy();
    expect(screen.getByText(/no workspace/)).toBeTruthy();
  });

  it("is announced as navigation", () => {
    render(<Sidebar workspace="fieldkit-dev" />);

    expect(screen.getByRole("navigation", { name: "Back office" })).toBeTruthy();
  });
});
