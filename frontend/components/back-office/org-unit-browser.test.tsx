// @vitest-environment jsdom

import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";

import type { AuthContextValue } from "@/components/auth-provider";
import { OrgUnitBrowser } from "@/components/back-office/org-unit-browser";
import { ApiError } from "@/lib/api/client";
import type { OrgUnit } from "@/lib/api/org";
import { render } from "@/test/render";

const auth = vi.hoisted(() => ({ current: {} as AuthContextValue }));
const fetchOrgUnits = vi.hoisted(() => vi.fn());
const createOrgUnit = vi.hoisted(() => vi.fn());
const updateOrgUnit = vi.hoisted(() => vi.fn());
const deleteOrgUnit = vi.hoisted(() => vi.fn());

vi.mock("@/components/auth-provider", () => ({ useAuth: () => auth.current }));

vi.mock("@/lib/api/org", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/lib/api/org")>()),
  fetchOrgUnits: (...args: unknown[]) => fetchOrgUnits(...args),
  createOrgUnit: (...args: unknown[]) => createOrgUnit(...args),
  updateOrgUnit: (...args: unknown[]) => updateOrgUnit(...args),
  deleteOrgUnit: (...args: unknown[]) => deleteOrgUnit(...args),
}));

const UNITS: OrgUnit[] = [
  { id: "ro", name: "Romania", parentId: null },
  { id: "south", name: "Bucharest & South", parentId: "ro" },
  { id: "team", name: "Team North", parentId: "south" },
  { id: "mol", name: "Moldova", parentId: "ro" },
];

describe("<OrgUnitBrowser>", () => {
  beforeEach(() => {
    fetchOrgUnits.mockReset().mockResolvedValue(UNITS);
    createOrgUnit.mockReset().mockResolvedValue(UNITS[0]);
    updateOrgUnit.mockReset().mockResolvedValue(UNITS[0]);
    deleteOrgUnit.mockReset().mockResolvedValue(undefined);

    auth.current = {
      status: "authenticated",
      user: { access_token: "token", profile: { sub: "subject-a" } },
      workspace: "fieldkit-dev",
      signIn: vi.fn(),
      signOut: vi.fn(),
      completeSignIn: vi.fn(),
    } as unknown as AuthContextValue;
  });

  it("draws the hierarchy parents-first, whatever depth a tenant uses", () => {
    // ORG-01: depth and labels are the tenant's own, so the shape is the information. A flat list
    // sorted by name would put a team next to a country and say nothing about which contains which.
    render(<OrgUnitBrowser />);

    return waitFor(() =>
      expect(screen.getAllByRole("listitem").map((row) => row.textContent?.split("Edit")[0])).toEqual([
        "Romania",
        "└Bucharest & South, in Romania / Bucharest & South",
        "└Team North, in Romania / Bucharest & South / Team North",
        "└Moldova, in Romania / Moldova",
      ]),
    );
  });

  it("tells an empty workspace where to start", async () => {
    // The state this whole screen exists for: without a unit there is nothing for a territory to
    // hang off, and until now nothing could create the first one.
    fetchOrgUnits.mockResolvedValue([]);

    render(<OrgUnitBrowser />);

    expect(await screen.findByText(/a territory has to hang off one/)).toBeTruthy();
  });

  it("creates a top-level unit when nothing is chosen as its parent", async () => {
    // Empty is a root, not a missing answer — the top of a hierarchy has no parent.
    render(<OrgUnitBrowser />);
    await screen.findAllByRole("listitem");

    await userEvent.click(screen.getByRole("button", { name: "New org unit" }));
    await userEvent.type(screen.getByLabelText(/^name/i), "Hungary");
    await userEvent.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => expect(createOrgUnit).toHaveBeenCalled());

    expect(createOrgUnit).toHaveBeenCalledWith("token", { name: "Hungary", parentId: null });
  });

  it("names each parent option by its whole path", async () => {
    render(<OrgUnitBrowser />);
    await screen.findAllByRole("listitem");

    await userEvent.click(screen.getByRole("button", { name: "New org unit" }));

    const options = [...screen.getByLabelText(/sits under/i).querySelectorAll("option")];

    expect(options.map((option) => option.textContent)).toContain(
      "Romania / Bucharest & South / Team North",
    );
  });

  it("will not offer a unit its own subtree as a parent", async () => {
    // Choosing a descendant makes a cycle, which the API refuses. Unlike a name collision this is
    // never what somebody meant — there is no version of "move Romania under Team North" that is a
    // good idea — so it is not offered rather than explained afterwards.
    render(<OrgUnitBrowser />);
    await screen.findAllByRole("listitem");

    await userEvent.click(screen.getByRole("button", { name: "Edit Romania" }));

    const options = [...screen.getByLabelText(/sits under/i).querySelectorAll("option")].map(
      (option) => option.textContent,
    );

    expect(options).not.toContain("Romania");
    expect(options).not.toContain("Romania / Bucharest & South");
    expect(options).not.toContain("Romania / Bucharest & South / Team North");
  });

  it("renames and reparents in one call", async () => {
    // The API made them one request so "rename this team and move it under the new region" cannot
    // half-succeed. Splitting it here would put the failure back.
    render(<OrgUnitBrowser />);
    await screen.findAllByRole("listitem");

    await userEvent.click(screen.getByRole("button", { name: "Edit Team North" }));
    await userEvent.clear(screen.getByLabelText(/^name/i));
    await userEvent.type(screen.getByLabelText(/^name/i), "Team East");
    await userEvent.selectOptions(screen.getByLabelText(/sits under/i), "mol");
    await userEvent.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => expect(updateOrgUnit).toHaveBeenCalled());

    expect(updateOrgUnit).toHaveBeenCalledWith("token", "team", {
      name: "Team East",
      parentId: "mol",
    });
  });

  it("shows what is in the way when a unit cannot be deleted", async () => {
    // Three refusals, each naming what is holding it: child units, staffed positions, or
    // territories. "Could not delete" throws away the only part an admin can act on.
    deleteOrgUnit.mockRejectedValue(
      new ApiError(409, [
        { field: null, message: "'Romania' still has 2 child unit(s). Move or delete them first." },
      ]),
    );

    render(<OrgUnitBrowser />);
    await screen.findAllByRole("listitem");

    await userEvent.click(screen.getByRole("button", { name: "Delete Romania" }));

    expect((await screen.findByRole("alert")).textContent).toContain("still has 2 child unit(s)");
  });

  it("puts a refusal the server attributed under the control it is about", async () => {
    createOrgUnit.mockRejectedValue(
      new ApiError(409, [{ field: "name", message: "'Moldova' already exists under this parent." }]),
    );

    render(<OrgUnitBrowser />);
    await screen.findAllByRole("listitem");

    await userEvent.click(screen.getByRole("button", { name: "New org unit" }));
    await userEvent.type(screen.getByLabelText(/^name/i), "Moldova");
    await userEvent.click(screen.getByRole("button", { name: "Save" }));

    const message = await screen.findByText(/already exists under this parent/);

    expect(screen.getByLabelText(/^name/i).getAttribute("aria-describedby")).toBe(message.id);
  });
});
