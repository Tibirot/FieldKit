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
import { pending } from "@/lib/sync/outbox";
import { render } from "@/test/render";

/**
 * Ending a visit on the device (`VIS-04`, `VIS-05`) — W9 slice 8.
 *
 * Driven through `<Visit>` rather than by mounting `<CheckOut>` directly: what is being claimed is
 * that a rep working this screen reaches a sealed visit and one outbox mutation, and a test that
 * mounted the panel alone would prove the panel renders.
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

/** A phone that answers, or one that does not. Check-out never waits on either (`BR-VIS-3`). */
function locate(at: { latitude: number; longitude: number } | null) {
  Object.defineProperty(globalThis.navigator, "geolocation", {
    configurable: true,
    value: {
      getCurrentPosition: (onSuccess: PositionCallback, onError?: PositionErrorCallback) =>
        at
          ? onSuccess({ coords: { ...at, accuracy: 9 }, timestamp: 0 } as GeolocationPosition)
          : onError?.({ code: 3, message: "" } as GeolocationPositionError),
    },
  });
}

/**
 * The recap section, by its heading (W9 slice 10).
 *
 * Two lists on this screen name steps and they say different things — the recap's *optional and not
 * done*, the check-out panel's *mandatory and blocking*. Every assertion about either has to say
 * which, or it passes on the other's contents.
 */
const recap = () => screen.getByText("Before you finish").closest("section")!;

/** The block that names what `BR-VIS-3` is holding the rep for. */
const blockedBySteps = () => screen.getByRole("status");

let db: FieldKitDatabase;

beforeEach(async () => {
  replace.mockClear();
  db = new FieldKitDatabase(`checkout:${crypto.randomUUID()}`);
  sync.current = { db, pending: 0, running: false, outcome: null, syncNow: vi.fn() };

  await db.outlets.add(SHOP);
  locate({ latitude: 44.4639, longitude: 26.0947 });
});

afterEach(async () => {
  await db.delete();
  closeDatabase();
});

describe("finishing a visit", () => {
  it("seals it and queues exactly one mutation", async () => {
    // The shape `OFF-01` asks for: a visit is worked entirely on the device and reaches the server
    // as one `CapturedVisit`, never as a running commentary.
    await db.visits.add(visit([step({ order: 1, label: "Shelf check", completedAtUtc: "2026-03-17T09:05:00.000Z" })]));

    render(<Visit visitId="visit-1" />);

    await userEvent.click(await screen.findByRole("button", { name: "Check out" }));

    await waitFor(async () => expect((await db.visits.get("visit-1"))?.status).toBe("checkedOut"));

    const queued = await pending(db);

    expect(queued).toHaveLength(1);
    expect(queued[0]).toMatchObject({ type: "CapturedVisit", subjectId: "visit-1" });
  });

  it("records where the phone was, and does not judge it", async () => {
    // Two points are a cheap counter against a visit that was never really worked. A geofence rule
    // at this end would prompt a rep who has done the job and walked to the car.
    locate({ latitude: 44.5, longitude: 26.2 });
    await db.visits.add(visit([]));

    render(<Visit visitId="visit-1" />);

    await userEvent.click(await screen.findByRole("button", { name: "Check out" }));

    await waitFor(async () => {
      const sealed = await db.visits.get("visit-1");

      expect(sealed?.checkOutLatitude).toBe(44.5);
      expect(sealed?.checkOutLongitude).toBe(26.2);
      expect(sealed?.status).toBe("checkedOut");
    });

    // Two kilometres away and nothing said about it: there is no second geofence.
    expect(screen.queryByText(/outside/i)).toBeNull();
  });

  it("finishes with no fix at all rather than keeping the rep in the shop", async () => {
    locate(null);
    await db.visits.add(visit([]));

    render(<Visit visitId="visit-1" />);

    await userEvent.click(await screen.findByRole("button", { name: "Check out" }));

    await waitFor(async () => {
      const sealed = await db.visits.get("visit-1");

      expect(sealed?.status).toBe("checkedOut");
      expect(sealed?.checkOutLatitude).toBeNull();
    });
  });

  it("goes back to the round, where the stop now answers both questions at once", async () => {
    await db.visits.add(visit([]));

    render(<Visit visitId="visit-1" />);

    await userEvent.click(await screen.findByRole("button", { name: "Check out" }));

    await waitFor(() => expect(replace).toHaveBeenCalledWith("/field"));
  });
});

describe("what BR-VIS-3 refuses", () => {
  it("names the outstanding steps before the rep tries to leave, not after", async () => {
    // Being told at the door is the version of this rule that sends a rep back into a shop they have
    // walked out of. The list is on screen the whole time.
    await db.visits.add(
      visit([
        step({ order: 1, mandatory: true, label: "Shelf check" }),
        step({ order: 2, mandatory: true, label: "Fridge photo" }),
        step({ order: 3, label: "Optional chat" }),
      ]),
    );

    render(<Visit visitId="visit-1" />);

    expect(await screen.findByText("2 steps still have to be done before you can finish:")).toBeTruthy();

    // Scoped to the outstanding block: the recap above it lists the *optional* steps left (W9
    // slice 10), so an unscoped `getAllByRole("listitem")` now spans two lists that say different
    // things — and a test that mixed them would pass on either one's contents.
    const outstanding = within(blockedBySteps()).getAllByRole("listitem").map((row) => row.textContent);

    expect(outstanding).toContain("Shelf check");
    expect(outstanding).toContain("Fridge photo");
    expect(outstanding).not.toContain("Optional chat");
  });

  it("refuses the check-out itself, because the list alone is not the rule", async () => {
    await db.visits.add(visit([step({ order: 1, mandatory: true, label: "Shelf check" })]));

    render(<Visit visitId="visit-1" />);

    // Live rather than disabled: a rep who taps gets told why, which is more use than a dead control.
    await userEvent.click(await screen.findByRole("button", { name: "Check out" }));

    expect(await screen.findByText("The steps listed above still have to be done.")).toBeTruthy();
    expect((await db.visits.get("visit-1"))?.status).toBe("inProgress");
    expect(await pending(db)).toEqual([]);
  });

  it("lets the rep out once the last mandatory step is done", async () => {
    await db.visits.add(visit([step({ order: 1, mandatory: true, label: "Shelf check" })]));

    render(<Visit visitId="visit-1" />);

    await userEvent.click(await screen.findByRole("button", { name: "Mark done" }));
    await waitFor(() =>
      expect(screen.queryByText(/still ha(s|ve) to be done before you can finish/)).toBeNull(),
    );

    await userEvent.click(screen.getByRole("button", { name: "Check out" }));

    await waitFor(async () => expect((await db.visits.get("visit-1"))?.status).toBe("checkedOut"));
  });
});

describe("the outcome", () => {
  it("defaults to productive and stores no reason for it", async () => {
    await db.visits.add(visit([]));

    render(<Visit visitId="visit-1" />);

    await userEvent.click(await screen.findByRole("button", { name: "Check out" }));

    await waitFor(async () => {
      const sealed = await db.visits.get("visit-1");

      expect(sealed?.outcome).toBe("Productive");
      expect(sealed?.outcomeReason).toBeNull();
    });
  });

  it("asks why nothing came of the call, and refuses to seal without it", async () => {
    await db.visits.add(visit([]));

    render(<Visit visitId="visit-1" />);

    await userEvent.click(await screen.findByLabelText("Nothing came of it"));
    await userEvent.click(screen.getByRole("button", { name: "Check out" }));

    expect(await screen.findByText("Say what happened before finishing.")).toBeTruthy();
    expect((await db.visits.get("visit-1"))?.status).toBe("inProgress");

    await userEvent.type(screen.getByRole("textbox"), "Closed early for a stock take");
    await userEvent.click(screen.getByRole("button", { name: "Check out" }));

    await waitFor(async () => {
      const sealed = await db.visits.get("visit-1");

      expect(sealed?.outcome).toBe("NonProductive");
      expect(sealed?.outcomeReason).toBe("Closed early for a stock take");
    });
  });

  it("does not ask for a reason on a productive call", async () => {
    await db.visits.add(visit([]));

    render(<Visit visitId="visit-1" />);

    await screen.findByRole("button", { name: "Check out" });

    expect(screen.queryByRole("textbox")).toBeNull();
  });
});

describe("a visit that is already sealed", () => {
  it("reads back the three facts check-out produced", async () => {
    await db.visits.add(
      visit([], {
        status: "checkedOut",
        checkedOutAtUtc: "2026-03-17T09:23:30.000Z",
        outcome: "NonProductive",
        outcomeReason: "Closed early for a stock take",
      }),
    );

    render(<Visit visitId="visit-1" />);

    expect(await screen.findByText("Nothing came of it")).toBeTruthy();
    expect(screen.getByText("Closed early for a stock take")).toBeTruthy();

    // Derived, never stored — check-out minus check-in, and 23.5 minutes reads as 23 (`BR-VIS-5`).
    expect(screen.getByText("23 minutes")).toBeTruthy();
  });

  it("offers no way to finish it twice", async () => {
    await db.visits.add(
      visit([], { status: "checkedOut", checkedOutAtUtc: "2026-03-17T09:25:00.000Z", outcome: "Productive" }),
    );

    render(<Visit visitId="visit-1" />);

    await screen.findByText("This visit is finished. Nothing here can be changed.");

    expect(screen.queryByRole("button", { name: "Check out" })).toBeNull();
  });
});

describe("the recap before checking out (VIS-09, W9 slice 10)", () => {
  it("names the optional steps nothing else will stop the rep leaving behind", async () => {
    // The one thing on this screen a rep can still act on that no other list tells them about:
    // `BR-VIS-3` gates on mandatory steps, so the check-out panel names those and stops.
    await db.visits.add(
      visit([
        step({ order: 1, mandatory: true, label: "Shelf check", completedAtUtc: "2026-03-17T09:05:00.000Z" }),
        step({ order: 2, label: "Fridge photo" }),
      ]),
    );

    render(<Visit visitId="visit-1" />);

    expect(await screen.findByText("1 optional step is not done:")).toBeTruthy();

    // Scoped: "Fridge photo" is also the label of the step row further up the screen, and an
    // unscoped query would pass on that one whether or not the recap listed anything.
    expect(within(recap()).getByText("Fridge photo")).toBeTruthy();
  });

  it("does not repeat the mandatory ones the check-out panel already lists", async () => {
    await db.visits.add(visit([step({ order: 1, mandatory: true, label: "Shelf check" })]));

    render(<Visit visitId="visit-1" />);

    await screen.findByText("1 step still has to be done before you can finish:");

    expect(screen.queryByText(/optional step/)).toBeNull();
  });

  it("gathers what the rep wrote, which is otherwise scattered under its steps", async () => {
    await db.visits.add(
      visit([
        step({
          order: 1,
          type: "Note",
          label: "Anything to report?",
          notes: "Manager asked about the promotion end date",
          completedAtUtc: "2026-03-17T09:05:00.000Z",
        }),
      ]),
    );

    render(<Visit visitId="visit-1" />);

    expect(await screen.findByText("What you wrote")).toBeTruthy();
    expect(
      screen.getAllByText("Manager asked about the promotion end date").length,
    ).toBeGreaterThanOrEqual(1);
  });

  it("counts the time in the shop while the visit is still open", async () => {
    // Not available anywhere else until the visit is sealed — and the case the sealed record's own
    // copy of this derivation answered zero for.
    vi.setSystemTime(new Date("2026-03-17T09:18:40.000Z"));
    await db.visits.add(visit([]));

    render(<Visit visitId="visit-1" />);

    expect(await screen.findByText("18 minutes")).toBeTruthy();

    vi.useRealTimers();
  });

  it("says check-out is final, because that is what a recap is for", async () => {
    await db.visits.add(visit([]));

    render(<Visit visitId="visit-1" />);

    expect(
      await screen.findByText(
        "Checking out files this visit. It cannot be changed afterwards, on this phone or in the back office.",
      ),
    ).toBeTruthy();
  });

  it("is gone once the visit is sealed — there is nothing left to review", async () => {
    await db.visits.add(
      visit([], { status: "checkedOut", checkedOutAtUtc: "2026-03-17T09:25:00.000Z", outcome: "Productive" }),
    );

    render(<Visit visitId="visit-1" />);

    await screen.findByText("This visit is finished. Nothing here can be changed.");

    expect(screen.queryByText("Before you finish")).toBeNull();
  });
});
