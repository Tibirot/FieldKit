// @vitest-environment jsdom

import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";

import type { AuthContextValue } from "@/components/auth-provider";
import { TerritoryBrowser } from "@/components/back-office/territory-browser";
import { ApiError } from "@/lib/api/client";
import { fetchIdentity } from "@/lib/api/identity";
import type { OrgUnit, Territory } from "@/lib/api/org";
import { render } from "@/test/render";

const auth = vi.hoisted(() => ({ current: {} as AuthContextValue }));
const push = vi.hoisted(() => vi.fn());
const search = vi.hoisted(() => ({ current: new URLSearchParams() }));
const fetchOrgUnits = vi.hoisted(() => vi.fn());
const fetchTerritories = vi.hoisted(() => vi.fn());
const createTerritory = vi.hoisted(() => vi.fn());
const updateTerritory = vi.hoisted(() => vi.fn());
const deleteTerritory = vi.hoisted(() => vi.fn());

vi.mock("@/components/auth-provider", () => ({ useAuth: () => auth.current }));

vi.mock("@/i18n/navigation", () => ({
  useRouter: () => ({ push }),
  usePathname: () => "/territories",
}));

vi.mock("next/navigation", () => ({ useSearchParams: () => search.current }));

vi.mock("@/lib/api/users", () => ({ usersKey: () => ["users"], fetchUsers: () => Promise.resolve([]) }));

vi.mock("@/lib/api/org", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/lib/api/org")>()),
  fetchAssignments: () => Promise.resolve([]),
  fetchOrgUnits: (...args: unknown[]) => fetchOrgUnits(...args),
  fetchTerritories: (...args: unknown[]) => fetchTerritories(...args),
  createTerritory: (...args: unknown[]) => createTerritory(...args),
  updateTerritory: (...args: unknown[]) => updateTerritory(...args),
  deleteTerritory: (...args: unknown[]) => deleteTerritory(...args),
}));

const UNITS: OrgUnit[] = [
  { id: "u-ro", name: "Romania", parentId: null },
  { id: "u-mun", name: "Muntenia", parentId: "u-ro" },
  { id: "u-mol", name: "Moldova", parentId: "u-ro" },
];

const TERRITORIES: Territory[] = [
  { id: "t-1", name: "București Nord", orgUnitId: "u-mun", outletCount: 42 },
  { id: "t-2", name: "Iași", orgUnitId: "u-mol", outletCount: 0 },
];

describe("<TerritoryBrowser>", () => {
  beforeEach(() => {
    push.mockReset();
    search.current = new URLSearchParams();
    fetchOrgUnits.mockReset().mockResolvedValue(UNITS);
    fetchTerritories.mockReset().mockResolvedValue(TERRITORIES);
    createTerritory.mockReset().mockResolvedValue(TERRITORIES[1]);
    updateTerritory.mockReset().mockResolvedValue(TERRITORIES[0]);
    deleteTerritory.mockReset().mockResolvedValue(undefined);

    auth.current = {
      status: "authenticated",
      user: { access_token: "token", profile: { sub: "subject-a" } },
      workspace: "fieldkit-dev",
      signIn: vi.fn(),
      signOut: vi.fn(),
      completeSignIn: vi.fn(),
    } as unknown as AuthContextValue;
  });

  it("names each territory's org unit by its whole path", async () => {
    // A flat list of leaf names is ambiguous the moment two regions each have a "North", which is
    // the normal case rather than the odd one.
    render(<TerritoryBrowser />);

    const table = await screen.findByRole("table");
    const rows = within(table).getAllByRole("row");
    const cells = rows.slice(1).map((row) => [...row.querySelectorAll("th,td")].map((c) => c.textContent));

    expect(cells[0]?.slice(0, 3)).toEqual(["București Nord", "Romania / Muntenia", "42"]);
    expect(cells[1]?.slice(0, 3)).toEqual(["Iași", "Romania / Moldova", "0"]);
  });

  it("puts the org-unit filter in the URL", async () => {
    // Per ADR-0004: a filtered view is what someone bookmarks and sends to a colleague, and React
    // state would make "the territories in Muntenia" unspeakable.
    render(<TerritoryBrowser />);
    await screen.findByRole("table");

    await userEvent.selectOptions(screen.getByLabelText("Filter by org unit"), "u-mun");

    expect(push).toHaveBeenCalledWith("/territories?orgUnitId=u-mun");
  });

  it("asks the API for the filtered set rather than filtering what it already has", async () => {
    // The outlet count is the server's, and so is the filter — a client-side narrowing would show a
    // page of a list it never fetched.
    search.current = new URLSearchParams("orgUnitId=u-mol");

    render(<TerritoryBrowser />);
    await screen.findByRole("table");

    expect(fetchTerritories).toHaveBeenCalledWith("token", "u-mol", expect.anything());
  });

  it("shows what the server said when a territory cannot be deleted", async () => {
    // "'București Nord' still holds 42 outlets. Move them first." is a refusal an admin can act on;
    // "could not delete" is not. Refused rather than cascaded, because a territory's membership is a
    // rep's offline scope and those outlets would vanish from a device tomorrow morning.
    deleteTerritory.mockRejectedValue(
      new ApiError(409, [
        { field: null, message: "'București Nord' still holds 42 outlet(s). Move them first." },
      ]),
    );

    render(<TerritoryBrowser />);
    await screen.findByRole("table");

    await userEvent.click(screen.getByRole("button", { name: "Delete București Nord" }));

    expect((await screen.findByRole("alert")).textContent).toContain("still holds 42 outlet(s)");
  });

  it("opens the form on the territory whose Edit was pressed", async () => {
    // React Hook Form captures its defaults on the first render, so without a key per target the
    // second territory's form would show the first one's name.
    render(<TerritoryBrowser />);
    await screen.findByRole("table");

    await userEvent.click(screen.getByRole("button", { name: "Edit București Nord" }));
    expect((screen.getByLabelText(/^name/i) as HTMLInputElement).value).toBe("București Nord");

    // Straight from one to the other, without closing in between — which is the case the key is for.
    // Cancelling first unmounts the form anyway, so a test that did that would pass without it.
    await userEvent.click(screen.getByRole("button", { name: "Edit Iași" }));

    expect((screen.getByLabelText(/^name/i) as HTMLInputElement).value).toBe("Iași");
    expect((screen.getByLabelText(/^org unit/i) as HTMLSelectElement).value).toBe("u-mol");
  });

  it("creates a territory with the org unit that was chosen", async () => {
    render(<TerritoryBrowser />);
    await screen.findByRole("table");

    await userEvent.click(screen.getByRole("button", { name: "New territory" }));
    await userEvent.type(screen.getByLabelText(/^name/i), "Cluj Vest");
    await userEvent.selectOptions(screen.getByLabelText(/^org unit/i), "u-mun");
    await userEvent.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => expect(createTerritory).toHaveBeenCalled());

    expect(createTerritory).toHaveBeenCalledWith("token", { name: "Cluj Vest", orgUnitId: "u-mun" });
  });

  it("will not save a territory with no name", async () => {
    render(<TerritoryBrowser />);
    await screen.findByRole("table");

    await userEvent.click(screen.getByRole("button", { name: "New territory" }));
    await userEvent.selectOptions(screen.getByLabelText(/^org unit/i), "u-mun");
    await userEvent.click(screen.getByRole("button", { name: "Save" }));

    const message = await screen.findByText("This field is required.");

    expect(screen.getByLabelText(/^name/i).getAttribute("aria-describedby")).toBe(message.id);
    expect(createTerritory).not.toHaveBeenCalled();
  });

  it("puts a name the server refused under the name box", async () => {
    // The API knows a name is taken; this form cannot. Now that the refusal names the field, it
    // reads exactly like a client-side one instead of appearing in a list above.
    createTerritory.mockRejectedValue(
      new ApiError(409, [{ field: "name", message: "A territory named 'Iași' already exists." }]),
    );

    render(<TerritoryBrowser />);
    await screen.findByRole("table");

    await userEvent.click(screen.getByRole("button", { name: "New territory" }));
    await userEvent.type(screen.getByLabelText(/^name/i), "Iași");
    await userEvent.selectOptions(screen.getByLabelText(/^org unit/i), "u-mol");
    await userEvent.click(screen.getByRole("button", { name: "Save" }));

    const message = await screen.findByText(/already exists/);

    expect(screen.getByLabelText(/^name/i).getAttribute("aria-describedby")).toBe(message.id);
  });

  it("puts the open detail panel in the URL", async () => {
    // Same reasoning as the filter: "the assignments for București Nord" is a view worth sending to
    // a colleague, and React state cannot be linked to.
    render(<TerritoryBrowser />);
    await screen.findByRole("table");

    await userEvent.click(screen.getByRole("button", { name: "București Nord" }));

    expect(push).toHaveBeenCalledWith("/territories?territory=t-1");
  });

  it("closes the panel when the filter would hide the territory it is about", async () => {
    // A territory the filter excludes is not one the screen is showing, and leaving its detail
    // below an empty table reads as a bug.
    search.current = new URLSearchParams("territory=t-1");

    render(<TerritoryBrowser />);
    await screen.findByRole("table");

    await userEvent.selectOptions(screen.getByLabelText("Filter by org unit"), "u-mun");

    expect(push).toHaveBeenCalledWith("/territories?orgUnitId=u-mun");
  });

  it("opens nothing for a territory id that is not in the current view", async () => {
    // A stale link or a changed filter. Opening nothing is the honest outcome — the alternative is
    // fetching a territory the list does not contain to render a panel under it.
    search.current = new URLSearchParams("territory=t-gone");

    render(<TerritoryBrowser />);
    await screen.findByRole("table");

    expect(screen.queryByRole("button", { name: "Assign a rep" })).toBeNull();
  });

  it("shows the detail panel for the territory named in the URL", async () => {
    search.current = new URLSearchParams("territory=t-2");

    render(<TerritoryBrowser />);

    expect(await screen.findByText("Reps covering Iași")).toBeTruthy();
  });

  it("offers no way to change anything to a caller who may only read", async () => {
    // Found by walking the Phase 1 demo: tenant B was shown "New user" on a screen that had just
    // refused to show them users. Hidden rather than disabled — a permission is constant for the
    // session, so a dead control is a question with no answer.
    vi.mocked(fetchIdentity).mockResolvedValue({
      subject: "subject-a",
      tenant: "fieldkit-dev",
      permissions: ["territory:read", "orgunit:read"],
    });

    render(<TerritoryBrowser />);
    await screen.findByRole("table");

    expect(screen.queryByRole("button", { name: "New territory" })).toBeNull();
    expect(screen.queryByRole("button", { name: "Edit București Nord" })).toBeNull();
    expect(screen.queryByRole("button", { name: "Delete București Nord" })).toBeNull();

    // …and the list itself is still there, because reading is what they may do.
    expect(screen.getByText("București Nord")).toBeTruthy();
  });

  it("says so when there is nothing to show, and why", async () => {
    // "No territories yet" and "none in this org unit" are different facts, and the second one is
    // about a filter someone can clear.
    fetchTerritories.mockResolvedValue([]);

    const { unmount } = render(<TerritoryBrowser />);
    expect(await screen.findByText(/No territories yet/)).toBeTruthy();

    unmount();
    search.current = new URLSearchParams("orgUnitId=u-mun");
    render(<TerritoryBrowser />);

    expect(await screen.findByText(/No territories in this org unit/)).toBeTruthy();
  });
});
