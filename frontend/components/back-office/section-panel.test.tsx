// @vitest-environment jsdom

import { screen } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

import type { AuthContextValue } from "@/components/auth-provider";
import { SectionPanel } from "@/components/back-office/section-panel";
import { fetchIdentity } from "@/lib/api/identity";
import { eventually } from "@/test/eventually";
import { render } from "@/test/render";

vi.mock("@/components/auth-provider", () => ({
  useAuth: () =>
    ({
      status: "authenticated",
      user: { access_token: "token", profile: { sub: "subject-a" } },
    }) as unknown as AuthContextValue,
}));

/**
 * Where the caller is standing, per test.
 *
 * The panel's whole job is deciding what to show *given a path*, so the path is the input under
 * test — a single fixed mock like the sidebar's would let one screen stand in for all seventeen.
 */
let pathname = "/products/price-lists";

vi.mock("@/i18n/navigation", () => ({
  Link: ({ href, children, ...rest }: { href: string; children: React.ReactNode }) => (
    <a href={href} {...rest}>
      {children}
    </a>
  ),
  usePathname: () => pathname,
}));

/** Signs the caller in with exactly these permissions and nothing else. */
function allow(...permissions: string[]) {
  vi.mocked(fetchIdentity).mockResolvedValue({
    subject: "subject-a",
    tenant: "fieldkit-dev",
    permissions,
  });
}

/**
 * The panel's links once it has settled, in render order, as a reader would see them.
 *
 * Awaited via `findByRole`, because a built item waits for the API's answer about who is asking —
 * the same reason the sidebar's tests await theirs.
 */
async function links() {
  const panel = await screen.findByRole("navigation");

  return [...panel.querySelectorAll("a")].map((link) => link.textContent?.trim());
}

beforeEach(() => {
  pathname = "/products/price-lists";
  allow("product:read", "channel:read", "outlet:read", "config:read", "journey:read");
});

describe("<SectionPanel>", () => {
  it("shows the screens of the section you are standing in", async () => {
    render(<SectionPanel />);

    expect(await links()).toEqual([
      "Catalogue",
      "Classification",
      "Assortments",
      "Price lists",
      "Promotions",
      "Order minimums",
    ]);
  });

  it("lights the screen you are on, and only that one", async () => {
    render(<SectionPanel />);

    const current = await screen.findByRole("link", { current: "page" });

    expect(current.textContent?.trim()).toBe("Price lists");
    expect(current.getAttribute("href")).toBe("/products/price-lists");
    expect(screen.getAllByRole("link", { current: "page" })).toHaveLength(1);
  });

  it("stays put and stays lit four segments into a record", async () => {
    /*
     * The reason `findScreen` exists. `/products/price-lists/{id}/scope` has no navigation item of
     * its own and must not get one — but the panel has to know it belongs to price lists, or it
     * empties out on exactly the screens where somebody most needs a way back.
     */
    pathname = "/products/price-lists/019ff1e1-cdfc-71e6/scope";
    render(<SectionPanel />);

    expect(await links()).toHaveLength(6);
    expect((await screen.findByRole("link", { current: "page" })).textContent?.trim()).toBe(
      "Price lists",
    );
  });

  it("does not light the section index just because it is a prefix", async () => {
    // Longest match, seen from the UI. `/products` covers every route in its own section, so a
    // first-match scan would highlight Catalogue on five screens out of six.
    render(<SectionPanel />);

    expect((await screen.findByRole("link", { current: "page" })).textContent?.trim()).not.toBe(
      "Catalogue",
    );
  });

  it("names itself after its section, so it is not just another navigation", async () => {
    // Two <nav> landmarks share this shell. A screen reader listing them should be able to tell
    // which is which without entering either.
    render(<SectionPanel />);

    expect(await screen.findByRole("navigation", { name: "Products & pricing" })).toBeTruthy();
  });

  it("hides a screen this caller may not read", async () => {
    // Assortments and order minimums are organised by channel, so both need `channel:read` on top
    // of `product:read` — a reader without it would get a selector with nothing in it.
    allow("product:read");
    render(<SectionPanel />);

    expect(await links()).toEqual(["Catalogue", "Classification", "Price lists", "Promotions"]);
  });

  it("says nothing where it would only repeat the sidebar", async () => {
    /*
     * Territories has one screen. A 192px column restating the nav item above it is the dead control
     * this codebase rejects everywhere else, so there is no panel at all — and the layout shifting
     * by a column is the accepted cost.
     */
    pathname = "/territories";
    allow("territory:read");
    render(<SectionPanel />);

    // `eventually` rather than a bare assertion: proving something stays absent means letting the
    // permission answer land first, or it passes on the pending render for the wrong reason.
    await eventually(() => expect(screen.queryByRole("navigation")).toBeNull());
  });

  it("says nothing when permissions leave one screen standing", async () => {
    // Same rule reached the other way: `channel:read` alone opens exactly one screen under Outlets,
    // and a panel appearing to say where you already are is worse than no panel.
    pathname = "/outlets/channels";
    allow("channel:read");
    render(<SectionPanel />);

    await eventually(() => expect(screen.queryByRole("navigation")).toBeNull());
  });

  it("says nothing outside the back office", async () => {
    // The field app and sign-in render their own shells. A panel here would be a second navigation
    // for a section the caller is not in.
    pathname = "/field/outlets/019ff1e1";
    render(<SectionPanel />);

    await eventually(() => expect(screen.queryByRole("navigation")).toBeNull());
  });

  it("offers nothing at all before the permission answer arrives", async () => {
    /*
     * Pending counts as denied, which `usePermissions` guarantees and this pins from the outside: a
     * panel that renders every screen and then removes four is worse than one that grows into
     * place, because the four are clickable in between.
     */
    vi.mocked(fetchIdentity).mockImplementation(() => new Promise(() => {}));
    render(<SectionPanel />);

    expect(screen.queryByRole("navigation")).toBeNull();
  });
});
