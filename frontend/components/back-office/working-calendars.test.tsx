// @vitest-environment jsdom

import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";

import type { AuthContextValue } from "@/components/auth-provider";
import { WorkingCalendars } from "@/components/back-office/working-calendars";
import { ApiError } from "@/lib/api/client";
import { fetchIdentity } from "@/lib/api/identity";
import type { Holiday, WorkingCalendar } from "@/lib/api/journeys";
import type { User } from "@/lib/api/users";
import { render } from "@/test/render";

const auth = vi.hoisted(() => ({ current: {} as AuthContextValue }));
const fetchCalendars = vi.hoisted(() => vi.fn());
const fetchHolidays = vi.hoisted(() => vi.fn());
const setCalendar = vi.hoisted(() => vi.fn());
const deleteCalendar = vi.hoisted(() => vi.fn());
const addHoliday = vi.hoisted(() => vi.fn());
const deleteHoliday = vi.hoisted(() => vi.fn());
const fetchUsers = vi.hoisted(() => vi.fn());

vi.mock("@/components/auth-provider", () => ({ useAuth: () => auth.current }));

vi.mock("@/lib/api/journeys", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/lib/api/journeys")>()),
  fetchCalendars: (...args: unknown[]) => fetchCalendars(...args),
  fetchHolidays: (...args: unknown[]) => fetchHolidays(...args),
  setCalendar: (...args: unknown[]) => setCalendar(...args),
  deleteCalendar: (...args: unknown[]) => deleteCalendar(...args),
  addHoliday: (...args: unknown[]) => addHoliday(...args),
  deleteHoliday: (...args: unknown[]) => deleteHoliday(...args),
}));

vi.mock("@/lib/api/users", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/lib/api/users")>()),
  fetchUsers: (...args: unknown[]) => fetchUsers(...args),
}));

const MARIA: WorkingCalendar = {
  userId: "subject-maria",
  displayName: "Maria Ionescu",
  workingDays: ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday"],
  visitsPerDay: 8,
};

const CHRISTMAS: Holiday = { id: "h-1", date: "2026-12-25", name: "Christmas Day" };

const USERS: User[] = [
  {
    id: "u-1",
    subjectId: "subject-maria",
    email: "maria@fieldkit.local",
    displayName: "Maria Ionescu",
    locale: "ro-RO",
    timeZone: "Europe/Bucharest",
    isActive: true,
    roleIds: [],
  },
  {
    id: "u-2",
    subjectId: "subject-andrei",
    email: "andrei@fieldkit.local",
    displayName: "Andrei Pop",
    locale: "ro-RO",
    timeZone: "Europe/Bucharest",
    isActive: true,
    roleIds: [],
  },
  {
    id: "u-3",
    subjectId: "subject-gone",
    email: "gone@fieldkit.local",
    displayName: "Departed Rep",
    locale: "ro-RO",
    timeZone: "Europe/Bucharest",
    isActive: false,
    roleIds: [],
  },
];

/** Waits for the permission answer — the write controls only exist once it has arrived. */
async function ready(): Promise<void> {
  await screen.findByRole("button", { name: "Save the calendar for Maria Ionescu" });
}

function signedIn(permissions: readonly string[]): void {
  vi.mocked(fetchIdentity).mockResolvedValue({
    subject: "subject-a",
    tenant: "fieldkit-dev",
    permissions: [...permissions],
  });

  auth.current = {
    status: "authenticated",
    user: { access_token: "token", profile: { sub: "subject-a" } },
    workspace: "fieldkit-dev",
    signIn: vi.fn(),
    signOut: vi.fn(),
    completeSignIn: vi.fn(),
  } as unknown as AuthContextValue;
}

describe("<WorkingCalendars>", () => {
  beforeEach(() => {
    fetchCalendars.mockReset().mockResolvedValue([MARIA]);
    fetchHolidays.mockReset().mockResolvedValue([CHRISTMAS]);
    setCalendar.mockReset().mockResolvedValue(MARIA);
    deleteCalendar.mockReset().mockResolvedValue(undefined);
    addHoliday.mockReset().mockResolvedValue(CHRISTMAS);
    deleteHoliday.mockReset().mockResolvedValue(undefined);
    fetchUsers.mockReset().mockResolvedValue(USERS);

    signedIn(["journey:read", "journey:write", "user:read"]);
  });

  it("shows the week starting on Monday, whatever the wire says", async () => {
    // .NET's DayOfWeek numbers the week from Sunday, which is why the API takes names — and why a
    // screen is free to read the week the way its readers do.
    render(<WorkingCalendars />);
    await ready();

    const days = screen
      .getAllByRole("checkbox")
      .map((box) => box.getAttribute("aria-label"));

    expect(days[0]).toBe("Monday, Maria Ionescu");
    expect(days[6]).toBe("Sunday, Maria Ionescu");
  });

  it("sends the days by name, in week order", async () => {
    render(<WorkingCalendars />);
    await ready();

    await userEvent.click(screen.getByLabelText("Saturday, Maria Ionescu"));
    await userEvent.click(screen.getByRole("button", { name: "Save the calendar for Maria Ionescu" }));

    await waitFor(() =>
      expect(setCalendar).toHaveBeenCalledWith(
        "token",
        "subject-maria",
        ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"],
        8,
      ),
    );
  });

  it("refuses an empty week, because that rule is a deletion", async () => {
    // `journey.calendar.noWorkingDays` says it in words: to stop planning for a rep you remove their
    // calendar. A calendar with no days would be a rep who is configured to be planned for nothing,
    // which reads on a plan exactly like a bug.
    render(<WorkingCalendars />);
    await ready();

    for (const day of ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday"]) {
      await userEvent.click(screen.getByLabelText(`${day}, Maria Ionescu`));
    }

    expect(
      await screen.findByText(/works at least one day\. To stop planning for them/),
    ).toBeTruthy();

    expect(
      (screen.getByRole("button", { name: "Save the calendar for Maria Ionescu" }) as HTMLButtonElement)
        .disabled,
    ).toBe(true);

    expect(setCalendar).not.toHaveBeenCalled();
  });

  it("refuses a capacity outside what a day can hold", async () => {
    render(<WorkingCalendars />);
    await ready();

    const capacity = screen.getByLabelText("Calls a day, Maria Ionescu");
    await userEvent.clear(capacity);
    await userEvent.type(capacity, "80");

    expect(capacity.getAttribute("aria-invalid")).toBe("true");
    expect(setCalendar).not.toHaveBeenCalled();
  });

  it("offers a calendar only to a rep who has none, and never to a deactivated one", async () => {
    render(<WorkingCalendars />);
    await ready();

    await userEvent.click(screen.getByRole("button", { name: "Add a calendar" }));

    const picker = (await screen.findByLabelText("Rep")) as HTMLSelectElement;
    // By subject rather than by label: since W11½ R3 the option reads "Andrei Pop — andrei@…", and
    // the two exclusions below would otherwise hold for the wrong reason — no option's text is a
    // bare name any more, offered or not.
    const offered = [...picker.options].map((option) => option.value);

    expect(offered).toEqual(["subject-andrei"]);

    // Maria already has one — offering her would be an edit disguised as a create. The deactivated
    // rep is refused by the server, and offering a choice only to take it back is worse than not
    // offering it.
    expect(offered).not.toContain("subject-maria");
    expect(offered).not.toContain("subject-gone");

    // The label still carries the name, which is what a person is picked by.
    expect(picker.options[0].textContent).toContain("Andrei Pop");
  });

  it("writes nothing until a new calendar is saved", async () => {
    render(<WorkingCalendars />);
    await ready();

    await userEvent.click(screen.getByRole("button", { name: "Add a calendar" }));
    await screen.findByLabelText("Rep");

    expect(setCalendar).not.toHaveBeenCalled();

    await userEvent.click(screen.getByRole("button", { name: "Save the calendar for Andrei Pop" }));

    await waitFor(() =>
      expect(setCalendar).toHaveBeenCalledWith(
        "token",
        "subject-andrei",
        ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday"],
        8,
      ),
    );
  });

  it("says a rep with no calendar is unplanned rather than unavailable", async () => {
    // The distinction the API refuses to collapse: there is no "works no days", so an empty screen
    // means nobody is planned for — not that everybody is free.
    fetchCalendars.mockResolvedValue([]);

    render(<WorkingCalendars />);

    expect(await screen.findByText(/Nobody is planned for until one does/)).toBeTruthy();
  });

  it("removes a calendar rather than emptying it", async () => {
    render(<WorkingCalendars />);
    await ready();

    await userEvent.click(
      screen.getByRole("button", { name: "Remove the calendar for Maria Ionescu" }),
    );

    await waitFor(() => expect(deleteCalendar).toHaveBeenCalledWith("token", "subject-maria"));
    expect(setCalendar).not.toHaveBeenCalled();
  });

  it("adds a holiday, and says a repeated date is taken before the server does", async () => {
    render(<WorkingCalendars />);
    await ready();

    const date = screen.getByLabelText("Date");
    await userEvent.type(date, "2026-12-25");

    expect(await screen.findByText("That date is already a holiday.")).toBeTruthy();
    expect(
      (screen.getByRole("button", { name: "Add a holiday" }) as HTMLButtonElement).disabled,
    ).toBe(true);

    await userEvent.clear(date);
    await userEvent.type(date, "2026-12-26");
    await userEvent.type(screen.getByLabelText("Name"), "  Boxing Day  ");
    await userEvent.click(screen.getByRole("button", { name: "Add a holiday" }));

    await waitFor(() =>
      expect(addHoliday).toHaveBeenCalledWith("token", "2026-12-26", "Boxing Day"),
    );
  });

  it("shows the server's refusal in the reader's language", async () => {
    setCalendar.mockRejectedValue(
      new ApiError(400, [
        {
          field: "visitsPerDay",
          message: "A day holds between 1 and 50 calls.",
          code: "journey.calendar.capacityOutOfRange",
          args: { max: "50" },
        },
      ]),
    );

    render(<WorkingCalendars />);
    await ready();

    await userEvent.click(screen.getByLabelText("Saturday, Maria Ionescu"));
    await userEvent.click(screen.getByRole("button", { name: "Save the calendar for Maria Ionescu" }));

    expect(await screen.findByRole("alert")).toBeTruthy();
    expect(screen.getByRole("alert").textContent).toMatch(/between 1 and 50 calls/);
  });

  it("still works for a caller who cannot read the user directory", async () => {
    // `user:read` is a separate permission, and the calendar list carries its own display names. A
    // caller without it sees every calendar and simply cannot create one for somebody new.
    fetchUsers.mockRejectedValue(new ApiError(403));
    signedIn(["journey:read", "journey:write"]);

    render(<WorkingCalendars />);
    await ready();

    expect(screen.getByText("Maria Ionescu")).toBeTruthy();

    await waitFor(() =>
      expect(screen.queryByRole("button", { name: "Add a calendar" })).toBeNull(),
    );
  });

  it("shows a reader the calendars and none of the controls", async () => {
    signedIn(["journey:read", "user:read"]);

    render(<WorkingCalendars />);

    expect(await screen.findByText("Maria Ionescu")).toBeTruthy();
    expect((screen.getByLabelText("Monday, Maria Ionescu") as HTMLInputElement).disabled).toBe(true);
    expect((screen.getByLabelText("Calls a day, Maria Ionescu") as HTMLInputElement).disabled).toBe(
      true,
    );

    await waitFor(() => expect(screen.queryByLabelText("Date")).toBeNull());

    expect(screen.queryByRole("button", { name: "Add a holiday" })).toBeNull();
    expect(
      screen.queryByRole("button", { name: "Remove the calendar for Maria Ionescu" }),
    ).toBeNull();
  });
});
