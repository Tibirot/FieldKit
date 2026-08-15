// @vitest-environment jsdom

import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";

import type { AuthContextValue } from "@/components/auth-provider";
import { VisitList } from "@/components/back-office/visit-list";
import { ApiError } from "@/lib/api/client";
import { fetchIdentity } from "@/lib/api/identity";
import type { Visit } from "@/lib/api/visits";
import { render } from "@/test/render";

const auth = vi.hoisted(() => ({ current: {} as AuthContextValue }));
const fetchVisits = vi.hoisted(() => vi.fn());
const fetchOutlets = vi.hoisted(() => vi.fn());
const fetchUsers = vi.hoisted(() => vi.fn());

vi.mock("@/components/auth-provider", () => ({ useAuth: () => auth.current }));

vi.mock("@/lib/api/visits", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/lib/api/visits")>()),
  fetchVisits: (...args: unknown[]) => fetchVisits(...args),
}));

vi.mock("@/lib/api/outlets", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/lib/api/outlets")>()),
  fetchOutlets: (...args: unknown[]) => fetchOutlets(...args),
}));

vi.mock("@/lib/api/users", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/lib/api/users")>()),
  fetchUsers: (...args: unknown[]) => fetchUsers(...args),
}));

/** An ordinary visit: inside the geofence, worked online, productive. */
const ORDINARY: Visit = {
  id: "visit-1",
  outletId: "outlet-1",
  userId: "subject-maria",
  plannedVisitId: null,
  status: "CheckedOut",
  checkedInAtUtc: "2026-09-03T09:30:00Z",
  checkInLatitude: 44.4638,
  checkInLongitude: 26.0946,
  checkInDistanceMetres: 12,
  wasInsideGeofence: true,
  geofenceOverrideReason: null,
  checkedOutAtUtc: "2026-09-03T09:52:00Z",
  checkOutLatitude: 44.4638,
  checkOutLongitude: 26.0946,
  outcome: "Productive",
  outcomeReason: null,
  timeOnSiteSeconds: 1320,
  source: "Live",
  recordedAtUtc: "2026-09-03T09:52:00Z",
};

const OUTLETS = {
  items: [
    { id: "outlet-1", code: "OUT-1", name: "Corner Shop", channelId: "c1", channelName: "TT", segment: null, banner: null, status: "Active", territory: null },
    { id: "outlet-2", code: "OUT-2", name: "Big Store", channelId: "c1", channelName: "TT", segment: null, banner: null, status: "Active", territory: null },
  ],
  total: 2,
};

const USERS = [
  { id: "u1", subjectId: "subject-maria", email: "maria@fieldkit.local", displayName: "Maria Ionescu", locale: "ro-RO", timeZone: "Europe/Bucharest", isActive: true, roleIds: [] },
];

function allow(...permissions: string[]) {
  vi.mocked(fetchIdentity).mockResolvedValue({
    subject: "subject-a",
    tenant: "fieldkit-dev",
    permissions,
  });
}

describe("<VisitList>", () => {
  beforeEach(() => {
    vi.clearAllMocks();

    auth.current = {
      status: "authenticated",
      user: { access_token: "token", profile: { sub: "subject-a" } },
    } as unknown as AuthContextValue;

    fetchVisits.mockResolvedValue([ORDINARY]);
    fetchOutlets.mockResolvedValue(OUTLETS);
    fetchUsers.mockResolvedValue(USERS);
    allow("visit:read");
  });

  it("names the shop and the rep rather than showing their ids", async () => {
    // The ids are what the API returns and what nobody can read. A row a supervisor cannot scan is
    // a row they will not use.
    render(<VisitList />);

    // Scoped to the list, because the outlet filter's options carry the same names — an unscoped
    // `findByText(/Corner Shop/)` matches the `<option>` too and throws on the ambiguity.
    const row = within(await screen.findByRole("list")).getByText(/Corner Shop/).closest("li")!;

    // `identifying` renders "name — email", which is how two people called Maria stay apart. The
    // assertion is on the name so it does not pin that formatting decision to this screen.
    expect(within(row).getByText(/Maria Ionescu/)).toBeTruthy();
    expect(within(row).getByText("Productive")).toBeTruthy();
    expect(row.textContent).not.toContain("subject-maria");
    expect(row.textContent).not.toContain("outlet-1");
  });

  it("shows the reason a rep gave for being away from the shop", async () => {
    /*
     * `BR-VIS-2` never blocks a rep who is elsewhere — it asks for a sentence and records it. That
     * sentence is the whole point of the rule: without it, the visit is indistinguishable from one
     * worked at the counter, and the rule would have cost the rep an interruption for nothing.
     */
    fetchVisits.mockResolvedValue([
      { ...ORDINARY, wasInsideGeofence: false, geofenceOverrideReason: "Van broke down on the ring road" },
    ]);

    render(<VisitList />);

    expect(await screen.findByText(/Van broke down on the ring road/)).toBeTruthy();
  });

  it("says so when a rep was away and gave no reason", async () => {
    // Reachable for a visit ingested from a device: the reason is validated where a rep can still
    // answer, and a pushed visit is past that point. Rendering nothing would make it look ordinary.
    fetchVisits.mockResolvedValue([
      { ...ORDINARY, wasInsideGeofence: false, geofenceOverrideReason: null },
    ]);

    render(<VisitList />);

    expect(await screen.findByText(/no reason given/)).toBeTruthy();
  });

  it("distinguishes the day the work happened from the day it arrived", async () => {
    // A visit captured offline on Tuesday and drained on Friday is a record of Tuesday. Both dates
    // are shown, because "why is this only appearing now" is a question the list should answer
    // rather than provoke.
    fetchVisits.mockResolvedValue([
      { ...ORDINARY, source: "Device", checkedInAtUtc: "2026-09-01T09:30:00Z", recordedAtUtc: "2026-09-04T18:02:00Z" },
    ]);

    render(<VisitList />);

    expect(await screen.findByText(/Captured offline/)).toBeTruthy();
  });

  it("says nothing about arrival for a visit worked online", async () => {
    /*
     * The other half of the one above, and it needs its own test rather than a second `render` in
     * that one: two renders share a document, so the first row's line is still in the DOM when the
     * second is asserted absent. A negative assertion that cannot fail is worse than none.
     */
    render(<VisitList />);

    await screen.findByRole("list");

    expect(screen.queryByText(/Captured offline/)).toBeNull();
  });

  it("calls an open visit open rather than unproductive", async () => {
    // A rep standing in the shop has not failed. The same distinction the dashboard keeps out of
    // the strike rate.
    fetchVisits.mockResolvedValue([{ ...ORDINARY, status: "CheckedIn", outcome: null, checkedOutAtUtc: null }]);

    render(<VisitList />);

    expect(await screen.findByText("Still open")).toBeTruthy();
  });

  it("asks again for one shop when a reader filters", async () => {
    render(<VisitList />);

    const selector = await screen.findByLabelText("Outlet");

    await userEvent.selectOptions(selector, "outlet-2");

    await waitFor(() =>
      expect(fetchVisits).toHaveBeenCalledWith(
        "token",
        expect.objectContaining({ outletId: "outlet-2" }),
        expect.anything(),
      ));
  });

  it("says when there is nothing to show", async () => {
    fetchVisits.mockResolvedValue([]);

    render(<VisitList />);

    expect(await screen.findByText(/No visits recorded yet/)).toBeTruthy();
  });

  it("says which refusal it met", async () => {
    fetchVisits.mockRejectedValue(new ApiError(403, "Forbidden"));

    render(<VisitList />);

    expect((await screen.findByRole("alert")).textContent).toMatch(/do not have permission/);
  });
});
