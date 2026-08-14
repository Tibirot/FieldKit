// @vitest-environment jsdom

import "fake-indexeddb/auto";

import { screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { TodaysJourney } from "@/components/field/todays-journey";
import type { SyncContextValue } from "@/components/sync/sync-provider";
import {
  closeDatabase,
  FieldKitDatabase,
  type LocalVisit,
  type ReferenceOutlet,
  type ReferencePlannedVisit,
} from "@/lib/sync/db";
import { enqueue, markRejected } from "@/lib/sync/outbox";
import { eventually } from "@/test/eventually";
import { render } from "@/test/render";

/**
 * The screen the field app opens on (`JRN-05`, `OFF-05`) — W9 slice 5.
 *
 * Rendered against a **real** database rather than a stubbed reader, because what this screen is for
 * is reading the local store: a test that mocked `today()` would assert that the component renders
 * an array, which is not the claim.
 */
const sync = vi.hoisted(() => ({ current: {} as SyncContextValue }));

vi.mock("@/components/sync/sync-provider", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/components/sync/sync-provider")>()),
  useSync: () => sync.current,
}));

const TODAY = new Date(2026, 2, 17, 9, 0);
const TODAY_ISO = "2026-03-17";

function outlet(id: string, name: string, code: string): ReferenceOutlet {
  return {
    id,
    code,
    name,
    channelId: "channel-1",
    segment: "A",
    status: "Active",
    latitude: 44.4638,
    longitude: 26.0946,
    countryCode: "RO",
    timeZoneId: "Europe/Bucharest",
    radiusMetres: 150,
    rowVersion: 4,
  };
}

function call(
  id: string,
  outletId: string,
  overrides: Partial<ReferencePlannedVisit> = {},
): ReferencePlannedVisit {
  return {
    id,
    outletId,
    date: TODAY_ISO,
    status: "Planned",
    source: "Generated",
    notVisitedReason: null,
    rowVersion: 7,
    movableFrom: null,
    movableTo: null,
    ...overrides,
  };
}

function visit(id: string, outletId: string, overrides: Partial<LocalVisit> = {}): LocalVisit {
  return {
    id,
    outletId,
    plannedVisitId: null,
    status: "checkedOut",
    checkedInAtUtc: "2026-03-17T09:00:00.000Z",
    checkInLatitude: null,
    checkInLongitude: null,
    checkInDistanceMetres: null,
    wasInsideGeofence: true,
    overrideReason: null,
    steps: [],
    checkedOutAtUtc: "2026-03-17T09:25:00.000Z",
    checkOutLatitude: null,
    checkOutLongitude: null,
    outcome: "Productive",
    outcomeReason: null,
    ...overrides,
  };
}

let db: FieldKitDatabase;

beforeEach(() => {
  db = new FieldKitDatabase(`journey:${crypto.randomUUID()}`);
  sync.current = {
    db,
    photographs: 0,
    pending: 0,
    failed: 0,
    running: false,
    outcome: null,
    syncNow: vi.fn(),
  };
});

afterEach(async () => {
  await db.delete();
  closeDatabase();
});

describe("<TodaysJourney>", () => {
  it("says what to do when the device holds no round for today", async () => {
    render(<TodaysJourney now={TODAY} />);

    // One empty state where there are arguably two — "no plan" and "a plan with nothing today". A
    // rep cannot act differently on them, and a screen that split them would be explaining its own
    // data model.
    expect(
      await screen.findByText(
        "No calls planned for today. Sync to pick up a new round, or ask your supervisor.",
      ),
    ).toBeTruthy();
  });

  it("shows the day's stops with the code that tells two shops of one name apart", async () => {
    await db.outlets.bulkAdd([
      outlet("outlet-1", "Mega Image Dorobanți", "RO-BUC-0001"),
      outlet("outlet-2", "Mega Image Dorobanți", "RO-BUC-0009"),
    ]);
    await db.plannedVisits.bulkAdd([call("call-1", "outlet-1"), call("call-2", "outlet-2")]);

    render(<TodaysJourney now={TODAY} />);

    expect(await screen.findByText("RO-BUC-0001")).toBeTruthy();
    expect(screen.getByText("RO-BUC-0009")).toBeTruthy();

    // The date the rep is standing in, formatted for their locale rather than printed as an ISO
    // string at them.
    expect(screen.getByText("Tuesday, March 17, 2026")).toBeTruthy();
  });

  it("marks the shop the rep is standing in", async () => {
    await db.outlets.add(outlet("outlet-1", "Mega Image", "RO-001"));
    await db.plannedVisits.add(call("call-1", "outlet-1"));
    await db.visits.add(visit("visit-1", "outlet-1", { status: "inProgress" }));

    render(<TodaysJourney now={TODAY} />);

    expect(await screen.findByText("In the shop")).toBeTruthy();
  });

  it("keeps the rep's reason next to the stop it explains", async () => {
    await db.outlets.add(outlet("outlet-1", "Mega Image", "RO-001"));
    await db.plannedVisits.add(
      call("call-1", "outlet-1", {
        status: "NotVisited",
        notVisitedReason: "Closed for refurbishment",
      }),
    );

    render(<TodaysJourney now={TODAY} />);

    expect(await screen.findByText("Not visited")).toBeTruthy();
    expect(screen.getByText("Closed for refurbishment")).toBeTruthy();
  });

  it("badges a worked stop whose visit has not reached the back office", async () => {
    // The badge answers a different question from the status chip beside it: one is "did the rep do
    // it", the other is "does the back office know". A worked visit still queued is exactly the case
    // where the two differ, and the rep needs both.
    await db.outlets.add(outlet("outlet-1", "Mega Image", "RO-001"));
    await db.plannedVisits.add(call("call-1", "outlet-1"));
    await db.visits.add(visit("visit-1", "outlet-1"));
    await enqueue(db, { type: "CapturedVisit", subjectId: "visit-1", payload: {} });

    render(<TodaysJourney now={TODAY} />);

    expect(await screen.findByText("Worked")).toBeTruthy();
    expect(await screen.findByText("Not synced")).toBeTruthy();
  });

  it("says why the back office refused a visit, not just that it did (W11½ R5)", async () => {
    // Regression F1: `markRejected` has stored the reason since W8 and nothing read it, so a rep saw
    // *Needs attention* and had no way to find out what to attend to.
    await db.outlets.add(outlet("outlet-1", "Mega Image", "RO-001"));
    await db.plannedVisits.add(call("call-1", "outlet-1"));
    await db.visits.add(visit("visit-1", "outlet-1"));

    const entry = await enqueue(db, {
      type: "CapturedVisit",
      subjectId: "visit-1",
      payload: {},
    });

    await markRejected(db, entry.mutationId, "visit.ingest.outletUnknown", "No such outlet.");

    render(<TodaysJourney now={TODAY} />);

    // The badge still says what it always said; the sentence is the half that was missing.
    expect(await screen.findByText("Needs attention")).toBeTruthy();
    expect((await screen.findByRole("alert")).textContent).toBe("No such outlet.");
  });

  it("says nothing extra when a failure carried no reason", async () => {
    // A transport failure marks no code, and "refused, reason unknown" is a worse answer than the
    // badge alone. The absence has to be a decision rather than an accident of the data.
    await db.outlets.add(outlet("outlet-1", "Mega Image", "RO-001"));
    await db.plannedVisits.add(call("call-1", "outlet-1"));
    await db.visits.add(visit("visit-1", "outlet-1"));

    const entry = await enqueue(db, {
      type: "CapturedVisit",
      subjectId: "visit-1",
      payload: {},
    });

    await markRejected(db, entry.mutationId);

    render(<TodaysJourney now={TODAY} />);

    expect(await screen.findByText("Needs attention")).toBeTruthy();
    await eventually(() => expect(screen.queryByRole("alert")).toBeNull());
  });

  it("names a call whose shop this device does not hold rather than showing an id", async () => {
    await db.plannedVisits.add(call("call-1", "gone"));

    render(<TodaysJourney now={TODAY} />);

    expect(await screen.findByText("A shop this device does not hold")).toBeTruthy();
    expect(screen.queryByText("gone")).toBeNull();
  });

  it("opens a stop at the shop, carrying the call it answers (W9 slice 6)", async () => {
    // The call id is what makes the visit answer *this* line. Without it a rep opening a stop from
    // their round would capture an unplanned visit at the right shop, and the coverage figure the
    // plan exists to produce would still count the call as outstanding.
    await db.outlets.add(outlet("outlet-1", "Mega Image", "RO-001"));
    await db.plannedVisits.add(call("call-1", "outlet-1"));

    render(<TodaysJourney now={TODAY} />);

    expect(await screen.findByRole("link", { name: "Mega Image" })).toHaveProperty(
      "href",
      expect.stringContaining("/field/outlets/outlet-1?call=call-1"),
    );
  });

  it("sends a rep back into the visit they are standing in, not to check in again (W9 slice 7)", async () => {
    // Without this the visit is stranded by a routing decision: a rep who navigated away — to read
    // the round, or because the phone locked — lands on a check-in screen that correctly refuses to
    // start a second visit and then has nothing to offer.
    await db.outlets.add(outlet("outlet-1", "Mega Image", "RO-001"));
    await db.plannedVisits.add(call("call-1", "outlet-1"));
    await db.visits.add(visit("visit-1", "outlet-1", { status: "inProgress" }));

    render(<TodaysJourney now={TODAY} />);

    expect(await screen.findByRole("link", { name: "Mega Image" })).toHaveProperty(
      "href",
      expect.stringContaining("/field/visits/visit-1"),
    );
  });

  it("still offers a second call at a shop whose visit is finished", async () => {
    // Deliberate rather than an omission: the sealed visit is a record, and what a rep at that shop
    // wants next is an unplanned call (`JRN-06`), not the read-only page.
    await db.outlets.add(outlet("outlet-1", "Mega Image", "RO-001"));
    await db.plannedVisits.add(call("call-1", "outlet-1"));
    await db.visits.add(visit("visit-1", "outlet-1"));

    render(<TodaysJourney now={TODAY} />);

    expect(await screen.findByRole("link", { name: "Mega Image" })).toHaveProperty(
      "href",
      expect.stringContaining("/field/outlets/outlet-1?call=call-1"),
    );
  });

  it("does not offer a tap that leads nowhere", async () => {
    // A stop with no outlet has nothing behind it: the check-in screen could only say so a second
    // time. The row stays — the call is real — and it simply is not a link.
    await db.plannedVisits.add(call("call-1", "gone"));

    render(<TodaysJourney now={TODAY} />);

    await screen.findByText("A shop this device does not hold");

    expect(screen.queryByRole("link")).toBeNull();
  });
});
