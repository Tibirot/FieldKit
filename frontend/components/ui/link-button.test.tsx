// @vitest-environment jsdom

import { screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import { Button } from "@/components/ui/button";
import { LinkButton } from "@/components/ui/link-button";
import { render } from "@/test/render";

// next-intl's Link reaches for Next's router, which does not resolve outside a Next build. Every
// prop is forwarded, so anything `LinkButton` adds — a `role`, a `tabindex` — shows up on the anchor
// and this file can say whether it did.
vi.mock("@/i18n/navigation", () => ({
  Link: ({
    href,
    children,
    ...rest
  }: { href: string; children: React.ReactNode } & React.AnchorHTMLAttributes<HTMLAnchorElement>) => (
    <a href={href} {...rest}>
      {children}
    </a>
  ),
}));

describe("<LinkButton>", () => {
  it("announces itself as a link, not as a button", async () => {
    // The regression. `<Button nativeButton={false} render={<Link/>}>` produced an anchor carrying
    // `role="button"`, because Base UI's `useButton` applies it unconditionally when `native` is
    // false and offers no prop to suppress it. Assistive technology then said "this does something
    // here" about a control that navigates.
    render(<LinkButton href="/products/promotions">Promotions</LinkButton>);

    const link = await screen.findByRole("link", { name: "Promotions" });

    expect(link.getAttribute("href")).toBe("/products/promotions");
    expect(link.hasAttribute("role")).toBe(false);
  });

  it("adds no tabindex, because an anchor with an href is already focusable", async () => {
    // The old shape set `tabindex="0"` explicitly. Harmless here, but it is the kind of attribute
    // that stops being harmless the moment someone renders one without an href.
    render(<LinkButton href="/">Home</LinkButton>);

    expect((await screen.findByRole("link")).hasAttribute("tabindex")).toBe(false);
  });

  it("looks exactly like the button it borrows from", async () => {
    // Compared against a rendered `Button` rather than against `buttonVariants(...)` directly.
    // `cn` runs tailwind-merge, which drops the base `rounded-lg`/`text-sm` that the `sm` size
    // supersedes — so the raw cva output is *not* what either component ends up with, and asserting
    // against it would be asserting against a string neither one renders.
    //
    // This is what makes the fix a swap rather than a redesign: same class list, different element.
    render(
      <>
        <LinkButton href="/" size="sm" variant="outline">
          Back
        </LinkButton>
        <Button size="sm" variant="outline">
          Back
        </Button>
      </>,
    );

    const link = await screen.findByRole("link");
    const button = await screen.findByRole("button");

    expect(link.className).toBe(button.className);
  });

  it("keeps a caller's own className alongside the variants", async () => {
    render(
      <LinkButton href="/" className="ml-auto">
        Back
      </LinkButton>,
    );

    expect((await screen.findByRole("link")).className).toContain("ml-auto");
  });
});
