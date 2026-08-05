// @vitest-environment jsdom

import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";

import type { AuthContextValue } from "@/components/auth-provider";
import { TerritoryAssignments } from "@/components/back-office/territory-assignments";
import { ApiError } from "@/lib/api/client";
import type { RepAssignment, Territory } from "@/lib/api/org";
import type { User } from "@/lib/api/users";
import { render } from "@/test/render";

const auth = vi.hoisted(() => ({ current: {} as AuthContextValue }));
const fetchAssignments = vi.hoisted(() => vi.fn());
const createAssignment = vi.hoisted(() => vi.fn());
const updateAssignment = vi.hoisted(() => vi.fn());
const deleteAssignment = vi.hoisted(() => vi.fn());
const fetchUsers = vi.hoisted(() => vi.fn());

vi.mock("@/components/auth-provider", () => ({ useAuth: () => auth.current }));

vi.mock("@/lib/api/org", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/lib/api/org")>()),
  fetchAssignments: (...args: unknown[]) => fetchAssignments(...args),
  createAssignment: (...args: unknown[]) => createAssignment(...args),
  updateAssignment: (...args: unknown[]) => updateAssignment(...args),
  deleteAssignment: (...args: unknown[]) => deleteAssignment(...args),
}));

vi.mock("@/lib/api/users", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/lib/api/users")>()),
  fetchUsers: (...args: unknown[]) => fetchUsers(...args),
}));

const TERRITORY: Territory = {
  id: "t-1",
  name: "București Nord",
  orgUnitId: "u-mun",
  outletCount: 42,
};

const USERS: User[] = [
  {
    id: "019f-a",
    subjectId: "sub-ana",
    email: "ana@example.com",
    displayName: "Ana Ionescu",
    locale: "ro",
    timeZone: "Europe/Bucharest",
    isActive: true,
    roleIds: [],
  },
  {
    id: "019f-b",
    subjectId: "sub-bogdan",
    email: "bogdan@example.com",
    displayName: "Bogdan Pop",
    locale: "ro",
    timeZone: "Europe/Bucharest",
    isActive: false,
    roleIds: [],
  },
];

const ASSIGNMENTS: RepAssignment[] = [
  {
    id: "a-1",
    territoryId: "t-1",
    userId: "sub-ana",
    displayName: "Ana Ionescu",
    from: "2026-07-01",
    to: null,
    isCurrent: true,
  },
  {
    id: "a-2",
    territoryId: "t-1",
    userId: "sub-old",
    displayName: null,
    from: "2025-01-01",
    to: "2026-06-30",
    isCurrent: false,
  },
];

describe("<TerritoryAssignments>", () => {
  beforeEach(() => {
    fetchAssignments.mockReset().mockResolvedValue(ASSIGNMENTS);
    fetchUsers.mockReset().mockResolvedValue(USERS);
    createAssignment.mockReset().mockResolvedValue(ASSIGNMENTS[0]);
    updateAssignment.mockReset().mockResolvedValue(ASSIGNMENTS[0]);
    deleteAssignment.mockReset().mockResolvedValue(undefined);

    auth.current = {
      status: "authenticated",
      user: { access_token: "token", profile: { sub: "subject-a" } },
      workspace: "fieldkit-dev",
      signIn: vi.fn(),
      signOut: vi.fn(),
      completeSignIn: vi.fn(),
    } as unknown as AuthContextValue;
  });

  it("shows the whole history, and marks which one is current", async () => {
    // A history rather than a current holder: BR-ORG-2 allows one rep at a time, so more than one
    // row means they do not overlap — not that the rule was bent.
    render(<TerritoryAssignments territory={TERRITORY} />);

    const items = await screen.findAllByRole("listitem");

    expect(items).toHaveLength(2);
    expect(items[0].textContent).toContain("Ana Ionescu");
    expect(items[0].textContent).toContain("Current");
    expect(items[1].textContent).not.toContain("Current");
  });

  it("says when an assignment has no end date rather than leaving it blank", async () => {
    // Open-ended is the ordinary case — a rep covers a territory until somebody decides otherwise —
    // and an empty cell reads as missing data.
    render(<TerritoryAssignments territory={TERRITORY} />);

    const items = await screen.findAllByRole("listitem");

    expect(items[0].textContent).toContain("until further notice");
    expect(items[1].textContent).toContain("–");
  });

  it("names a rep the directory no longer resolves", async () => {
    // The assignment still stands, and its history is what the panel is for.
    render(<TerritoryAssignments territory={TERRITORY} />);

    const items = await screen.findAllByRole("listitem");

    expect(items[1].textContent).toContain("Unknown rep");
  });

  it("sends the Keycloak subject, not the profile's row id", async () => {
    // Two different strings on the same user. The wrong one comes back as "No such user in this
    // tenant", which reads like a missing person rather than a mismatched identifier.
    render(<TerritoryAssignments territory={TERRITORY} />);
    await screen.findAllByRole("listitem");

    await userEvent.click(screen.getByRole("button", { name: "Assign a rep" }));
    await userEvent.selectOptions(screen.getByLabelText(/^rep/i), "sub-ana");
    await userEvent.type(screen.getByLabelText(/^from/i), "2026-09-01");
    await userEvent.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => expect(createAssignment).toHaveBeenCalled());

    expect(createAssignment).toHaveBeenCalledWith("token", "t-1", {
      userId: "sub-ana",
      from: "2026-09-01",
      to: null,
    });
  });

  it("does not offer a deactivated rep", async () => {
    // The server refuses assigning one, and offering the choice only to take it back is worse than
    // not offering it.
    render(<TerritoryAssignments territory={TERRITORY} />);
    await screen.findAllByRole("listitem");

    await userEvent.click(screen.getByRole("button", { name: "Assign a rep" }));

    const options = [...screen.getByLabelText(/^rep/i).querySelectorAll("option")];

    expect(options.map((option) => option.textContent)).toContain("Ana Ionescu");
    expect(options.map((option) => option.textContent)).not.toContain("Bogdan Pop");
  });

  it("puts an overlap refusal under the date it is about", async () => {
    // BR-ORG-2 is the server's rule and is not re-checked here — two people can be editing the same
    // territory, so a client-side answer is a guess about a set it does not own. The refusal names
    // `from`, because moving the start is the usual way out of an overlap.
    createAssignment.mockRejectedValue(
      new ApiError(409, [
        {
          field: "from",
          message: "Another rep is already assigned to this territory from 2026-07-01 to further notice.",
        },
      ]),
    );

    render(<TerritoryAssignments territory={TERRITORY} />);
    await screen.findAllByRole("listitem");

    await userEvent.click(screen.getByRole("button", { name: "Assign a rep" }));
    await userEvent.selectOptions(screen.getByLabelText(/^rep/i), "sub-ana");
    await userEvent.type(screen.getByLabelText(/^from/i), "2026-08-01");
    await userEvent.click(screen.getByRole("button", { name: "Save" }));

    const message = await screen.findByText(/Another rep is already assigned/);

    expect(screen.getByLabelText(/^from/i).getAttribute("aria-describedby")).toBe(message.id);
  });

  it("opens the form on the assignment whose Edit was pressed", async () => {
    // React Hook Form captures its defaults on the first render, so without a key per target the
    // second assignment's form would show the first one's dates.
    render(<TerritoryAssignments territory={TERRITORY} />);
    await screen.findAllByRole("listitem");

    await userEvent.click(screen.getByRole("button", { name: "Edit the assignment for Ana Ionescu" }));
    expect((screen.getByLabelText(/^from/i) as HTMLInputElement).value).toBe("2026-07-01");

    await userEvent.click(screen.getByRole("button", { name: "Edit the assignment for Unknown rep" }));

    expect((screen.getByLabelText(/^from/i) as HTMLInputElement).value).toBe("2025-01-01");
    expect((screen.getByLabelText(/^to/i) as HTMLInputElement).value).toBe("2026-06-30");
  });

  it("shows what the server said when an assignment cannot be removed", async () => {
    deleteAssignment.mockRejectedValue(new ApiError(409, [{ field: null, message: "Still in use." }]));

    render(<TerritoryAssignments territory={TERRITORY} />);
    await screen.findAllByRole("listitem");

    await userEvent.click(
      screen.getByRole("button", { name: "Remove the assignment for Ana Ionescu" }),
    );

    expect((await screen.findByRole("alert")).textContent).toContain("Still in use.");
  });

  it("says so when nobody is assigned", async () => {
    fetchAssignments.mockResolvedValue([]);

    render(<TerritoryAssignments territory={TERRITORY} />);

    expect(await screen.findByText(/Nobody is assigned/)).toBeTruthy();
  });
});
