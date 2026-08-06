// @vitest-environment jsdom

import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";

import type { AuthContextValue } from "@/components/auth-provider";
import { TerritoryOutlets } from "@/components/back-office/territory-outlets";
import { ApiError } from "@/lib/api/client";
import type { Territory, TerritoryOutlet } from "@/lib/api/org";
import type { Outlet, PagedList } from "@/lib/api/outlets";
import { render } from "@/test/render";

const auth = vi.hoisted(() => ({ current: {} as AuthContextValue }));
const fetchTerritoryOutlets = vi.hoisted(() => vi.fn());
const assignOutlets = vi.hoisted(() => vi.fn());
const removeOutlet = vi.hoisted(() => vi.fn());
const fetchOutlets = vi.hoisted(() => vi.fn());

vi.mock("@/components/auth-provider", () => ({ useAuth: () => auth.current }));

vi.mock("@/lib/api/org", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/lib/api/org")>()),
  fetchTerritoryOutlets: (...args: unknown[]) => fetchTerritoryOutlets(...args),
  assignOutlets: (...args: unknown[]) => assignOutlets(...args),
  removeOutlet: (...args: unknown[]) => removeOutlet(...args),
}));

vi.mock("@/lib/api/outlets", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/lib/api/outlets")>()),
  fetchOutlets: (...args: unknown[]) => fetchOutlets(...args),
}));

const TERRITORY: Territory = {
  id: "t-1",
  name: "București Nord",
  orgUnitId: "u-1",
  outletCount: 2,
};

const MEMBERS: TerritoryOutlet[] = [
  { outletId: "o-1", code: "OUT-1", name: "Corner Shop", isOpen: true },
  { outletId: "o-2", code: "OUT-2", name: "Closed Shop", isOpen: false },
  { outletId: "o-gone", code: null, name: null, isOpen: null },
];

const outlet = (id: string, code: string, territory: { id: string; name: string } | null): Outlet => ({
  id,
  code,
  name: `Shop ${code}`,
  channelId: "c-1",
  channelName: "Modern Trade",
  segment: null,
  banner: null,
  status: "Active",
  territory,
});

const MATCHES: PagedList<Outlet> = {
  items: [
    outlet("o-1", "OUT-1", { id: "t-1", name: "București Nord" }),
    outlet("o-3", "OUT-3", null),
    outlet("o-4", "OUT-4", { id: "t-2", name: "Iași" }),
  ],
  total: 3,
  page: 1,
  pageSize: 10,
};

describe("<TerritoryOutlets>", () => {
  beforeEach(() => {
    fetchTerritoryOutlets.mockReset().mockResolvedValue(MEMBERS);
    fetchOutlets.mockReset().mockResolvedValue(MATCHES);
    assignOutlets.mockReset().mockResolvedValue(undefined);
    removeOutlet.mockReset().mockResolvedValue(undefined);

    auth.current = {
      status: "authenticated",
      user: { access_token: "token", profile: { sub: "subject-a" } },
      workspace: "fieldkit-dev",
      signIn: vi.fn(),
      signOut: vi.fn(),
      completeSignIn: vi.fn(),
    } as unknown as AuthContextValue;
  });

  it("keeps a row for an outlet that no longer resolves", async () => {
    // The membership is Organization's fact. Hiding the row would make the territory quietly smaller
    // than its own count says, and nobody would know which shop went missing.
    render(<TerritoryOutlets territory={TERRITORY} />);

    const items = await screen.findAllByRole("listitem");

    expect(items).toHaveLength(3);
    expect(items[2].textContent).toContain("Unknown outlet");
  });

  it("says which outlets are closed rather than dropping them", async () => {
    // A closed shop is still in the territory — BR-OUT-4 keeps its history, and a rep's scope has to
    // account for it.
    render(<TerritoryOutlets territory={TERRITORY} />);

    const items = await screen.findAllByRole("listitem");

    expect(items[1].textContent).toContain("Closed");
  });

  it("does not list the outlet base until something is searched for", async () => {
    // A tenant has thousands. A picker holding the whole base is a scroll, not a choice.
    render(<TerritoryOutlets territory={TERRITORY} />);
    await screen.findAllByRole("listitem");

    expect(fetchOutlets).not.toHaveBeenCalled();

    await userEvent.type(screen.getByLabelText("Add outlets"), "OUT");

    await waitFor(() => expect(fetchOutlets).toHaveBeenCalled());
  });

  it("adds every outlet that was ticked, in one request", async () => {
    // A list rather than one at a time, because that is the shape of the decision — and the endpoint
    // refuses the set rather than half-applying it.
    render(<TerritoryOutlets territory={TERRITORY} />);
    await screen.findAllByRole("listitem");

    await userEvent.type(screen.getByLabelText("Add outlets"), "OUT");
    await screen.findByRole("checkbox", { name: /OUT-3/ });

    await userEvent.click(screen.getByRole("checkbox", { name: /OUT-3/ }));
    await userEvent.click(screen.getByRole("checkbox", { name: /OUT-4/ }));
    await userEvent.click(screen.getByRole("button", { name: "Add 2 outlets" }));

    await waitFor(() => expect(assignOutlets).toHaveBeenCalled());

    expect(assignOutlets).toHaveBeenCalledWith("token", "t-1", ["o-3", "o-4"]);
  });

  it("shows an outlet already here, and will not offer to add it again", async () => {
    // Found by a search that expected it. Omitting it would read as the search being wrong.
    render(<TerritoryOutlets territory={TERRITORY} />);
    await screen.findAllByRole("listitem");

    await userEvent.type(screen.getByLabelText("Add outlets"), "OUT");

    const box = (await screen.findByRole("checkbox", { name: /OUT-1/ })) as HTMLInputElement;

    expect(box.disabled).toBe(true);
    expect(box.closest("label")?.textContent).toContain("already here");
  });

  it("says which territory an outlet is already in, before anything is attempted", async () => {
    // ORG-05: an outlet belongs to exactly one territory, and the server refuses a reassignment.
    // Knowing beforehand is the difference between a choice and a correction.
    render(<TerritoryOutlets territory={TERRITORY} />);
    await screen.findAllByRole("listitem");

    await userEvent.type(screen.getByLabelText("Add outlets"), "OUT");

    const box = await screen.findByRole("checkbox", { name: /OUT-4/ });

    expect(box.closest("label")?.textContent).toContain("in Iași");
  });

  it("shows what the server said when an outlet is already taken", async () => {
    // The refusal names the outlets by code, so the admin knows which to free up first.
    assignOutlets.mockRejectedValue(
      new ApiError(409, [
        {
          field: "outletIds",
          message: "Some outlets already belong to another territory. Remove them from it first: OUT-4.",
        },
      ]),
    );

    render(<TerritoryOutlets territory={TERRITORY} />);
    await screen.findAllByRole("listitem");

    await userEvent.type(screen.getByLabelText("Add outlets"), "OUT");
    await userEvent.click(await screen.findByRole("checkbox", { name: /OUT-4/ }));
    await userEvent.click(screen.getByRole("button", { name: "Add 1 outlet" }));

    expect((await screen.findByRole("alert")).textContent).toContain("OUT-4");
  });

  it("removes one membership by its outlet", async () => {
    render(<TerritoryOutlets territory={TERRITORY} />);
    await screen.findAllByRole("listitem");

    await userEvent.click(
      screen.getByRole("button", { name: "Remove OUT-1 from this territory" }),
    );

    await waitFor(() => expect(removeOutlet).toHaveBeenCalled());

    expect(removeOutlet).toHaveBeenCalledWith("token", "t-1", "o-1");
  });

  it("says so when a territory covers nothing", async () => {
    fetchTerritoryOutlets.mockResolvedValue([]);

    render(<TerritoryOutlets territory={TERRITORY} />);

    expect(await screen.findByText(/covers no outlets yet/)).toBeTruthy();
  });

  it("labels the removal of an unresolvable outlet by its id, not a blank", async () => {
    // The only thing left to name it by. A button reading "Remove from this territory" would be one
    // of three identical ones.
    render(<TerritoryOutlets territory={TERRITORY} />);

    const items = await screen.findAllByRole("listitem");

    expect(within(items[2]).getByRole("button").getAttribute("aria-label")).toContain("o-gone");
  });
});
