// @vitest-environment jsdom

import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";

import type { AuthContextValue } from "@/components/auth-provider";
import { Dashboard } from "@/components/back-office/dashboard";
import { ApiError } from "@/lib/api/client";
import { fetchIdentity } from "@/lib/api/identity";
import type { ReportingSummary } from "@/lib/api/reporting";
import { render } from "@/test/render";

const auth = vi.hoisted(() => ({ current: {} as AuthContextValue }));
const fetchSummary = vi.hoisted(() => vi.fn());
const fetchTerritories = vi.hoisted(() => vi.fn());

vi.mock("@/components/auth-provider", () => ({ useAuth: () => auth.current }));

vi.mock("@/lib/api/reporting", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/lib/api/reporting")>()),
  fetchSummary: (...args: unknown[]) => fetchSummary(...args),
}));

vi.mock("@/lib/api/org", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/lib/api/org")>()),
  fetchTerritories: (...args: unknown[]) => fetchTerritories(...args),
}));

/** A month with work in it, at two shops. */
const BUSY: ReportingSummary = {
  from: "2026-09-01",
  to: "2026-09-30",
  territoryId: null,
  outlets: 2,
  coverage: { planned: 8, notVisited: 1, made: 6, percentage: 75 },
  visits: { productive: 6, nonProductive: 2, open: 1, strikeRate: 75 },
  perfectStore: {
    audits: 5,
    scored: 4,
    averageScore: 82.5,
    comparable: true,
    weightSetVersions: [3],
    pillars: [
      { pillar: "Availability", average: 90, measured: 4, skipped: 0 },
      { pillar: "ShareOfShelf", average: 96, measured: 1, skipped: 3 },
    ],
  },
  orders: {
    orders: 4,
    lines: 9,
    linesPerOrder: 2.25,
    rejected: 1,
    cancelled: 0,
    priceDisagreements: 0,
    value: [{ currencyCode: "RON", net: 1240.5, tax: 235.7, gross: 1476.2, orders: 4 }],
  },
};

/** The state a fresh tenant meets: shops in scope, and nothing done yet. */
const QUIET: ReportingSummary = {
  ...BUSY,
  coverage: { planned: 0, notVisited: 0, made: 0, percentage: null },
  visits: { productive: 0, nonProductive: 0, open: 0, strikeRate: null },
  perfectStore: { audits: 0, scored: 0, averageScore: null, comparable: true, weightSetVersions: [], pillars: [] },
  orders: { orders: 0, lines: 0, linesPerOrder: null, rejected: 0, cancelled: 0, priceDisagreements: 0, value: [] },
};

/** Signs the caller in with exactly these permissions. */
function allow(...permissions: string[]) {
  vi.mocked(fetchIdentity).mockResolvedValue({
    subject: "subject-a",
    tenant: "fieldkit-dev",
    permissions,
  });
}

describe("<Dashboard>", () => {
  beforeEach(() => {
    // Call history, not just return values. Without this, "did not fetch territories" passes or
    // fails depending on which tests ran before it — which is how a negative assertion about a mock
    // becomes a test of the file's running order.
    vi.clearAllMocks();

    auth.current = {
      status: "authenticated",
      user: { access_token: "token", profile: { sub: "subject-a" } },
    } as unknown as AuthContextValue;

    fetchSummary.mockResolvedValue(BUSY);
    fetchTerritories.mockResolvedValue([]);
    allow("visit:read", "journey:read");
  });

  it("shows each figure beside what it was measured over", async () => {
    // A percentage with no denominator is the most confidently wrong thing a dashboard can print:
    // 75% coverage means one thing over eight planned calls and another over four hundred.
    render(<Dashboard />);

    /*
     * Scoped to each card rather than searched for globally. Coverage and the strike rate are both
     * 75% in this fixture — deliberately, because a screen that rendered one figure twice would pass
     * an unscoped `findByText("75.00%")` and be obviously wrong on screen.
     */
    const coverage = (await screen.findByText("Coverage")).closest("div")!;

    expect(within(coverage).getByText("75.00%")).toBeTruthy();
    expect(within(coverage).getByText(/6 of 8 planned calls made · 1 not visited/)).toBeTruthy();

    const strikeRate = screen.getByText("Strike rate").closest("div")!;

    expect(within(strikeRate).getByText("75.00%")).toBeTruthy();
    expect(within(strikeRate).getByText(/6 productive of 8 finished · 1 still open/)).toBeTruthy();

    const perfectStore = screen.getByText("Perfect store").closest("div")!;

    expect(within(perfectStore).getByText("82.50%")).toBeTruthy();
    expect(within(perfectStore).getByText(/4 of 5 audits scored/)).toBeTruthy();

    // The scope, before any of them.
    expect(screen.getByText(/2 outlets, 2026-09-01 to 2026-09-30/)).toBeTruthy();
  });

  it("renders a rate that does not exist as a dash, never as zero", async () => {
    /*
     * The single most important behaviour on this screen, and the one a fresh tenant meets first.
     *
     * Nothing planned is not 0% coverage and nothing finished is not a 0% strike rate — those would
     * tell a supervisor their team failed every call on their first morning. The server is careful
     * to send null; rendering it as zero here would throw that away at the last step, where it is
     * least visible.
     */
    fetchSummary.mockResolvedValue(QUIET);

    render(<Dashboard />);

    // Two shops are in scope, so this is not the empty-scope path — the work simply has not happened.
    expect(await screen.findByText(/2 outlets/)).toBeTruthy();

    const dashes = await screen.findAllByText("—");

    // Coverage, strike rate, perfect store and order value: four figures, four dashes.
    expect(dashes.length).toBe(4);
    expect(screen.queryByText("0.00%")).toBeNull();
  });

  it("says when nothing is in scope, rather than showing zeroes", async () => {
    // "No shops" and "no work" are different emergencies. A dashboard that renders a grid of dashes
    // over an empty scope invites a supervisor to go looking for a team that has no shops.
    fetchSummary.mockResolvedValue({ ...QUIET, outlets: 0 });

    render(<Dashboard />);

    expect(await screen.findByText(/No outlets are in scope yet/)).toBeTruthy();
    expect(screen.queryByText("—")).toBeNull();
  });

  it("warns when an average was taken across two weightings", async () => {
    /*
     * `BR-AUD-8` records the weighting each audit was scored against because a re-weighting cannot
     * be undone. An average across two of them is an average of two rulers — still worth showing,
     * but a five-point movement across that boundary is not a change in anybody's shops.
     *
     * As text, not a tooltip: a caveat a keyboard cannot reach is one only sighted mouse users get.
     */
    fetchSummary.mockResolvedValue({
      ...BUSY,
      perfectStore: { ...BUSY.perfectStore, comparable: false, weightSetVersions: [3, 4] },
    });

    render(<Dashboard />);

    expect(await screen.findByText(/Averaged across weighting versions 3, 4/)).toBeTruthy();
  });

  it("shows a skipped pillar's count beside its average", async () => {
    // `BR-AUD-2` renormalises a skipped pillar away rather than scoring it zero, so 96% here is over
    // the one audit that measured it. Without the count, that reads as a triumph.
    render(<Dashboard />);

    const shelf = (await screen.findByText("Share of shelf")).closest("li")!;

    expect(within(shelf).getByText("96.00%")).toBeTruthy();
    expect(within(shelf).getByText(/1 measured · 3 skipped/)).toBeTruthy();
  });

  it("asks again for one territory when a reader picks one", async () => {
    allow("visit:read", "journey:read", "territory:read");
    fetchTerritories.mockResolvedValue([
      { id: "terr-1", name: "Bucharest North", orgUnitId: "unit-1", outletCount: 12 },
    ]);

    render(<Dashboard />);

    const selector = await screen.findByLabelText("Territory");

    await userEvent.selectOptions(selector, "terr-1");

    await waitFor(() =>
      expect(fetchSummary).toHaveBeenCalledWith(
        "token",
        expect.objectContaining({ territoryId: "terr-1" }),
        expect.anything(),
      ));
  });

  it("does not offer a territory selector to a reader who may not list them", async () => {
    // The dashboard still answers — it defaults to every territory, which is the only answer such a
    // reader could have had anyway. What it does not do is show a control that would return nothing.
    allow("visit:read", "journey:read");
    fetchTerritories.mockResolvedValue([
      { id: "terr-1", name: "Bucharest North", orgUnitId: "unit-1", outletCount: 12 },
    ]);

    render(<Dashboard />);

    await screen.findByText(/2 outlets/);

    expect(screen.queryByLabelText("Territory")).toBeNull();
    expect(fetchTerritories).not.toHaveBeenCalled();
  });

  it("says which refusal it met", async () => {
    // A 403 is not a failure to load: the first is about who is asking and the second about the
    // server. Telling a reader to retry a request that will never succeed is the worse of the two.
    fetchSummary.mockRejectedValue(new ApiError(403, "Forbidden"));

    render(<Dashboard />);

    const alert = await screen.findByRole("alert");

    expect(alert.textContent).toMatch(/do not have permission/);
  });
});
