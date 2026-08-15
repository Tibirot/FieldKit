// @vitest-environment jsdom

import { screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";

import type { AuthContextValue } from "@/components/auth-provider";
import { BackOfficeShell } from "@/components/back-office/shell";
import { render } from "@/test/render";

/**
 * The navigation drawer the shell grows below `md` (W12½ slice 6).
 *
 * **Its own file, beside `shell.test.tsx` rather than inside it.** That one is about the session
 * lifecycle — restoring, anonymous, expired, signing back in — and drives the real
 * {@link SessionGuard} through four statuses. This is about a button and an overlay, and needs
 * exactly one of those statuses. Two subjects that happen to share a component.
 */

let pathname = "/products/price-lists";

vi.mock("@/i18n/navigation", () => ({
  Link: ({ href, children, ...rest }: { href: string; children: React.ReactNode }) => (
    <a href={href} {...rest}>
      {children}
    </a>
  ),
  usePathname: () => pathname,
  useRouter: () => ({ replace: vi.fn() }),
}));

const auth = vi.hoisted(() => ({ current: {} as AuthContextValue }));

vi.mock("@/components/auth-provider", () => ({ useAuth: () => auth.current }));

const menu = () => screen.getByRole("button", { name: /menu|close/i });

/** The region the button owns, which is where the rail and the panel live. */
const drawer = () => document.getElementById("back-office-navigation")!;

/** Everything the drawer covers when it is open. */
const content = () => document.querySelector("main")!.parentElement!;

beforeEach(() => {
  pathname = "/products/price-lists";

  auth.current = {
    status: "authenticated",
    user: null,
    workspace: "fieldkit-dev",
    signIn: vi.fn(),
    signOut: vi.fn(),
    completeSignIn: vi.fn(),
    expire: vi.fn(),
    reauthenticate: vi.fn(),
  };
});

describe("the back office on a phone", () => {
  it("keeps the navigation shut until it is asked for", () => {
    render(<BackOfficeShell>screen</BackOfficeShell>);

    expect(menu().getAttribute("aria-expanded")).toBe("false");
    expect(drawer().className).toContain("hidden");
  });

  it("names a region that exists", () => {
    // `aria-controls` pointing at nothing is the kind of attribute that looks like accessibility and
    // is not — the id has to be there, and only a test can say whether it still is.
    render(<BackOfficeShell>screen</BackOfficeShell>);

    expect(menu().getAttribute("aria-controls")).toBe("back-office-navigation");
    expect(document.getElementById("back-office-navigation")).not.toBeNull();
  });

  it("opens onto the rail and the panel — the same two, not a third copy", async () => {
    /*
     * Why the drawer wraps the existing columns instead of rendering its own list: two `Back office`
     * landmarks in one document is a screen reader offering the navigation twice, with no way to
     * tell which of them is the one behind `display: none`.
     */
    render(<BackOfficeShell>screen</BackOfficeShell>);
    await userEvent.click(menu());

    expect(menu().getAttribute("aria-expanded")).toBe("true");
    expect(screen.getAllByRole("navigation", { name: "Back office" })).toHaveLength(1);
    expect(drawer().className).not.toContain("hidden");
  });

  it("takes the page out of the tab order while it is over it", async () => {
    /*
     * The half a backdrop does not do. A full-screen overlay hides the page from the eye and not
     * from the tab key, so without `inert` a keyboard walks off the last screen in the panel into
     * the sign-out button and the table behind it, both invisible.
     */
    render(<BackOfficeShell>screen</BackOfficeShell>);

    expect(content().hasAttribute("inert")).toBe(false);

    await userEvent.click(menu());

    expect(content().hasAttribute("inert")).toBe(true);
  });

  it("closes on Escape, and puts the caret back on the button", async () => {
    // A full-screen overlay with no way out is a trap for anyone who opened it by accident, and the
    // close control is at the top of a list that scrolls.
    render(<BackOfficeShell>screen</BackOfficeShell>);
    await userEvent.click(menu());

    await userEvent.keyboard("{Escape}");

    expect(menu().getAttribute("aria-expanded")).toBe("false");
    expect(document.activeElement).toBe(menu());
  });

  it("closes when you arrive somewhere", async () => {
    /*
     * Adjusted during render rather than in an effect: `useEffect(() => setOpen(false), [pathname])`
     * is a lint error under React 19's `set-state-in-effect` **and** renders the drawer over the
     * screen it just reached before removing it.
     */
    const { rerender } = render(<BackOfficeShell>screen</BackOfficeShell>);
    await userEvent.click(menu());

    expect(menu().getAttribute("aria-expanded")).toBe("true");

    pathname = "/products/promotions";
    rerender(<BackOfficeShell>screen</BackOfficeShell>);

    expect(menu().getAttribute("aria-expanded")).toBe("false");
    expect(content().hasAttribute("inert")).toBe(false);
  });

  it("does not re-open itself when you come back", async () => {
    /*
     * **This test found a real bug in the first version of the fix.** That one stored the path the
     * drawer was opened at and derived `open` from `openedAt === pathname` — shorter, and wrong:
     * navigate away and back, the two are equal again, and the drawer re-opens on a screen nobody
     * opened it on. What matters is the transition, not the destination.
     */
    const { rerender } = render(<BackOfficeShell>screen</BackOfficeShell>);
    await userEvent.click(menu());

    pathname = "/products/promotions";
    rerender(<BackOfficeShell>screen</BackOfficeShell>);

    pathname = "/products/price-lists";
    rerender(<BackOfficeShell>screen</BackOfficeShell>);

    expect(menu().getAttribute("aria-expanded")).toBe("false");
  });
});
