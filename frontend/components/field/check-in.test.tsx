// @vitest-environment jsdom

import "fake-indexeddb/auto";

import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { CheckIn } from "@/components/field/check-in";
import type { SyncContextValue } from "@/components/sync/sync-provider";
import {
  closeDatabase,
  FieldKitDatabase,
  type ReferenceOutlet,
  type ReferenceVisitWorkflow,
} from "@/lib/sync/db";
import { pending } from "@/lib/sync/outbox";
import { render } from "@/test/render";

/**
 * Starting a visit from the device (`VIS-01`, `VIS-02`) — W9 slice 6.
 *
 * Against a real database and a real `assess`, because the claim this screen makes is that the
 * verdict a rep reads is the verdict the visit carries. Stubbing either end would leave that
 * untested and the screen still passing.
 */
const replace = vi.fn();

vi.mock("@/i18n/navigation", () => ({
  Link: ({ href, children }: { href: string; children: React.ReactNode }) => (
    <a href={href}>{children}</a>
  ),
  useRouter: () => ({ replace }),
}));

const sync = vi.hoisted(() => ({ current: {} as SyncContextValue }));

vi.mock("@/components/sync/sync-provider", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/components/sync/sync-provider")>()),
  useSync: () => sync.current,
}));

/** Mega Image Dorobanți, placed, with the default 150 m fence. */
const SHOP: ReferenceOutlet = {
  id: "outlet-1",
  code: "RO-BUC-0001",
  name: "Mega Image Dorobanți",
  channelId: "channel-1",
  segment: "A",
  status: "Active",
  latitude: 44.4638,
  longitude: 26.0946,
  radiusMetres: 150,
  rowVersion: 4,
};

/** Roughly 90 m north of the shop — comfortably inside, and not near the boundary. */
const AT_THE_DOOR = { latitude: 44.4646, longitude: 26.0946 };

/** Roughly 2.2 km away: the "did you really go" distance rather than a GPS wobble. */
const DOWN_THE_ROAD = { latitude: 44.4838, longitude: 26.0946 };

function workflow(overrides: Partial<ReferenceVisitWorkflow> = {}): ReferenceVisitWorkflow {
  return {
    id: "workflow-1",
    channelId: "channel-1",
    presenceExpected: true,
    steps: [],
    rowVersion: 3,
    ...overrides,
  };
}

/** Puts a fix — or a refusal — behind `navigator.geolocation` for one test. */
function locateAt(at: { latitude: number; longitude: number } | { code: 1 | 2 | 3 }) {
  Object.defineProperty(globalThis.navigator, "geolocation", {
    configurable: true,
    value: {
      getCurrentPosition: (onSuccess: PositionCallback, onError?: PositionErrorCallback) =>
        "code" in at
          ? onError?.({ code: at.code, message: "" } as GeolocationPositionError)
          : onSuccess({
              coords: { ...at, accuracy: 8 },
              timestamp: 0,
            } as GeolocationPosition),
    },
  });
}

let db: FieldKitDatabase;

beforeEach(async () => {
  replace.mockClear();
  db = new FieldKitDatabase(`checkin:${crypto.randomUUID()}`);
  sync.current = { db, pending: 0, running: false, outcome: null, syncNow: vi.fn() };

  await db.outlets.add(SHOP);
  locateAt(AT_THE_DOOR);
});

afterEach(async () => {
  await db.delete();
  closeDatabase();
});

describe("<CheckIn> inside the fence", () => {
  it("says where the rep is and starts the visit without asking for anything", async () => {
    await db.workflows.add(workflow());

    render(<CheckIn outletId="outlet-1" plannedVisitId="call-1" />);

    expect(await screen.findByText(/You are at this shop/)).toBeTruthy();

    // The reason box is the thing that must *not* be here: a reason volunteered for a check-in that
    // was inside the fence makes "how many overrides this month" a count of typing.
    expect(screen.queryByLabelText(/Why are you checking in/)).toBeNull();

    await userEvent.click(screen.getByRole("button", { name: "Check in and start the visit" }));

    await waitFor(async () => expect(await db.visits.count()).toBe(1));

    const visit = (await db.visits.toArray())[0];

    expect(visit.wasInsideGeofence).toBe(true);
    expect(visit.overrideReason).toBeNull();
    expect(visit.plannedVisitId).toBe("call-1");
    expect(visit.status).toBe("inProgress");
  });

  it("opens the visit it just started (W9 slice 7)", async () => {
    render(<CheckIn outletId="outlet-1" />);

    await screen.findByText(/You are at this shop/);
    await userEvent.click(screen.getByRole("button", { name: "Check in and start the visit" }));

    // The id is the device's own, minted here, so the route is reachable from the moment the visit
    // exists — including for a whole day with no signal.
    await waitFor(async () => {
      const started = (await db.visits.toArray())[0];

      expect(replace).toHaveBeenCalledWith(`/field/visits/${started.id}`);
    });
  });

  it("captures nothing in the outbox — a visit reaches the server when it is sealed, not when it opens", async () => {
    // The whole shape of `OFF-01`: the device authors a visit and works it offline, and only
    // check-out turns it into one `CapturedVisit`. A mutation queued here would send the server a
    // half-worked visit and make `IVisitIngest` a second implementation of this flow.
    render(<CheckIn outletId="outlet-1" />);

    await screen.findByText(/You are at this shop/);
    await userEvent.click(screen.getByRole("button", { name: "Check in and start the visit" }));

    await waitFor(async () => expect(await db.visits.count()).toBe(1));

    expect(await pending(db)).toEqual([]);
  });
});

describe("<CheckIn> outside the fence", () => {
  beforeEach(() => locateAt(DOWN_THE_ROAD));

  it("says how far, against what radius, because those are different conversations", async () => {
    render(<CheckIn outletId="outlet-1" />);

    // Two kilometres and twelve metres both read "outside" without the numbers, and only one of
    // them is worth a supervisor's attention.
    expect(await screen.findByText(/2\d{3} m from this shop, outside its 150 m area/)).toBeTruthy();
  });

  it("refuses the check-in until the rep says why, and never blocks it after that", async () => {
    render(<CheckIn outletId="outlet-1" />);

    const start = await screen.findByRole("button", { name: "Check in and start the visit" });

    // `BR-VIS-2` in one assertion: the button is live while the rep is outside the fence. A disabled
    // button here would be the block the rule forbids.
    expect((start as HTMLButtonElement).disabled).toBe(false);

    await userEvent.click(start);

    expect(await screen.findByText("Say why you are checking in from here.")).toBeTruthy();
    expect(await db.visits.count()).toBe(0);

    await userEvent.type(
      screen.getByRole("textbox"),
      "Deliveries blocking the yard, parked on the street",
    );
    await userEvent.click(screen.getByRole("button", { name: "Check in and start the visit" }));

    await waitFor(async () => expect(await db.visits.count()).toBe(1));

    const visit = (await db.visits.toArray())[0];

    expect(visit.wasInsideGeofence).toBe(false);
    expect(visit.overrideReason).toBe("Deliveries blocking the yard, parked on the street");
    expect(Math.round(visit.checkInDistanceMetres ?? 0)).toBeGreaterThan(2000);
  });
});

describe("<CheckIn> when the phone cannot say where it is", () => {
  it("asks for a reason anyway, because a missing fix is how a check-in would be faked", async () => {
    locateAt({ code: 1 });

    render(<CheckIn outletId="outlet-1" />);

    expect(await screen.findByText(/Location is blocked for this app/)).toBeTruthy();
    expect(screen.getByRole("textbox")).toBeTruthy();
  });

  it("records the visit with no position rather than refusing to start it", async () => {
    locateAt({ code: 2 });

    render(<CheckIn outletId="outlet-1" />);

    await screen.findByText(/could not get a location/);
    await userEvent.type(screen.getByRole("textbox"), "In the stockroom, no signal");
    await userEvent.click(screen.getByRole("button", { name: "Check in and start the visit" }));

    await waitFor(async () => expect(await db.visits.count()).toBe(1));

    const visit = (await db.visits.toArray())[0];

    expect(visit.checkInLatitude).toBeNull();
    expect(visit.checkInDistanceMetres).toBeNull();
    expect(visit.wasInsideGeofence).toBe(false);
    expect(visit.overrideReason).toBe("In the stockroom, no signal");
  });
});

describe("<CheckIn> and the channel's presence policy", () => {
  it("asks nothing of a remote-capable channel, however far away the rep is", async () => {
    // Read from the pulled workflow, not assumed. A phone call is legitimately not at the outlet,
    // and a flag that fires on ordinary work is one supervisors learn to ignore (`BR-VIS-2`).
    await db.workflows.add(workflow({ presenceExpected: false }));
    locateAt(DOWN_THE_ROAD);

    render(<CheckIn outletId="outlet-1" />);

    expect(await screen.findByText("This channel does not expect you to be at the shop.")).toBeTruthy();
    expect(screen.queryByRole("textbox")).toBeNull();

    await userEvent.click(screen.getByRole("button", { name: "Check in and start the visit" }));

    await waitFor(async () => expect(await db.visits.count()).toBe(1));
    expect((await db.visits.toArray())[0].overrideReason).toBeNull();
  });

  it("expects presence when this device holds no workflow for the channel", async () => {
    // The safe direction, and the same answer the server's default gives. A device that has not
    // been sent a channel's workflow must not conclude the rep may check in from anywhere.
    locateAt(DOWN_THE_ROAD);

    render(<CheckIn outletId="outlet-1" />);

    expect(await screen.findByText(/outside its 150 m area/)).toBeTruthy();
  });

  it("copies the workflow's steps onto the visit, in order", async () => {
    // `BR-VIS-6`'s snapshot rule: an admin editing the channel workflow at eleven must not change
    // what a rep who checked in at ten is required to do.
    await db.workflows.add(
      workflow({
        steps: [
          { order: 2, type: "Order", mandatory: false, label: "Take order" },
          { order: 1, type: "Audit", mandatory: true, label: "Shelf audit" },
        ],
      }),
    );

    render(<CheckIn outletId="outlet-1" />);

    await screen.findByText(/You are at this shop/);
    await userEvent.click(screen.getByRole("button", { name: "Check in and start the visit" }));

    await waitFor(async () => expect(await db.visits.count()).toBe(1));

    expect((await db.visits.toArray())[0].steps.map((step) => step.label)).toEqual([
      "Shelf audit",
      "Take order",
    ]);
  });
});

describe("<CheckIn> when it cannot proceed", () => {
  it("does not offer a check-in at a shop this device does not hold", async () => {
    // This is also what pins the `?? null` in the outlet query: without it "not held" and "still
    // reading" are the same value, and this screen waits forever on a shop that will never arrive.
    // Verified by removing it — this test is the one that fails.
    render(<CheckIn outletId="gone" />);

    expect(await screen.findByText("This device does not hold this shop")).toBeTruthy();
    expect(screen.queryByRole("button", { name: "Check in and start the visit" })).toBeNull();
  });

  it("says a visit is open at another shop before the button rather than after it", async () => {
    await db.outlets.add({ ...SHOP, id: "outlet-2", code: "RO-BUC-0002", name: "Profi Titan" });
    await db.visits.add({
      id: "visit-1",
      outletId: "outlet-2",
      plannedVisitId: null,
      status: "inProgress",
      checkedInAtUtc: "2026-03-17T09:00:00.000Z",
      checkInLatitude: null,
      checkInLongitude: null,
      checkInDistanceMetres: null,
      wasInsideGeofence: true,
      overrideReason: null,
      steps: [],
      checkedOutAtUtc: null,
      checkOutLatitude: null,
      checkOutLongitude: null,
      outcome: null,
      outcomeReason: null,
    });

    render(<CheckIn outletId="outlet-1" />);

    expect(await screen.findByText("A visit is open at another shop")).toBeTruthy();
  });

  it("sends a rep back to the visit they already have open here", async () => {
    await db.visits.add({
      id: "visit-1",
      outletId: "outlet-1",
      plannedVisitId: null,
      status: "inProgress",
      checkedInAtUtc: "2026-03-17T09:00:00.000Z",
      checkInLatitude: null,
      checkInLongitude: null,
      checkInDistanceMetres: null,
      wasInsideGeofence: true,
      overrideReason: null,
      steps: [],
      checkedOutAtUtc: null,
      checkOutLatitude: null,
      checkOutLongitude: null,
      outcome: null,
      outcomeReason: null,
    });

    render(<CheckIn outletId="outlet-1" />);

    expect(await screen.findByText("You are already in this shop")).toBeTruthy();
  });
});
