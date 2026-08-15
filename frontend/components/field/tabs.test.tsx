// @vitest-environment jsdom

import { screen } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

import { FieldTabs } from "@/components/field/tabs";
import { render } from "@/test/render";

let pathname = "/field";

vi.mock("@/i18n/navigation", () => ({
  Link: ({ href, children, ...rest }: { href: string; children: React.ReactNode }) => (
    <a href={href} {...rest}>
      {children}
    </a>
  ),
  usePathname: () => pathname,
}));

const lit = () => screen.queryByRole("link", { current: "page" })?.textContent?.trim();

beforeEach(() => {
  pathname = "/field";
});

describe("<FieldTabs>", () => {
  it("offers the three places a rep can be", () => {
    render(<FieldTabs />);

    expect(
      screen.getAllByRole("link").map((link) => [
        link.textContent?.trim(),
        link.getAttribute("href"),
      ]),
    ).toEqual([
      ["Today", "/field"],
      ["Outlets", "/field/outlets"],
      ["Device", "/field/device"],
    ]);
  });

  it("does not offer sync, which is a control rather than a place", () => {
    // The wireframe drew four tabs and the fourth was Sync. It is a status and a button — "has my
    // work gone in", and a way to make it go — so a tab for it would navigate nowhere. It stays in
    // the header, where it already was.
    render(<FieldTabs />);

    expect(screen.queryByRole("link", { name: /sync/i })).toBeNull();
  });

  it("lights Today only on the round itself", () => {
    /*
     * The rule that makes the bar usable, and the one it would be easy to get wrong: **every** field
     * route begins with `/field`, so a prefix match lights Today on the outlet list, the device
     * screen and every visit at once — three tabs lit, none of them meaning anything.
     */
    render(<FieldTabs />);

    expect(lit()).toBe("Today");
  });

  it.each([
    ["/field/outlets", "Outlets"],
    ["/field/outlets/019ff1e1", "Outlets"],
    ["/field/device", "Device"],
  ])("lights %s as %s", (path, expected) => {
    // The detail route matters: a rep who taps into a shop from the list should still be able to
    // see where they are. `aria-current` is the whole of what a screen reader is told about that.
    pathname = path;
    render(<FieldTabs />);

    expect(lit()).toBe(expected);
  });

  it("lights exactly one tab, wherever you stand", () => {
    for (const path of ["/field", "/field/outlets", "/field/outlets/x", "/field/device"]) {
      pathname = path;

      const { unmount } = render(<FieldTabs />);

      expect(screen.getAllByRole("link", { current: "page" }), path).toHaveLength(1);

      unmount();
    }
  });

  it("lights nothing on a screen no tab owns", () => {
    // A visit, an audit, an order. The bar is a way *out* of those rather than a claim about them,
    // and lighting the nearest tab would tell a rep they are somewhere they are not.
    pathname = "/field/visits/019ff1e1/audit";
    render(<FieldTabs />);

    expect(lit()).toBeUndefined();
  });

  it("does not light Outlets for a route that merely starts with its name", () => {
    // Segment boundary, the rule the back office settled. No such route exists, which is exactly
    // when it is cheap to get right.
    pathname = "/field/outlets-archive";
    render(<FieldTabs />);

    expect(lit()).toBeUndefined();
  });

  it("is announced as the field app's navigation", () => {
    render(<FieldTabs />);

    expect(screen.getByRole("navigation", { name: "Field app" })).toBeTruthy();
  });
});
