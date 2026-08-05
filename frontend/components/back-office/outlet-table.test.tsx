// @vitest-environment jsdom

import { screen, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

import type { AuthContextValue } from "@/components/auth-provider";
import { OutletTable } from "@/components/back-office/outlet-table";
import { ApiError } from "@/lib/api/client";
import type { Outlet, PagedList } from "@/lib/api/outlets";
import { render } from "@/test/render";

const auth = vi.hoisted(() => ({ current: {} as AuthContextValue }));
const fetchOutlets = vi.hoisted(() => vi.fn());

vi.mock("@/components/auth-provider", () => ({ useAuth: () => auth.current }));

// The module, not `fetch`. Stubbing global fetch would test our URL-building and JSON-parsing at the
// same time as the table's rendering, and leave the failure ambiguous when either breaks.
vi.mock("@/lib/api/outlets", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/lib/api/outlets")>()),
  fetchOutlets: (...args: unknown[]) => fetchOutlets(...args),
}));

/** One page holding whatever rows a test cares about — the envelope is not what is under test. */
const page = (items: Outlet[]): PagedList<Outlet> => ({
  items,
  total: items.length,
  page: 1,
  pageSize: 50,
});

const OUTLET: Outlet = {
  id: "019f-1",
  code: "OUT-2214",
  name: "Select Market Dorobanți",
  channelId: "019f-c",
  channelName: "Modern Trade",
  segment: "A",
  banner: null,
  status: "Active",
  territory: { id: "019f-t", name: "Bucharest N" },
};

describe("<OutletTable>", () => {
  beforeEach(() => {
    fetchOutlets.mockReset();
    auth.current = {
      status: "authenticated",
      user: { access_token: "token", profile: { sub: "subject-a" } },
      workspace: "fieldkit-dev",
      signIn: vi.fn(),
      signOut: vi.fn(),
      completeSignIn: vi.fn(),
    } as unknown as AuthContextValue;
  });

  it("shows the outlet base, with the territory that covers each shop", async () => {
    fetchOutlets.mockResolvedValue(page([OUTLET]));

    render(<OutletTable />);

    expect(await screen.findByText("Select Market Dorobanți")).toBeTruthy();
    expect(screen.getByText("OUT-2214")).toBeTruthy();
    expect(screen.getByText("Bucharest N")).toBeTruthy();
    expect(screen.getByText("Active")).toBeTruthy();

  });

  it("shows an outlet nobody covers as unassigned rather than blank", async () => {
    // Blank would read as missing data. It is an ordinary state — outlets exist before anyone
    // decides who covers them (BR-OUT-1).
    fetchOutlets.mockResolvedValue(page([{ ...OUTLET, territory: null }]));

    render(<OutletTable />);

    expect(await screen.findByText("Unassigned")).toBeTruthy();
  });

  it("distinguishes not being allowed from being broken", async () => {
    // A 403 is an answer about this person, and telling them the outlet base "could not be loaded"
    // sends them to retry, then to report a bug, for a system behaving exactly as configured.
    fetchOutlets.mockRejectedValue(new ApiError(403));

    render(<OutletTable />);

    const alert = await screen.findByRole("alert");
    expect(alert.textContent).toContain("permission");
  });

  it("says so plainly when the call fails for any other reason", async () => {
    fetchOutlets.mockRejectedValue(new ApiError(500));

    render(<OutletTable />);

    const alert = await screen.findByRole("alert");
    expect(alert.textContent).toContain("could not be loaded");
    expect(alert.textContent).not.toContain("permission");
  });

  it("invites the first import rather than showing an empty table", async () => {
    fetchOutlets.mockResolvedValue(page([]));

    render(<OutletTable />);

    expect(await screen.findByText(/no outlets yet/i)).toBeTruthy();
    expect(screen.queryByRole("table")).toBeNull();
  });

  it("does not call the API without a token", async () => {
    // A token can expire between renders. The query must simply not run rather than send
    // `Bearer undefined` and collect a 401 that looks like a session problem.
    auth.current = { ...auth.current, user: null } as AuthContextValue;

    render(<OutletTable />);

    await waitFor(() => expect(screen.getByRole("status")).toBeTruthy());
    expect(fetchOutlets).not.toHaveBeenCalled();
  });
});
