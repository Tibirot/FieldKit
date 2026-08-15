// @vitest-environment jsdom

import { screen, within } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

import type { AuthContextValue } from "@/components/auth-provider";
import { Breadcrumb } from "@/components/back-office/breadcrumb";
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

let pathname = "/products/price-lists";

vi.mock("@/i18n/navigation", () => ({
  Link: ({ href, children, ...rest }: { href: string; children: React.ReactNode }) => (
    <a href={href} {...rest}>
      {children}
    </a>
  ),
  usePathname: () => pathname,
}));

function allow(...permissions: string[]) {
  vi.mocked(fetchIdentity).mockResolvedValue({
    subject: "subject-a",
    tenant: "fieldkit-dev",
    permissions,
  });
}

/** The trail as a reader sees it, separators and all, once the permission answer has landed. */
async function trail() {
  const nav = await screen.findByRole("navigation", { name: "Breadcrumb" });

  return [...nav.querySelectorAll("li")].map((crumb) =>
    crumb.textContent?.replace(/^\//, "").trim(),
  );
}

/**
 * Only the segments you can actually walk to, with where they go.
 *
 * **Awaited, and the reason is a real property rather than test plumbing.** The trail knows *where
 * you are* from the path alone, so it paints on the first render — but where the **section** link
 * goes depends on which of its screens this caller may open, and that answer arrives from the API.
 * Pending counts as denied, so for one render the section is text and then becomes a link: the
 * navigation grows into place rather than offering a door and withdrawing it. A synchronous read
 * here sees the first render and reports no links at all, which is how this was written the first
 * time.
 */
async function links(expected: number) {
  const nav = await screen.findByRole("navigation", { name: "Breadcrumb" });

  await eventually(() => expect(nav.querySelectorAll("a")).toHaveLength(expected));

  return [...nav.querySelectorAll("a")].map((link) => [
    link.textContent?.trim(),
    link.getAttribute("href"),
  ]);
}

beforeEach(() => {
  pathname = "/products/price-lists";
  allow("product:read", "channel:read", "outlet:read", "config:read", "journey:read");
});

describe("<Breadcrumb>", () => {
  it("says where you are, in the navigation's own words", async () => {
    render(<Breadcrumb />);

    expect(await trail()).toEqual(["Master data", "Products & pricing", "Price lists"]);
  });

  it("agrees with the rail rather than repeating a second vocabulary", async () => {
    /*
     * The reason this is derived. The hand-written crumb this replaces said
     * `Master data / Products / Price lists` — while the rail said **Products & pricing** — and
     * further afield it called the journeys block *Field ops*, a group the navigation has never
     * had. Two copies of one hierarchy, drifted, with nothing able to notice.
     *
     * Asserted against the catalog the rail reads, so the two cannot disagree again without this
     * failing.
     */
    const messages = (await import("@/messages/en.json")).default;

    render(<Breadcrumb />);

    expect(await trail()).toContain(messages.Nav.items.products);
    expect(await trail()).toContain(messages.Nav.screens.priceLists);
  });

  it("can be walked back up", async () => {
    // The whole point of the slice: it was a <p> with no links in it, so the trail was printed and
    // could not be followed.
    render(<Breadcrumb />);

    expect(await links(1)).toEqual([["Products & pricing", "/products"]]);
  });

  it("does not link the segment you are standing on", async () => {
    render(<Breadcrumb />);

    const nav = await screen.findByRole("navigation", { name: "Breadcrumb" });
    const current = within(nav).getByText("Price lists");

    expect(current.tagName).toBe("SPAN");
    expect(current.getAttribute("aria-current")).toBe("page");
  });

  it("keeps the screen walkable once something is below it", async () => {
    // Four segments deep, the screen stops being where you are and becomes a way back — which is
    // exactly the case these eleven routes had no answer for.
    pathname = "/products/price-lists/019ff1e1/scope";
    render(<Breadcrumb leaf="Scope" />);

    expect(await trail()).toEqual([
      "Master data",
      "Products & pricing",
      "Price lists",
      "Scope",
    ]);

    expect(await links(2)).toEqual([
      ["Products & pricing", "/products"],
      ["Price lists", "/products/price-lists"],
    ]);
  });

  it("takes two leaves for a record and what of it you are looking at", async () => {
    pathname = "/outlets/019ff1e1/assortment";
    render(<Breadcrumb leaf={["PROF-41", "Assortment"]} />);

    expect(await trail()).toEqual([
      "Master data",
      "Outlets",
      "All outlets",
      "PROF-41",
      "Assortment",
    ]);
  });

  it("does not name a single-screen section twice", async () => {
    /*
     * `Territories / Territories` is the redundancy the panel refuses by not rendering at all. The
     * section is the half kept, because that is what the rail calls it — and the trail agreeing with
     * the rail is the property being bought here.
     */
    pathname = "/territories";
    allow("territory:read");
    render(<Breadcrumb />);

    expect(await trail()).toEqual(["Master data", "Territories"]);
  });

  it("sends the section link where this caller can actually go", async () => {
    // `landingFor` again: somebody holding `channel:read` alone has no outlet list, so the section
    // crumb must not offer them one.
    pathname = "/outlets/channels";
    allow("channel:read", "config:read");
    render(<Breadcrumb />);

    expect(await links(1)).toEqual([["Outlets", "/outlets/channels"]]);
  });

  it("leaves out a group the section does not belong to", async () => {
    // The journeys block is ungrouped, and the crumb it replaces invented "Field ops" for it.
    pathname = "/journeys/calendars";
    allow("journey:read");
    render(<Breadcrumb />);

    expect(await trail()).toEqual(["Journeys", "Working calendar"]);
  });

  it("says nothing outside the back office", async () => {
    // A trail that guesses is worse than none, and the caller cannot be lost somewhere the
    // navigation does not reach.
    pathname = "/field/outlets/019ff1e1";
    render(<Breadcrumb />);

    await eventually(() => expect(screen.queryByRole("navigation")).toBeNull());
  });

  it("announces itself as a breadcrumb, not as a third navigation", async () => {
    // Three <nav> landmarks share a back-office screen now — rail, panel, trail. A screen reader
    // listing them should be able to tell which is which without entering any.
    render(<Breadcrumb />);

    expect(await screen.findByRole("navigation", { name: "Breadcrumb" })).toBeTruthy();
  });
});
