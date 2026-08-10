// @vitest-environment jsdom

import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";

import type { AuthContextValue } from "@/components/auth-provider";
import { CallFrequencies } from "@/components/back-office/call-frequencies";
import { ApiError } from "@/lib/api/client";
import { fetchIdentity } from "@/lib/api/identity";
import type { OutletFrequency, SegmentFrequency } from "@/lib/api/journeys";
import { render } from "@/test/render";

const auth = vi.hoisted(() => ({ current: {} as AuthContextValue }));
const fetchSegmentFrequencies = vi.hoisted(() => vi.fn());
const fetchOutletFrequencies = vi.hoisted(() => vi.fn());
const setSegmentFrequency = vi.hoisted(() => vi.fn());
const setOutletFrequency = vi.hoisted(() => vi.fn());
const deleteSegmentFrequency = vi.hoisted(() => vi.fn());
const deleteOutletFrequency = vi.hoisted(() => vi.fn());
const fetchOutlet = vi.hoisted(() => vi.fn());
const fetchOutlets = vi.hoisted(() => vi.fn());

vi.mock("@/components/auth-provider", () => ({ useAuth: () => auth.current }));

vi.mock("@/lib/api/journeys", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/lib/api/journeys")>()),
  fetchSegmentFrequencies: (...args: unknown[]) => fetchSegmentFrequencies(...args),
  fetchOutletFrequencies: (...args: unknown[]) => fetchOutletFrequencies(...args),
  setSegmentFrequency: (...args: unknown[]) => setSegmentFrequency(...args),
  setOutletFrequency: (...args: unknown[]) => setOutletFrequency(...args),
  deleteSegmentFrequency: (...args: unknown[]) => deleteSegmentFrequency(...args),
  deleteOutletFrequency: (...args: unknown[]) => deleteOutletFrequency(...args),
}));

vi.mock("@/lib/api/outlets", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/lib/api/outlets")>()),
  fetchOutlet: (...args: unknown[]) => fetchOutlet(...args),
  fetchOutlets: (...args: unknown[]) => fetchOutlets(...args),
}));

const WEEKLY: SegmentFrequency = { segment: "A", visitsPerCycle: 1, cycleLengthDays: 7 };
const TWICE: SegmentFrequency = { segment: "B", visitsPerCycle: 2, cycleLengthDays: 14 };

const OVERRIDE: OutletFrequency = {
  outletId: "outlet-1",
  visitsPerCycle: 4,
  cycleLengthDays: 28,
};

const CORNER = { id: "outlet-1", code: "RO-BUC-0001", name: "Corner Shop" };

/** Waits for the permission answer — the write controls only exist once it has arrived. */
async function ready(): Promise<void> {
  await screen.findByRole("button", { name: "Add a segment rule" });
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

describe("<CallFrequencies>", () => {
  beforeEach(() => {
    fetchSegmentFrequencies.mockReset().mockResolvedValue([WEEKLY, TWICE]);
    fetchOutletFrequencies.mockReset().mockResolvedValue([OVERRIDE]);
    setSegmentFrequency.mockReset().mockResolvedValue(WEEKLY);
    setOutletFrequency.mockReset().mockResolvedValue(OVERRIDE);
    deleteSegmentFrequency.mockReset().mockResolvedValue(undefined);
    deleteOutletFrequency.mockReset().mockResolvedValue(undefined);
    fetchOutlet.mockReset().mockResolvedValue(CORNER);
    fetchOutlets.mockReset().mockResolvedValue({ items: [], total: 0 });

    signedIn(["journey:read", "journey:write", "outlet:read"]);
  });

  it("shows both numbers, because a frequency is a pair and not a period", async () => {
    render(<CallFrequencies />);
    await ready();

    expect((screen.getByLabelText("Visits per cycle, B") as HTMLInputElement).value).toBe("2");
    expect((screen.getByLabelText("Cycle length in days, B") as HTMLInputElement).value).toBe("14");
  });

  it("saves one rule at a time, keyed by its segment", async () => {
    // Each row is its own PUT. A save-everything button would make one bad row refuse a screenful
    // of good ones, and the API's per-key idempotence is what makes retrying a single row safe.
    render(<CallFrequencies />);
    await ready();

    const visits = screen.getByLabelText("Visits per cycle, A");
    await userEvent.clear(visits);
    await userEvent.type(visits, "3");

    await userEvent.click(screen.getByRole("button", { name: "Save the rule for segment A" }));

    await waitFor(() =>
      expect(setSegmentFrequency).toHaveBeenCalledWith("token", "A", {
        visitsPerCycle: 3,
        cycleLengthDays: 7,
      }),
    );

    // And nothing else was written. The other segment was on screen and untouched.
    expect(setSegmentFrequency).toHaveBeenCalledTimes(1);
  });

  it("will not save a rule that has not changed", async () => {
    render(<CallFrequencies />);
    await ready();

    const save = screen.getByRole("button", { name: "Save the rule for segment A" });

    expect((save as HTMLButtonElement).disabled).toBe(true);
  });

  it("refuses a cycle longer than a year without asking the server", async () => {
    // The server refuses it too, and is the authority. Saying so here means the message lands beside
    // the field rather than as a refusal about the whole rule.
    render(<CallFrequencies />);
    await ready();

    const cycle = screen.getByLabelText("Cycle length in days, A");
    await userEvent.clear(cycle);
    await userEvent.type(cycle, "400");

    expect(cycle.getAttribute("aria-invalid")).toBe("true");
    expect(
      (screen.getByRole("button", { name: "Save the rule for segment A" }) as HTMLButtonElement)
        .disabled,
    ).toBe(true);
    expect(setSegmentFrequency).not.toHaveBeenCalled();
  });

  it("refuses zero visits, because that rule is a deletion", async () => {
    // `journey.frequency.visitsTooFew` says it in words: to stop visiting a shop you remove the
    // rule, and a frequency of zero would be a rule that plans nothing while looking like a plan.
    render(<CallFrequencies />);
    await ready();

    const visits = screen.getByLabelText("Visits per cycle, A");
    await userEvent.clear(visits);
    await userEvent.type(visits, "0");

    expect(visits.getAttribute("aria-invalid")).toBe("true");
    expect(setSegmentFrequency).not.toHaveBeenCalled();
  });

  it("says a new segment collides before the server does", async () => {
    render(<CallFrequencies />);
    await ready();

    await userEvent.click(screen.getByRole("button", { name: "Add a segment rule" }));
    await userEvent.type(screen.getByLabelText("Segment"), "a");

    // Case-insensitive, like the labels themselves — "a" and "A" are the same segment.
    expect(await screen.findByText("That segment already has a rule.")).toBeTruthy();
    expect(setSegmentFrequency).not.toHaveBeenCalled();
  });

  it("clears a new rule's row once it is saved", async () => {
    // Regression, found in the browser rather than here: the draft was identified by the segment
    // typed into it, and a draft starts empty — so after saving, nothing matched the name it had
    // just been given. The row stayed on screen next to the rule it had become, announcing that the
    // segment was already taken. Every assertion in this file passed while that was true.
    fetchSegmentFrequencies.mockResolvedValueOnce([]).mockResolvedValue([
      { segment: "C", visitsPerCycle: 1, cycleLengthDays: 7 },
    ]);

    render(<CallFrequencies />);
    await ready();

    await userEvent.click(screen.getByRole("button", { name: "Add a segment rule" }));
    await userEvent.type(screen.getByLabelText("Segment"), "C");
    await userEvent.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => expect(setSegmentFrequency).toHaveBeenCalled());

    // The draft's own field is what disappears — the stored row has no segment input.
    await waitFor(() => expect(screen.queryByLabelText("Segment")).toBeNull());
    expect(screen.queryByText("That segment already has a rule.")).toBeNull();
  });

  it("names the shop behind an override rather than its id", async () => {
    render(<CallFrequencies />);
    await ready();

    expect(await screen.findByText("Corner Shop")).toBeTruthy();
    expect(screen.queryByText("outlet-1")).toBeNull();
  });

  it("shows the shop's code, because a name is not an identifier", async () => {
    // Two shops, one name, different codes — which is ordinary in FMCG and is what this screen
    // could not represent. The row said "Mega Image Dorobanți" twice and so did both Save buttons,
    // so neither a reader nor a screen reader could tell which rule they were editing.
    fetchOutletFrequencies.mockResolvedValue([
      OVERRIDE,
      { outletId: "outlet-2", visitsPerCycle: 2, cycleLengthDays: 7 },
    ]);
    fetchOutlet.mockImplementation((_token: string, id: string) =>
      Promise.resolve(
        id === "outlet-1"
          ? { id: "outlet-1", code: "RO-BUC-0001", name: "Mega Image Dorobanți" }
          : { id: "outlet-2", code: "RO-BUC-0009", name: "Mega Image Dorobanți" },
      ),
    );

    render(<CallFrequencies />);
    await ready();

    expect(await screen.findByText("RO-BUC-0001")).toBeTruthy();
    expect(screen.getByText("RO-BUC-0009")).toBeTruthy();

    // The controls too. `getByRole` throws on more than one match, so these two calls are the
    // assertion: before the code was in the accessible name, both of them found two buttons.
    expect(
      screen.getByRole("button", {
        name: "Save the override for Mega Image Dorobanți (RO-BUC-0001)",
      }),
    ).toBeTruthy();
    expect(
      screen.getByRole("button", {
        name: "Remove the override for Mega Image Dorobanți (RO-BUC-0009)",
      }),
    ).toBeTruthy();
  });

  it("keeps an override whose shop could not be read", async () => {
    // The rule exists server-side whether or not this screen could name the shop. Dropping the row
    // would hide a rule that is still planning visits.
    fetchOutlet.mockRejectedValue(new ApiError(404));

    render(<CallFrequencies />);
    await ready();

    expect(await screen.findByText("Unknown shop")).toBeTruthy();

    // Carrying its id where its code would be, rather than a blank: the row has to stay
    // distinguishable from the next unreadable one.
    expect(screen.getByText("outlet-1")).toBeTruthy();
  });

  it("removes an override rather than copying the segment's numbers into it", async () => {
    // The two look identical today and diverge the moment the segment's rule changes. Only one of
    // them means "this shop follows its segment".
    render(<CallFrequencies />);
    await ready();

    await userEvent.click(
      await screen.findByRole("button", { name: "Remove the override for Corner Shop (RO-BUC-0001)" }),
    );

    await waitFor(() => expect(deleteOutletFrequency).toHaveBeenCalledWith("token", "outlet-1"));
    expect(setOutletFrequency).not.toHaveBeenCalled();
  });

  it("shows the server's refusal in the reader's language", async () => {
    setSegmentFrequency.mockRejectedValue(
      new ApiError(
        400,
        [
          {
            field: "cycleLengthDays",
            message: "A cycle is between 1 and 365 days.",
            code: "journey.frequency.cycleOutOfRange",
            args: { max: "365" },
          },
        ],
      ),
    );

    render(<CallFrequencies />);
    await ready();

    const visits = screen.getByLabelText("Visits per cycle, A");
    await userEvent.clear(visits);
    await userEvent.type(visits, "3");
    await userEvent.click(screen.getByRole("button", { name: "Save the rule for segment A" }));

    expect(await screen.findByRole("alert")).toBeTruthy();
    expect(screen.getByRole("alert").textContent).toMatch(/between 1 and 365 days/);
  });

  it("shows a reader the rules and none of the controls", async () => {
    // journey:read without journey:write. The screen is worth opening — "why is this shop planned
    // four times a month" is a question a reader has — and every control that would refuse is gone.
    signedIn(["journey:read", "outlet:read"]);

    render(<CallFrequencies />);

    expect(await screen.findByText("By segment")).toBeTruthy();
    expect((screen.getByLabelText("Visits per cycle, A") as HTMLInputElement).disabled).toBe(true);

    await waitFor(() =>
      expect(screen.queryByRole("button", { name: "Add a segment rule" })).toBeNull(),
    );

    expect(screen.queryByRole("button", { name: "Save the rule for segment A" })).toBeNull();

    // The picker's search box, which is an input rather than a button — the shape of this assertion
    // matters, because querying for a button here would pass whether or not the picker rendered.
    expect(screen.queryByLabelText("Find a shop")).toBeNull();
  });

  it("adds a shop as an unsaved row rather than writing a rule nobody chose", async () => {
    // An override created on the spot would need numbers, and any number this screen picked would
    // be a rule an admin did not write. So adding is a draft until its numbers are saved.
    fetchOutlets.mockResolvedValue({
      items: [{ id: "outlet-2", code: "RO-BUC-0002", name: "Kiosk 1 Mai" }],
      total: 1,
    });

    render(<CallFrequencies />);
    await ready();

    await userEvent.type(screen.getByLabelText("Find a shop"), "kiosk");
    await userEvent.click(
      await screen.findByRole("button", { name: "Add an override for Kiosk 1 Mai (RO-BUC-0002)" }),
    );

    expect(await screen.findByText("Not saved yet")).toBeTruthy();
    expect(setOutletFrequency).not.toHaveBeenCalled();

    // Exactly one control discards it. The picker drew a chip for it at first — so the shop was on
    // screen three times (chip, search result, row) with two buttons that both said "discard",
    // attached to different pieces of state. Found in the browser.
    expect(
      screen.getAllByRole("button", { name: "Discard the override for Kiosk 1 Mai (RO-BUC-0002)" }),
    ).toHaveLength(1);

    // And adding it again from the search does not open a second row.
    await userEvent.click(screen.getByRole("button", { name: "Add an override for Kiosk 1 Mai (RO-BUC-0002)" }));

    expect(
      screen.getAllByRole("button", { name: "Discard the override for Kiosk 1 Mai (RO-BUC-0002)" }),
    ).toHaveLength(1);

    await userEvent.click(
      screen.getByRole("button", { name: "Save the override for Kiosk 1 Mai (RO-BUC-0002)" }),
    );

    await waitFor(() =>
      expect(setOutletFrequency).toHaveBeenCalledWith("token", "outlet-2", {
        visitsPerCycle: 1,
        cycleLengthDays: 7,
      }),
    );
  });
});
