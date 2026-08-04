// @vitest-environment jsdom

import { screen, within } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import { Sidebar } from "@/components/back-office/sidebar";
import { render } from "@/test/render";

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

describe("<Sidebar>", () => {
  it("links to what is built", () => {
    render(<Sidebar workspace="fieldkit-dev" />);

    const outlets = screen.getByRole("link", { name: /outlets/i });

    // Plain DOM assertions rather than jest-dom matchers: one fewer dependency, and nothing here
    // is expressive enough to need them.
    expect(outlets.getAttribute("href")).toBe("/outlets");
    expect(outlets.getAttribute("aria-current")).toBe("page");
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
