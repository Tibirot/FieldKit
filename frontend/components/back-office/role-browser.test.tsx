// @vitest-environment jsdom

import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";

import type { AuthContextValue } from "@/components/auth-provider";
import { RoleBrowser } from "@/components/back-office/role-browser";
import { ApiError } from "@/lib/api/client";
import type { Permission, Role } from "@/lib/api/users";
import { render } from "@/test/render";

const auth = vi.hoisted(() => ({ current: {} as AuthContextValue }));
const fetchRoles = vi.hoisted(() => vi.fn());
const fetchPermissions = vi.hoisted(() => vi.fn());
const createRole = vi.hoisted(() => vi.fn());
const updateRole = vi.hoisted(() => vi.fn());
const deleteRole = vi.hoisted(() => vi.fn());

vi.mock("@/components/auth-provider", () => ({ useAuth: () => auth.current }));

vi.mock("@/lib/api/users", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/lib/api/users")>()),
  fetchRoles: (...args: unknown[]) => fetchRoles(...args),
  fetchPermissions: (...args: unknown[]) => fetchPermissions(...args),
  createRole: (...args: unknown[]) => createRole(...args),
  updateRole: (...args: unknown[]) => updateRole(...args),
  deleteRole: (...args: unknown[]) => deleteRole(...args),
}));

const PERMISSIONS: Permission[] = [
  { name: "outlet:read", description: "View outlets and their classification." },
  { name: "outlet:write", description: "Create and edit outlets, and change their status." },
  { name: "territory:read", description: "View territories and who covers them." },
  { name: "user:write", description: "Create and edit users, and set the roles they hold." },
];

const ROLES: Role[] = [
  {
    id: "r-rep",
    name: "Field rep",
    isSystemTemplate: true,
    permissions: ["outlet:read", "territory:read"],
  },
  { id: "r-empty", name: "Observer", isSystemTemplate: false, permissions: [] },
];

describe("<RoleBrowser>", () => {
  beforeEach(() => {
    fetchRoles.mockReset().mockResolvedValue(ROLES);
    fetchPermissions.mockReset().mockResolvedValue(PERMISSIONS);
    createRole.mockReset().mockResolvedValue(ROLES[1]);
    updateRole.mockReset().mockResolvedValue(ROLES[0]);
    deleteRole.mockReset().mockResolvedValue(undefined);

    auth.current = {
      status: "authenticated",
      user: { access_token: "token", profile: { sub: "subject-a" } },
      workspace: "fieldkit-dev",
      signIn: vi.fn(),
      signOut: vi.fn(),
      completeSignIn: vi.fn(),
    } as unknown as AuthContextValue;
  });

  it("shows which permissions a role carries, not how many", async () => {
    // Which ones is the question this list is asked; a count sends someone into the form to find
    // out. Grouped by resource, so outlet:read and outlet:write read as one decision about outlets.
    render(<RoleBrowser />);

    const items = await screen.findAllByRole("listitem");

    expect(items[0].textContent).toContain("outlet");
    expect(items[0].textContent).toContain("read");
    expect(items[0].textContent).toContain("territory");
    expect(items[0].textContent).not.toContain("outlet:write");
  });

  it("says a role grants nothing rather than leaving a gap", async () => {
    // A role that grants nothing is a real state — a group named before anyone decided what it may
    // do — not a rendering failure.
    render(<RoleBrowser />);
    await screen.findAllByRole("listitem");

    expect(screen.getByText("Grants nothing yet.")).toBeTruthy();
  });

  it("offers every permission the system enforces, with what it does", async () => {
    // The catalogue is code — a permission exists because a module checks it — so this list cannot
    // offer one that grants nothing. The description is the decision; the identifier is reference.
    render(<RoleBrowser />);
    await screen.findAllByRole("listitem");

    await userEvent.click(screen.getByRole("button", { name: "New role" }));

    for (const permission of PERMISSIONS) {
      expect(screen.getByRole("checkbox", { name: new RegExp(permission.name) })).toBeTruthy();
      expect(screen.getByText(permission.description)).toBeTruthy();
    }
  });

  it("groups the toggles by the resource they are about", async () => {
    // Thirty flat checkboxes is a list; a handful of decisions about outlets, territories and users
    // is a form someone can reason about.
    render(<RoleBrowser />);
    await screen.findAllByRole("listitem");

    await userEvent.click(screen.getByRole("button", { name: "New role" }));

    const outlets = screen.getByRole("heading", { name: "outlet" }).closest("div")!;

    expect(within(outlets).getAllByRole("checkbox")).toHaveLength(2);
    expect(within(outlets).queryByRole("checkbox", { name: /territory/ })).toBeNull();
  });

  it("ticks the permissions a role already has", async () => {
    render(<RoleBrowser />);
    await screen.findAllByRole("listitem");

    await userEvent.click(screen.getByRole("button", { name: "Edit the Field rep role" }));

    expect((screen.getByRole("checkbox", { name: /outlet:read/ }) as HTMLInputElement).checked).toBe(true);
    expect((screen.getByRole("checkbox", { name: /outlet:write/ }) as HTMLInputElement).checked).toBe(false);
    expect((screen.getByRole("checkbox", { name: /territory:read/ }) as HTMLInputElement).checked).toBe(true);
  });

  it("sends exactly the permissions that are ticked", async () => {
    render(<RoleBrowser />);
    await screen.findAllByRole("listitem");

    await userEvent.click(screen.getByRole("button", { name: "Edit the Field rep role" }));
    await userEvent.click(screen.getByRole("checkbox", { name: /outlet:write/ }));
    await userEvent.click(screen.getByRole("checkbox", { name: /territory:read/ }));
    await userEvent.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => expect(updateRole).toHaveBeenCalled());

    expect(updateRole).toHaveBeenCalledWith("token", "r-rep", {
      name: "Field rep",
      permissions: ["outlet:read", "outlet:write"],
    });
  });

  it("saves a role that grants nothing", async () => {
    // BR-IAM-3 is about a *user* holding a role, which is a different rule and lives on the user
    // form. A role with no permissions is allowed, and the server agrees.
    render(<RoleBrowser />);
    await screen.findAllByRole("listitem");

    await userEvent.click(screen.getByRole("button", { name: "New role" }));
    await userEvent.type(screen.getByLabelText(/^name/i), "Observer");
    await userEvent.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => expect(createRole).toHaveBeenCalled());

    expect(createRole).toHaveBeenCalledWith("token", { name: "Observer", permissions: [] });
  });

  it("lets a built-in role be edited, and says why it cannot be deleted", async () => {
    // A tenant may recompose a template to fit how they work. What they cannot do is strand
    // themselves with none — it is the way back to a working set (IAM-06).
    deleteRole.mockRejectedValue(
      new ApiError(409, [
        { field: null, message: "A system role template cannot be deleted. Edit it instead." },
      ]),
    );

    render(<RoleBrowser />);
    await screen.findAllByRole("listitem");

    await userEvent.click(screen.getByRole("button", { name: "Edit the Field rep role" }));
    expect(screen.getByText(/can be recomposed, but not deleted/)).toBeTruthy();

    await userEvent.click(screen.getByRole("button", { name: "Cancel" }));
    await userEvent.click(screen.getByRole("button", { name: "Delete the Field rep role" }));

    expect((await screen.findByRole("alert")).textContent).toContain("cannot be deleted");
  });

  it("shows what the server said when a role is still held", async () => {
    // "4 user(s) still hold this role. Reassign them before deleting it." is a refusal an admin can
    // act on; "could not delete" is not.
    deleteRole.mockRejectedValue(
      new ApiError(409, [
        { field: null, message: "4 user(s) still hold this role. Reassign them before deleting it." },
      ]),
    );

    render(<RoleBrowser />);
    await screen.findAllByRole("listitem");

    await userEvent.click(screen.getByRole("button", { name: "Delete the Observer role" }));

    expect((await screen.findByRole("alert")).textContent).toContain("still hold this role");
  });

  it("puts a refusal the server attributed under the control it is about", async () => {
    createRole.mockRejectedValue(
      new ApiError(409, [{ field: "name", message: "A role named 'Observer' already exists." }]),
    );

    render(<RoleBrowser />);
    await screen.findAllByRole("listitem");

    await userEvent.click(screen.getByRole("button", { name: "New role" }));
    await userEvent.type(screen.getByLabelText(/^name/i), "Observer");
    await userEvent.click(screen.getByRole("button", { name: "Save" }));

    const message = await screen.findByText(/already exists/);

    expect(screen.getByLabelText(/^name/i).getAttribute("aria-describedby")).toBe(message.id);
  });
});
