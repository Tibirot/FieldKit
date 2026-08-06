// @vitest-environment jsdom

import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";

import type { AuthContextValue } from "@/components/auth-provider";
import { OutletLifecycle } from "@/components/back-office/outlet-lifecycle";
import { ApiError } from "@/lib/api/client";
import { fetchIdentity } from "@/lib/api/identity";
import type { OutletDetail, OutletStatus, OutletStatusChange } from "@/lib/api/outlets";
import { render } from "@/test/render";

const auth = vi.hoisted(() => ({ current: {} as AuthContextValue }));
const changeOutletStatus = vi.hoisted(() => vi.fn());
const fetchOutletStatusHistory = vi.hoisted(() => vi.fn());

vi.mock("@/components/auth-provider", () => ({ useAuth: () => auth.current }));

vi.mock("@/lib/api/outlets", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/lib/api/outlets")>()),
  changeOutletStatus: (...args: unknown[]) => changeOutletStatus(...args),
  fetchOutletStatusHistory: (...args: unknown[]) => fetchOutletStatusHistory(...args),
}));

function outlet(status: OutletStatus): OutletDetail {
  return {
    id: "o-1",
    code: "OUT-1",
    name: "Corner Shop",
    channelId: "c-mt",
    channelName: "Modern Trade",
    segment: null,
    banner: null,
    status,
    territory: null,
    timeZoneId: "Europe/Bucharest",
    address: null,
    location: null,
    contacts: [],
    customFields: {},
  };
}

const HISTORY: OutletStatusChange[] = [
  {
    from: "Active",
    to: "Inactive",
    reason: "Refurbishment until March",
    changedAtUtc: "2026-02-01T09:15:00Z",
    changedBy: "ana",
  },
  {
    from: null,
    to: "Active",
    reason: null,
    changedAtUtc: "2025-11-04T08:00:00Z",
    changedBy: "import",
  },
];

describe("<OutletLifecycle>", () => {
  beforeEach(() => {
    changeOutletStatus.mockReset().mockResolvedValue(outlet("Inactive"));
    fetchOutletStatusHistory.mockReset().mockResolvedValue(HISTORY);

    auth.current = {
      status: "authenticated",
      user: { access_token: "token", profile: { sub: "subject-a" } },
      workspace: "fieldkit-dev",
      signIn: vi.fn(),
      signOut: vi.fn(),
      completeSignIn: vi.fn(),
    } as unknown as AuthContextValue;
  });

  it("never offers the status the outlet already holds", async () => {
    // Asking for what is already true is accepted by the API as idempotent, so it would fail
    // silently rather than loudly — the option simply should not be there.
    render(<OutletLifecycle outlet={outlet("Active")} />);
    await screen.findAllByRole("listitem");

    const options = [...(screen.getByLabelText("Move to") as HTMLSelectElement).options];

    expect(options.map((option) => option.value)).toEqual(["Inactive", "Closed"]);
  });

  it("moves an outlet to Inactive without demanding a reason", async () => {
    // Required only for the irreversible one. Demanding it for a routine toggle buys a column of ".".
    render(<OutletLifecycle outlet={outlet("Active")} />);
    await screen.findAllByRole("listitem");

    await userEvent.click(screen.getByRole("button", { name: "Change status" }));

    await waitFor(() => expect(changeOutletStatus).toHaveBeenCalled());

    expect(changeOutletStatus).toHaveBeenCalledWith("token", "o-1", {
      status: "Inactive",
      reason: null,
    });
  });

  it("sends a reason that was given, trimmed", async () => {
    // The server treats whitespace as absent when deciding whether a close was explained, so a
    // client that sends "   " gets a refusal it cannot explain.
    render(<OutletLifecycle outlet={outlet("Active")} />);
    await screen.findAllByRole("listitem");

    await userEvent.selectOptions(screen.getByLabelText("Move to"), "Closed");
    await userEvent.type(screen.getByLabelText(/^reason/i), "  Lease ended  ");
    await userEvent.click(screen.getByRole("button", { name: "Change status" }));

    await waitFor(() => expect(changeOutletStatus).toHaveBeenCalled());

    expect(changeOutletStatus).toHaveBeenCalledWith("token", "o-1", {
      status: "Closed",
      reason: "Lease ended",
    });
  });

  it("does not keep pointing at a status that has stopped being on offer", async () => {
    // The panel stays mounted across a successful change — it is keyed by outlet id, and the id does
    // not move. So an Active outlet whose user picked Inactive, and which then *becomes* Inactive,
    // is a select bound to a value its own option list no longer contains: the browser falls back to
    // showing the first option while React still holds the old one, and pressing the button sends a
    // status different from the one on screen. Silently, and as a no-op the API accepts.
    const { rerender } = render(<OutletLifecycle outlet={outlet("Active")} />);
    await screen.findAllByRole("listitem");

    await userEvent.selectOptions(screen.getByLabelText("Move to"), "Inactive");

    rerender(<OutletLifecycle outlet={outlet("Inactive")} />);

    const select = screen.getByLabelText("Move to") as HTMLSelectElement;

    expect([...select.options].map((option) => option.value)).toEqual(["Active", "Closed"]);
    expect(select.value).toBe("Active");

    await userEvent.click(screen.getByRole("button", { name: "Change status" }));

    await waitFor(() => expect(changeOutletStatus).toHaveBeenCalled());

    // What the screen says, not what the state remembered.
    expect(changeOutletStatus).toHaveBeenCalledWith("token", "o-1", {
      status: "Active",
      reason: null,
    });
  });

  it("warns what closing costs, and only when closing is what is selected", async () => {
    render(<OutletLifecycle outlet={outlet("Active")} />);
    await screen.findAllByRole("listitem");

    expect(screen.queryByText(/cannot be reopened/)).toBeNull();

    await userEvent.selectOptions(screen.getByLabelText("Move to"), "Closed");

    expect(screen.getByText(/cannot be reopened/)).toBeTruthy();
  });

  it("puts a missing reason under the reason box", async () => {
    changeOutletStatus.mockRejectedValue(
      new ApiError(400, [
        { field: "reason", message: "Closing an outlet permanently requires a reason." },
      ]),
    );

    render(<OutletLifecycle outlet={outlet("Active")} />);
    await screen.findAllByRole("listitem");

    await userEvent.selectOptions(screen.getByLabelText("Move to"), "Closed");
    await userEvent.click(screen.getByRole("button", { name: "Change status" }));

    const message = await screen.findByText(/requires a reason/);

    expect(screen.getByLabelText(/^reason/i).getAttribute("aria-describedby")).toBe(message.id);
  });

  it("offers a closed outlet no way forward, and says what to do instead", async () => {
    // `Closed` is terminal. A select still listing Active would be a door that does not open, and
    // the API would refuse it — the useful answer is the spec's: create a new outlet.
    render(<OutletLifecycle outlet={outlet("Closed")} />);
    await screen.findAllByRole("listitem");

    expect(screen.queryByLabelText("Move to")).toBeNull();
    expect(screen.queryByRole("button", { name: "Change status" })).toBeNull();
    expect(screen.getByText(/create a new outlet with its own code/)).toBeTruthy();
  });

  it("reads the first entry as a creation rather than a transition", async () => {
    // `from` is null there. Rendering it as "→ Active" would invent a previous state the shop
    // never had, and the entry exists so that "no history" cannot be read as "history was lost".
    render(<OutletLifecycle outlet={outlet("Inactive")} />);

    const entries = await screen.findAllByRole("listitem");

    expect(entries[0].textContent).toContain("Active → Inactive");
    expect(entries[0].textContent).toContain("Refurbishment until March");
    expect(entries[1].textContent).toContain("Created as Active");
    expect(entries[1].textContent).not.toContain("→");

    // Rendered in the app's zone, not the machine's. `vitest.config.ts` runs this suite in
    // Europe/Bucharest, where 08:00Z is 10:00 — so this assertion fails if the formatter ever
    // reaches for the system zone, which is how the last date bug got in.
    expect(entries[1].textContent).toContain("8:00 AM");
  });

  it("shows the trail to a caller who may not change anything", async () => {
    // Reading why a shop was closed is not the same authority as closing one. The history is the
    // answer to "why can't I order for this outlet", and hiding it helps nobody.
    vi.mocked(fetchIdentity).mockResolvedValue({
      subject: "subject-a",
      tenant: "fieldkit-dev",
      permissions: ["outlet:read"],
    });

    render(<OutletLifecycle outlet={outlet("Active")} />);
    await screen.findAllByRole("listitem");

    expect(screen.queryByRole("button", { name: "Change status" })).toBeNull();
    expect(screen.getByText(/Refurbishment until March/)).toBeTruthy();
  });
});
