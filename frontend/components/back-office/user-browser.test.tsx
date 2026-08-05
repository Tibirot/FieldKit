// @vitest-environment jsdom

import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";

import type { AuthContextValue } from "@/components/auth-provider";
import { UserBrowser } from "@/components/back-office/user-browser";
import { ApiError } from "@/lib/api/client";
import type { Role, User } from "@/lib/api/users";
import { render } from "@/test/render";

const auth = vi.hoisted(() => ({ current: {} as AuthContextValue }));
const fetchUsers = vi.hoisted(() => vi.fn());
const fetchRoles = vi.hoisted(() => vi.fn());
const createUser = vi.hoisted(() => vi.fn());
const updateUser = vi.hoisted(() => vi.fn());
const setUserActive = vi.hoisted(() => vi.fn());

vi.mock("@/components/auth-provider", () => ({ useAuth: () => auth.current }));

vi.mock("@/lib/api/users", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/lib/api/users")>()),
  fetchUsers: (...args: unknown[]) => fetchUsers(...args),
  fetchRoles: (...args: unknown[]) => fetchRoles(...args),
  createUser: (...args: unknown[]) => createUser(...args),
  updateUser: (...args: unknown[]) => updateUser(...args),
  setUserActive: (...args: unknown[]) => setUserActive(...args),
}));

const ROLES: Role[] = [
  { id: "r-admin", name: "Administrator", isSystemTemplate: true, permissions: ["outlet:write"] },
  { id: "r-rep", name: "Field rep", isSystemTemplate: true, permissions: ["outlet:read"] },
  { id: "r-audit", name: "Auditor", isSystemTemplate: false, permissions: ["outlet:read"] },
];

const USERS: User[] = [
  {
    id: "u-1",
    subjectId: "sub-ana",
    email: "ana@example.com",
    displayName: "Ana Ionescu",
    locale: "ro-RO",
    timeZone: "Europe/Bucharest",
    isActive: true,
    roleIds: ["r-admin", "r-rep"],
  },
  {
    id: "u-2",
    subjectId: "sub-bogdan",
    email: "bogdan@example.com",
    displayName: "Bogdan Pop",
    locale: "en-GB",
    timeZone: "Europe/London",
    isActive: false,
    roleIds: ["r-rep"],
  },
];

describe("<UserBrowser>", () => {
  beforeEach(() => {
    fetchUsers.mockReset().mockResolvedValue(USERS);
    fetchRoles.mockReset().mockResolvedValue(ROLES);
    createUser.mockReset().mockResolvedValue(USERS[0]);
    updateUser.mockReset().mockResolvedValue(USERS[0]);
    setUserActive.mockReset().mockResolvedValue({ ...USERS[1], isActive: true });

    auth.current = {
      status: "authenticated",
      user: { access_token: "token", profile: { sub: "subject-a" } },
      workspace: "fieldkit-dev",
      signIn: vi.fn(),
      signOut: vi.fn(),
      completeSignIn: vi.fn(),
    } as unknown as AuthContextValue;
  });

  it("names each user's roles rather than counting them", async () => {
    // "2 roles" is a number an admin then has to go and look up, which is the click this column
    // exists to save.
    render(<UserBrowser />);

    const table = await screen.findByRole("table");
    const rows = within(table).getAllByRole("row");
    const cells = rows.slice(1).map((row) => [...row.querySelectorAll("th,td")].map((c) => c.textContent));

    expect(cells[0]?.slice(0, 4)).toEqual([
      "Ana Ionescu",
      "ana@example.com",
      "Administrator, Field rep",
      "Active",
    ]);
  });

  it("keeps a deactivated account on the list", async () => {
    // "Why can't Bogdan log in" is answered here or nowhere — and hiding the row would make
    // reactivation reachable only by someone who already knew to look.
    render(<UserBrowser />);
    await screen.findByRole("table");

    expect(screen.getByText("Bogdan Pop")).toBeTruthy();
    expect(screen.getByText("Deactivated")).toBeTruthy();
    expect(screen.getByRole("button", { name: "Reactivate Bogdan Pop" })).toBeTruthy();
  });

  it("turns an account off through its own verb, not a profile edit", async () => {
    // Deactivation publishes `UserDeactivated` so Sync releases the bound device (A8). A consequence
    // that size should not be reachable by an unrelated edit to somebody's timezone.
    render(<UserBrowser />);
    await screen.findByRole("table");

    await userEvent.click(screen.getByRole("button", { name: "Deactivate Ana Ionescu" }));

    await waitFor(() => expect(setUserActive).toHaveBeenCalled());

    expect(setUserActive).toHaveBeenCalledWith("token", "u-1", false);
    expect(updateUser).not.toHaveBeenCalled();
  });

  it("will not save a user with no role", async () => {
    // BR-IAM-3. Checked here so the message lands on the control rather than arriving as a refusal
    // after a round trip — the server enforces it too.
    render(<UserBrowser />);
    await screen.findByRole("table");

    await userEvent.click(screen.getByRole("button", { name: "New user" }));
    await userEvent.type(screen.getByLabelText(/subject/i), "sub-new");
    await userEvent.type(screen.getByLabelText(/^name/i), "Carmen Dinu");
    await userEvent.type(screen.getByLabelText(/^email/i), "carmen@example.com");
    await userEvent.type(screen.getByLabelText(/^locale/i), "ro-RO");
    await userEvent.selectOptions(screen.getByLabelText(/time zone/i), "Europe/Bucharest");
    await userEvent.click(screen.getByRole("button", { name: "Save" }));

    expect(await screen.findByText("Pick at least one role.")).toBeTruthy();
    expect(createUser).not.toHaveBeenCalled();
  });

  it("sends every role that was ticked", async () => {
    render(<UserBrowser />);
    await screen.findByRole("table");

    await userEvent.click(screen.getByRole("button", { name: "New user" }));
    await userEvent.type(screen.getByLabelText(/subject/i), "sub-new");
    await userEvent.type(screen.getByLabelText(/^name/i), "Carmen Dinu");
    await userEvent.type(screen.getByLabelText(/^email/i), "carmen@example.com");
    await userEvent.type(screen.getByLabelText(/^locale/i), "ro-RO");
    await userEvent.selectOptions(screen.getByLabelText(/time zone/i), "Europe/Bucharest");
    await userEvent.click(screen.getByRole("checkbox", { name: /Field rep/ }));
    await userEvent.click(screen.getByRole("checkbox", { name: /Auditor/ }));
    await userEvent.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => expect(createUser).toHaveBeenCalled());

    expect(createUser).toHaveBeenCalledWith("token", {
      subjectId: "sub-new",
      displayName: "Carmen Dinu",
      email: "carmen@example.com",
      locale: "ro-RO",
      timeZone: "Europe/Bucharest",
      roleIds: ["r-rep", "r-audit"],
    });
  });

  it("will not let the subject id be changed on an existing user", async () => {
    // It is what every other module refers to this person by — a rep assignment among them — so
    // changing it would orphan their work rather than move it.
    render(<UserBrowser />);
    await screen.findByRole("table");

    await userEvent.click(screen.getByRole("button", { name: "Edit Ana Ionescu" }));

    const subject = screen.getByLabelText(/subject/i) as HTMLInputElement;

    expect(subject.value).toBe("sub-ana");
    expect(subject.readOnly).toBe(true);
  });

  it("ticks the roles a user already holds", async () => {
    render(<UserBrowser />);
    await screen.findByRole("table");

    await userEvent.click(screen.getByRole("button", { name: "Edit Ana Ionescu" }));

    expect((screen.getByRole("checkbox", { name: /Administrator/ }) as HTMLInputElement).checked).toBe(true);
    expect((screen.getByRole("checkbox", { name: /Field rep/ }) as HTMLInputElement).checked).toBe(true);
    expect((screen.getByRole("checkbox", { name: /Auditor/ }) as HTMLInputElement).checked).toBe(false);
  });

  it("keeps the locale a user already has", async () => {
    // The bug this exists for. A user's locale is a full BCP-47 tag — `ro-RO` — because it drives
    // formatting and not only translation (ADR-0010, BR-IAM-5). Offering the app's two UI languages
    // as a select could not express it: opening an existing user showed an empty box, and saving
    // would have quietly changed their formatting locale.
    render(<UserBrowser />);
    await screen.findByRole("table");

    await userEvent.click(screen.getByRole("button", { name: "Edit Ana Ionescu" }));

    expect((screen.getByLabelText(/^locale/i) as HTMLInputElement).value).toBe("ro-RO");

    await userEvent.click(screen.getByRole("button", { name: "Save" }));
    await waitFor(() => expect(updateUser).toHaveBeenCalled());

    expect(updateUser).toHaveBeenCalledWith("token", "u-1", expect.objectContaining({ locale: "ro-RO" }));
  });

  it("accepts the tags a real profile carries", async () => {
    // A UN M.49 region (`es-419`) and a script subtag (`zh-Hant-TW`) are both ordinary BCP-47 and
    // both would be rejected by a pattern that only allows two letters and two letters. The one that
    // shipped in the first draft of this file did exactly that, because an escape was lost.
    render(<UserBrowser />);
    await screen.findByRole("table");

    for (const tag of ["es-419", "zh-Hant-TW", "en-GB"]) {
      await userEvent.click(screen.getByRole("button", { name: "Edit Ana Ionescu" }));
      await userEvent.clear(screen.getByLabelText(/^locale/i));
      await userEvent.type(screen.getByLabelText(/^locale/i), tag);
      await userEvent.click(screen.getByRole("button", { name: "Save" }));

      await waitFor(() => expect(updateUser).toHaveBeenCalled());
      expect(updateUser).toHaveBeenLastCalledWith("token", "u-1", expect.objectContaining({ locale: tag }));
    }
  });

  it("refuses something that is not a language tag", async () => {
    render(<UserBrowser />);
    await screen.findByRole("table");

    await userEvent.click(screen.getByRole("button", { name: "Edit Ana Ionescu" }));
    await userEvent.clear(screen.getByLabelText(/^locale/i));
    await userEvent.type(screen.getByLabelText(/^locale/i), "Romanian");
    await userEvent.click(screen.getByRole("button", { name: "Save" }));

    expect(await screen.findByText("Use a BCP-47 tag, such as ro-RO.")).toBeTruthy();
    expect(updateUser).not.toHaveBeenCalled();
  });

  it("puts a refusal the server attributed under the control it is about", async () => {
    createUser.mockRejectedValue(
      new ApiError(409, [{ field: "email", message: "'carmen@example.com' is already in use." }]),
    );

    render(<UserBrowser />);
    await screen.findByRole("table");

    await userEvent.click(screen.getByRole("button", { name: "New user" }));
    await userEvent.type(screen.getByLabelText(/subject/i), "sub-new");
    await userEvent.type(screen.getByLabelText(/^name/i), "Carmen Dinu");
    await userEvent.type(screen.getByLabelText(/^email/i), "carmen@example.com");
    await userEvent.type(screen.getByLabelText(/^locale/i), "ro-RO");
    await userEvent.selectOptions(screen.getByLabelText(/time zone/i), "Europe/Bucharest");
    await userEvent.click(screen.getByRole("checkbox", { name: /Field rep/ }));
    await userEvent.click(screen.getByRole("button", { name: "Save" }));

    const message = await screen.findByText(/already in use/);

    expect(screen.getByLabelText(/^email/i).getAttribute("aria-describedby")).toBe(message.id);
  });

  it("says which roles are built in", async () => {
    // A system template is the way back to a working set of roles (IAM-06), so it behaves
    // differently from one a tenant invented — and the difference should be visible before someone
    // discovers it by being refused a delete.
    render(<UserBrowser />);
    await screen.findByRole("table");

    await userEvent.click(screen.getByRole("button", { name: "New user" }));

    expect(screen.getByRole("checkbox", { name: /Administrator/ }).closest("label")?.textContent)
      .toContain("Built in");
    expect(screen.getByRole("checkbox", { name: /Auditor/ }).closest("label")?.textContent)
      .not.toContain("Built in");
  });
});
