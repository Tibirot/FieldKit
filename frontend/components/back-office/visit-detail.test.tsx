// @vitest-environment jsdom

import { screen, within } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

import type { AuthContextValue } from "@/components/auth-provider";
import { VisitDetail } from "@/components/back-office/visit-detail";
import type { Audit } from "@/lib/api/audits";
import { ApiError } from "@/lib/api/client";
import { fetchIdentity } from "@/lib/api/identity";
import type { VisitDetail as Detail } from "@/lib/api/visits";
import { render } from "@/test/render";

const auth = vi.hoisted(() => ({ current: {} as AuthContextValue }));
const fetchVisit = vi.hoisted(() => vi.fn());
const fetchAudit = vi.hoisted(() => vi.fn());

vi.mock("@/components/auth-provider", () => ({ useAuth: () => auth.current }));

vi.mock("@/lib/api/visits", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/lib/api/visits")>()),
  fetchVisit: (...args: unknown[]) => fetchVisit(...args),
}));

vi.mock("@/lib/api/audits", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/lib/api/audits")>()),
  fetchAudit: (...args: unknown[]) => fetchAudit(...args),
}));

const DETAIL: Detail = {
  visit: {
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
  },
  steps: [
    {
      id: "step-1",
      order: 1,
      type: "Task",
      mandatory: true,
      label: "Check the chiller",
      status: "Completed",
      completedAtUtc: "2026-09-03T09:35:00Z",
      notes: null,
    },
    {
      id: "step-2",
      order: 2,
      type: "Note",
      mandatory: false,
      label: "Anything to report",
      status: "Pending",
      completedAtUtc: null,
      notes: null,
    },
  ],
  openMandatorySteps: [],
};

const AUDIT: Audit = {
  id: "audit-1",
  visitId: "visit-1",
  outletId: "outlet-1",
  userId: "subject-maria",
  capturedAtUtc: "2026-09-03T09:40:00Z",
  weightSetVersion: 3,
  categoryFacings: null,
  availability: [{ productId: "p1", status: "Present" }],
  facings: [{ productId: "p1", facings: 6 }],
  prices: [],
  surveyFormId: null,
  answers: [],
  photos: [],
  score: 62.5,
  scoredPillars: [
    { pillar: "Availability", percentage: 100, weight: 50 },
    // Skipped: the rep could not count the aisle, so share of shelf was renormalised away.
    { pillar: "ShareOfShelf", percentage: null, weight: 30 },
    { pillar: "PriceCompliance", percentage: 0, weight: 20 },
  ],
};

describe("<VisitDetail>", () => {
  beforeEach(() => {
    vi.clearAllMocks();

    auth.current = {
      status: "authenticated",
      user: { access_token: "token", profile: { sub: "subject-a" } },
    } as unknown as AuthContextValue;

    fetchVisit.mockResolvedValue(DETAIL);
    fetchAudit.mockResolvedValue(AUDIT);

    vi.mocked(fetchIdentity).mockResolvedValue({
      subject: "subject-a",
      tenant: "fieldkit-dev",
      permissions: ["visit:read"],
    });
  });

  it("shows a step the rep never did rather than dropping it", async () => {
    /*
     * A workflow of six steps with two untouched is a different visit from one of four, and only
     * the first tells a supervisor what was skipped. Rendering completed steps alone would make
     * every visit look complete — which is exactly the question this screen exists to answer.
     */
    render(<VisitDetail visitId="visit-1" />);

    const pending = (await screen.findByText("Anything to report")).closest("li")!;

    expect(within(pending).getByText("Not done")).toBeTruthy();

    const done = screen.getByText("Check the chiller").closest("li")!;

    expect(within(done).getByText("Done")).toBeTruthy();
    expect(within(done).getByText("Mandatory")).toBeTruthy();
  });

  it("says a skipped pillar was not measured, never zero", async () => {
    /*
     * The load-bearing assertion of the audit panel. `BR-AUD-2` renormalises an unmeasured pillar
     * out of the score rather than counting it against the shop — so a 0% here would both misstate
     * the shelf and disagree with the total printed above it.
     *
     * The pillar that genuinely scored zero is asserted beside it, so this cannot pass by rendering
     * every pillar as "not measured".
     */
    render(<VisitDetail visitId="visit-1" />);

    const shelf = (await screen.findByText("Share of shelf")).closest("li")!;

    expect(within(shelf).getByText("Not measured")).toBeTruthy();

    const price = screen.getByText("Price compliance").closest("li")!;

    expect(within(price).getByText("0.00%")).toBeTruthy();
  });

  it("names the weighting the score was computed against", async () => {
    // `BR-AUD-8` records it because a re-weighting cannot be undone. Two audits scored under
    // different versions are not comparable however close the numbers look, and a score printed
    // without its version invites exactly that comparison.
    render(<VisitDetail visitId="visit-1" />);

    expect(await screen.findByText(/weighting version 3/)).toBeTruthy();
  });

  it("shows the reason a rep gave for being away from the shop", async () => {
    // The prose `BR-VIS-2` collects, and the most likely reason a supervisor opened this screen.
    fetchVisit.mockResolvedValue({
      ...DETAIL,
      visit: {
        ...DETAIL.visit,
        wasInsideGeofence: false,
        geofenceOverrideReason: "Shutters down, served from the back",
      },
    });

    render(<VisitDetail visitId="visit-1" />);

    expect(await screen.findByText(/Shutters down, served from the back/)).toBeTruthy();
    expect(screen.getByText("Away from the shop")).toBeTruthy();
  });

  it("says plainly when no audit was worked", async () => {
    // A visit with no audit is ordinary — most are. The endpoint answers 404 and the API layer
    // turns that into null precisely so this reads as a sentence rather than a failure.
    fetchAudit.mockResolvedValue(null);

    render(<VisitDetail visitId="visit-1" />);

    expect(await screen.findByText(/No audit was worked/)).toBeTruthy();
    expect(screen.queryByText(/Perfect-store score/)).toBeNull();
  });

  it("renders an unscorable audit as a dash rather than as zero", async () => {
    // Same distinction as the pillars, one level up: an audit nothing could be scored from has no
    // score, and 0% would be a claim about a shop nobody managed to measure.
    fetchAudit.mockResolvedValue({
      ...AUDIT,
      score: null,
      scoredPillars: AUDIT.scoredPillars.map((pillar) => ({ ...pillar, percentage: null })),
    });

    render(<VisitDetail visitId="visit-1" />);

    await screen.findByText(/Perfect-store score/);

    expect(screen.getByText("—")).toBeTruthy();
    expect(screen.queryByText("0.00%")).toBeNull();
  });

  it("tells a missing visit apart from a refusal", async () => {
    // A 404 and a 403 send a reader to different places: one to the list they came from, the other
    // to whoever grants permissions. Collapsing them would waste both trips.
    fetchVisit.mockRejectedValue(new ApiError(404, "Not Found"));

    render(<VisitDetail visitId="visit-1" />);

    expect((await screen.findByRole("alert")).textContent).toMatch(/No such visit/);
  });

  it("says which refusal it met", async () => {
    fetchVisit.mockRejectedValue(new ApiError(403, "Forbidden"));

    render(<VisitDetail visitId="visit-1" />);

    expect((await screen.findByRole("alert")).textContent).toMatch(/do not have permission/);
  });
});
