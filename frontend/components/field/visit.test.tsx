// @vitest-environment jsdom

import "fake-indexeddb/auto";

import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { Visit } from "@/components/field/visit";
import type { SyncContextValue } from "@/components/sync/sync-provider";
import {
  closeDatabase,
  FieldKitDatabase,
  type LocalVisit,
  type LocalVisitStep,
  type ReferenceOutlet,
} from "@/lib/sync/db";
import { render } from "@/test/render";

/**
 * The visit a rep is working (`VIS-03`, `VIS-06`) — W9 slice 7.
 *
 * Against a real database and the real `completeStep`, because the claims are about what ends up in
 * the store: which sequence is rendered, what a note step refuses, and that a step whose control
 * does not exist yet can still be finished.
 */
// `<Visit>` mounts the check-out panel (W9 slice 8), which navigates once a visit is sealed. Nothing
// in this file exercises that — it is check-out's own test — but the module has to resolve.
vi.mock("@/i18n/navigation", () => ({
  Link: ({ href, children }: { href: string; children: React.ReactNode }) => (
    <a href={href}>{children}</a>
  ),
  useRouter: () => ({ replace: vi.fn() }),
}));

const sync = vi.hoisted(() => ({ current: {} as SyncContextValue }));

vi.mock("@/components/sync/sync-provider", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/components/sync/sync-provider")>()),
  useSync: () => sync.current,
}));

const SHOP: ReferenceOutlet = {
  id: "outlet-1",
  code: "RO-BUC-0001",
  name: "Mega Image Dorobanți",
  channelId: "channel-1",
  segment: "A",
  status: "Active",
  latitude: 44.4638,
  longitude: 26.0946,
  countryCode: "RO",
  radiusMetres: 150,
  rowVersion: 4,
};

function step(overrides: Partial<LocalVisitStep> & { order: number }): LocalVisitStep {
  return {
    stepId: `step-${overrides.order}`,
    type: "Task",
    mandatory: false,
    label: `Step ${overrides.order}`,
    notes: null,
    completedAtUtc: null,
    ...overrides,
  };
}

function visit(steps: LocalVisitStep[], overrides: Partial<LocalVisit> = {}): LocalVisit {
  return {
    id: "visit-1",
    outletId: "outlet-1",
    plannedVisitId: "call-1",
    status: "inProgress",
    checkedInAtUtc: "2026-03-17T09:00:00.000Z",
    checkInLatitude: 44.4638,
    checkInLongitude: 26.0946,
    checkInDistanceMetres: 12,
    wasInsideGeofence: true,
    overrideReason: null,
    steps,
    checkedOutAtUtc: null,
    checkOutLatitude: null,
    checkOutLongitude: null,
    outcome: null,
    outcomeReason: null,
    ...overrides,
  };
}

/**
 * The configured sequence, by the name the list now carries (W9 slice 10).
 *
 * The recap above check-out renders optional step labels and the notes written against them, so an
 * unscoped query for either matches twice — and would pass on the recap whether or not this list
 * held anything.
 */
const steps = () => within(screen.getByRole("list", { name: "Visit steps" }));

let db: FieldKitDatabase;

beforeEach(async () => {
  db = new FieldKitDatabase(`visit:${crypto.randomUUID()}`);
  sync.current = { db, pending: 0, failed: 0, running: false, outcome: null, syncNow: vi.fn() };

  await db.outlets.add(SHOP);
});

afterEach(async () => {
  await db.delete();
  closeDatabase();
});

describe("<Visit> and the sequence it shows", () => {
  it("reads the steps off the visit, not out of Configuration", async () => {
    // `BR-VIS-6`'s snapshot rule, asserted from the screen. The workflow in the store has been
    // edited since — an admin renaming a step at eleven — and the rep who checked in at ten must
    // still see, and be judged against, what they were told when they started.
    await db.workflows.add({
      id: "workflow-1",
      channelId: "channel-1",
      presenceExpected: true,
      steps: [{ order: 1, type: "Task", mandatory: true, label: "Renamed this morning" }],
      rowVersion: 9,
    });
    await db.visits.add(visit([step({ order: 1, label: "Check the chiller is lit" })]));

    render(<Visit visitId="visit-1" />);

    expect(await screen.findByText("0 of 1 steps done")).toBeTruthy();
    expect(steps().getByText("Check the chiller is lit")).toBeTruthy();
    expect(screen.queryByText("Renamed this morning")).toBeNull();
  });

  it("keeps the order the workflow gave them", async () => {
    await db.visits.add(
      visit([
        step({ order: 1, label: "Shelf audit" }),
        step({ order: 2, label: "Take order" }),
        step({ order: 3, label: "Fridge photo" }),
      ]),
    );

    render(<Visit visitId="visit-1" />);

    await screen.findByText("0 of 3 steps done");

    expect(steps().getAllByRole("listitem").map((row) => row.textContent?.split("Task")[0])).toEqual([
      "Shelf audit",
      "Take order",
      "Fridge photo",
    ]);
  });

  it("marks only what is required, and counts what is done", async () => {
    await db.visits.add(
      visit([
        step({ order: 1, mandatory: true, label: "Shelf audit" }),
        step({ order: 2, label: "Fridge photo", completedAtUtc: "2026-03-17T09:05:00.000Z" }),
      ]),
    );

    render(<Visit visitId="visit-1" />);

    expect(await screen.findByText("1 of 2 steps done")).toBeTruthy();

    // One chip, not two: optional is the majority, and badging it would give a rep a screen of
    // labels to read past to find the one that decides whether they can leave (`BR-VIS-3`).
    expect(screen.getAllByText("Required")).toHaveLength(1);
    expect(screen.getByText("Done")).toBeTruthy();
  });

  it("names a step type it has never heard of rather than rendering a blank row", async () => {
    // A device is offline-first and therefore routinely older than the server. A step type added
    // after this app shipped must not leave a mandatory row with no label and a rep who cannot
    // check out.
    await db.visits.add(visit([step({ order: 1, type: "Hologram", label: "Scan the shelf" })]));

    render(<Visit visitId="visit-1" />);

    expect(await screen.findByText("0 of 1 steps done")).toBeTruthy();
    expect(steps().getByText("Scan the shelf")).toBeTruthy();
    expect(steps().getByText("Step")).toBeTruthy();
  });

  it("says so when the channel has no steps at all", async () => {
    // Not a misconfiguration: `IVisitWorkflow` returns exactly this for a channel nobody set up, and
    // check-in copies it faithfully. The rep checks in and checks out.
    await db.visits.add(visit([]));

    render(<Visit visitId="visit-1" />);

    expect(
      await screen.findByText(
        "No steps are set up for this shop. Do the call and check out when you are done.",
      ),
    ).toBeTruthy();
  });
});

describe("<Visit> working a step", () => {
  it("finishes a step whose control does not exist yet, rather than stranding the visit", async () => {
    // The decision this slice is built on. `Audit` opens a sub-flow in W10; until then it is what it
    // already is — a labelled item on a checklist the rep works in the shop. A mandatory step nobody
    // can complete is, by `BR-VIS-3`, a rep who cannot check out: the visit would be broken by a
    // feature not being finished yet.
    await db.visits.add(visit([step({ order: 1, type: "Audit", mandatory: true, label: "Shelf audit" })]));

    render(<Visit visitId="visit-1" />);

    await userEvent.click(await screen.findByRole("button", { name: "Mark done" }));

    await waitFor(async () =>
      expect((await db.visits.get("visit-1"))?.steps[0].completedAtUtc).toBeTruthy(),
    );

    expect(await screen.findByText("Done")).toBeTruthy();
    expect(screen.queryByRole("button", { name: "Mark done" })).toBeNull();
  });

  it("refuses a note step with nothing written, because a note is its text", async () => {
    await db.visits.add(visit([step({ order: 1, type: "Note", label: "Anything to report?" })]));

    render(<Visit visitId="visit-1" />);

    await userEvent.click(await screen.findByRole("button", { name: "Save note" }));

    expect(await screen.findByText("Write the note before saving it.")).toBeTruthy();
    expect((await db.visits.get("visit-1"))?.steps[0].completedAtUtc).toBeNull();
  });

  it("keeps what the rep wrote, and keeps showing it once the step is done", async () => {
    await db.visits.add(visit([step({ order: 1, type: "Note", label: "Anything to report?" })]));

    render(<Visit visitId="visit-1" />);

    await userEvent.type(
      await screen.findByRole("textbox"),
      "Manager asked about the promotion end date",
    );
    await userEvent.click(screen.getByRole("button", { name: "Save note" }));

    await waitFor(async () =>
      expect((await db.visits.get("visit-1"))?.steps[0].notes).toBe(
        "Manager asked about the promotion end date",
      ),
    );

    // A finished note showing only a tick would have swallowed the whole point of writing it.
    // Scoped to the step list: the recap gathers notes too (W9 slice 10), so an unscoped query
    // would pass on the recap whether or not the step kept its text.
    expect(steps().getByText("Manager asked about the promotion end date")).toBeTruthy();
  });

  it("works one step without disturbing the other", async () => {
    await db.visits.add(
      visit([
        step({ order: 1, label: "Shelf audit" }),
        step({ order: 2, label: "Fridge photo" }),
      ]),
    );

    render(<Visit visitId="visit-1" />);

    const buttons = await screen.findAllByRole("button", { name: "Mark done" });
    await userEvent.click(buttons[1]);

    await waitFor(async () => {
      const steps = (await db.visits.get("visit-1"))?.steps ?? [];
      expect(steps[0].completedAtUtc).toBeNull();
      expect(steps[1].completedAtUtc).toBeTruthy();
    });
  });
});

describe("<Visit> that is no longer open", () => {
  it("reads as a record rather than a screen with the buttons greyed out", async () => {
    await db.visits.add(
      visit([step({ order: 1, label: "Shelf audit", completedAtUtc: "2026-03-17T09:05:00.000Z" })], {
        status: "checkedOut",
        checkedOutAtUtc: "2026-03-17T09:25:00.000Z",
        outcome: "Productive",
      }),
    );

    render(<Visit visitId="visit-1" />);

    expect(
      await screen.findByText("This visit is finished. Nothing here can be changed."),
    ).toBeTruthy();
    expect(screen.queryByRole("button")).toBeNull();
  });

  it("says a visit this device does not have is not here, rather than waiting on it", async () => {
    render(<Visit visitId="never-existed" />);

    expect(await screen.findByText("That visit is not on this device")).toBeTruthy();
  });
});
