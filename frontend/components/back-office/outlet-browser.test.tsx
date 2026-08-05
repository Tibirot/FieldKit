// @vitest-environment jsdom

import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";

import type { AuthContextValue } from "@/components/auth-provider";
import { OutletBrowser } from "@/components/back-office/outlet-browser";
import type { Outlet, PagedList } from "@/lib/api/outlets";
import { render } from "@/test/render";

const auth = vi.hoisted(() => ({ current: {} as AuthContextValue }));
const fetchOutlets = vi.hoisted(() => vi.fn());
const fetchChannels = vi.hoisted(() => vi.fn());
const nav = vi.hoisted(() => ({ push: vi.fn(), replace: vi.fn(), params: "" }));

vi.mock("@/components/auth-provider", () => ({ useAuth: () => auth.current }));

// The URL is the state, so the test drives it: `params` is what the component reads, and the two
// spies are what it writes. Asserting on those is asserting on the contract this screen actually
// has with the browser.
vi.mock("next/navigation", () => ({
  useSearchParams: () => new URLSearchParams(nav.params),
}));

vi.mock("@/i18n/navigation", () => ({
  Link: ({ href, children }: { href: string; children: React.ReactNode }) => (
    <a href={href}>{children}</a>
  ),
  usePathname: () => "/outlets",
  useRouter: () => ({ push: nav.push, replace: nav.replace }),
}));

vi.mock("@/lib/api/outlets", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/lib/api/outlets")>()),
  fetchOutlets: (...args: unknown[]) => fetchOutlets(...args),
}));

vi.mock("@/lib/api/channels", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/lib/api/channels")>()),
  fetchChannels: (...args: unknown[]) => fetchChannels(...args),
}));

const OUTLET: Outlet = {
  id: "019f-1",
  code: "OUT-2214",
  name: "Select Market Dorobanți",
  channelId: "019f-c",
  channelName: "Modern Trade",
  segment: "A",
  banner: null,
  status: "Active",
  territory: null,
};

const page = (over: Partial<PagedList<Outlet>> = {}): PagedList<Outlet> => ({
  items: [OUTLET],
  total: 1,
  page: 1,
  pageSize: 50,
  ...over,
});

/** The query the component last asked the API for. */
const asked = () => fetchOutlets.mock.calls.at(-1)?.[1] as Record<string, unknown> | undefined;

/** The URL the component last wrote, as parsed params. */
const wrote = (spy: typeof nav.push) =>
  new URLSearchParams((spy.mock.calls.at(-1)?.[0] as string).split("?")[1] ?? "");

describe("<OutletBrowser>", () => {
  beforeEach(() => {
    nav.push.mockReset();
    nav.replace.mockReset();
    nav.params = "";
    fetchOutlets.mockReset().mockResolvedValue(page());
    fetchChannels.mockReset().mockResolvedValue([{ id: "019f-c", name: "Modern Trade" }]);

    auth.current = {
      status: "authenticated",
      user: { access_token: "token", profile: { sub: "subject-a" } },
      workspace: "fieldkit-dev",
      signIn: vi.fn(),
      signOut: vi.fn(),
      completeSignIn: vi.fn(),
    } as unknown as AuthContextValue;
  });

  it("reads the query out of the URL", async () => {
    // The whole point of keeping this state in the address bar: a link someone was sent opens the
    // table they were looking at, not the default one.
    nav.params = "search=cluj&status=Closed&sort=Name&descending=true&page=3";

    render(<OutletBrowser />);

    await waitFor(() => expect(fetchOutlets).toHaveBeenCalled());

    expect(asked()).toMatchObject({
      search: "cluj",
      status: "Closed",
      sort: "Name",
      descending: true,
      page: 3,
    });
  });

  it("ignores values the URL has no business containing", async () => {
    // These are typed by whoever holds the address bar. An unknown sort would reach the API as a
    // value its enum does not have; a mangled URL should show the outlet base, not an error.
    nav.params = "sort=DROP+TABLE&status=Banana&page=-4";

    render(<OutletBrowser />);

    await waitFor(() => expect(fetchOutlets).toHaveBeenCalled());

    expect(asked()).toMatchObject({ sort: undefined, status: undefined, page: undefined });
  });

  it("puts a filter in the URL rather than in React state", async () => {
    render(<OutletBrowser />);
    await screen.findByRole("table");

    await userEvent.selectOptions(screen.getByLabelText("Status"), "Closed");

    expect(wrote(nav.push).get("status")).toBe("Closed");
  });

  it("replaces history while typing and pushes when clicking", async () => {
    // Typing is not navigation. At a 300ms debounce, pushing would leave a history entry per pause
    // and make Back walk backwards through half-typed words.
    nav.params = "search=corner";

    render(<OutletBrowser />);
    await screen.findByRole("table");

    await userEvent.type(screen.getByLabelText("Search outlets"), "s");

    await waitFor(() => expect(nav.replace).toHaveBeenCalled());
    expect(nav.push).not.toHaveBeenCalled();
  });

  it("returns to the first page whenever the question changes", async () => {
    // Staying on page 7 while narrowing to twelve results shows an empty table, which reads as the
    // filter being broken rather than as the page being past the end.
    nav.params = "page=7";

    render(<OutletBrowser />);
    await screen.findByRole("table");

    await userEvent.selectOptions(screen.getByLabelText("Status"), "Active");

    expect(wrote(nav.push).has("page")).toBe(false);
  });

  it("reverses the column it is already sorted by", async () => {
    // Default sort is Code, so clicking Code turns it around.
    render(<OutletBrowser />);
    await screen.findByRole("table");

    await userEvent.click(screen.getByRole("button", { name: /code/i }));

    expect(wrote(nav.push).get("descending")).toBe("true");
  });

  it("starts a different column ascending", async () => {
    // A first click must not produce a descending list nobody asked for, even when the column it
    // replaces was descending.
    nav.params = "sort=Code&descending=true";

    render(<OutletBrowser />);
    await screen.findByRole("table");

    await userEvent.click(screen.getByRole("button", { name: /outlet/i }));

    expect(wrote(nav.push).get("sort")).toBe("Name");
    expect(wrote(nav.push).has("descending")).toBe(false);
  });

  it("announces which column is sorted, not just which caret is lit", async () => {
    nav.params = "sort=Name&descending=true";

    render(<OutletBrowser />);
    await screen.findByRole("table");

    const sorted = screen.getByRole("columnheader", { name: /outlet/i });
    expect(sorted.getAttribute("aria-sort")).toBe("descending");

    expect(
      screen.getByRole("columnheader", { name: /^code/i }).hasAttribute("aria-sort"),
    ).toBe(false);
  });

  it("offers no way past the ends of the list", async () => {
    fetchOutlets.mockResolvedValue(page({ total: 4, pageSize: 50 }));

    render(<OutletBrowser />);

    const previous = await screen.findByRole("button", { name: "Previous page" });
    const next = await screen.findByRole("button", { name: "Next page" });

    // One page of four rows: both ends are here. Enabling Next against a full page rather than the
    // total would offer a page that is always empty.
    expect((previous as HTMLButtonElement).disabled).toBe(true);
    expect((next as HTMLButtonElement).disabled).toBe(true);
  });

  it("says the table is filtered rather than empty", async () => {
    // "No outlets yet" invites an import. It is the wrong thing to say to someone who has four
    // thousand and mistyped a search.
    nav.params = "search=nothing-matches-this";
    fetchOutlets.mockResolvedValue(page({ items: [], total: 0 }));

    render(<OutletBrowser />);

    expect(await screen.findByText(/no outlets match/i)).toBeTruthy();
    expect(screen.queryByText(/no outlets yet/i)).toBeNull();
  });

  it("offers no way to clear filters that are not applied", async () => {
    render(<OutletBrowser />);
    await screen.findByRole("table");

    expect(screen.queryByRole("button", { name: "Clear" })).toBeNull();
  });

  it("clears every filter at once, back to a bare URL", async () => {
    nav.params = "search=cluj&status=Closed";

    render(<OutletBrowser />);
    await screen.findByRole("table");

    await userEvent.click(screen.getByRole("button", { name: "Clear" }));

    // A bare path, not `?search=&status=` — the URL is the state, so an emptied filter has to
    // leave no trace in it.
    expect(nav.push.mock.calls.at(-1)?.[0]).toBe("/outlets");
  });
});
