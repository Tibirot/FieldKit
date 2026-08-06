// @vitest-environment jsdom

import { screen, within } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import type { AuthContextValue } from "@/components/auth-provider";
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

    render(<Sidebar workspace="fieldkit-dev" />);

    expect(screen.getByTitle("W7").textContent).toContain("Journeys");
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
    const { rerender } = render(<Sidebar workspace="fieldkit-dev" />);
    expect(screen.getByText("fieldkit-dev")).toBeTruthy();

    rerender(<Sidebar workspace={null} />);
    expect(screen.getByText("no workspace")).toBeTruthy();
  });

  it("is announced as navigation", () => {
    render(<Sidebar workspace="fieldkit-dev" />);

    expect(screen.getByRole("navigation", { name: "Back office" })).toBeTruthy();
  });
});
