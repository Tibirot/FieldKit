// @vitest-environment jsdom

import "fake-indexeddb/auto";

import { screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { Reschedule } from "@/components/field/reschedule";
import type { SyncContextValue } from "@/components/sync/sync-provider";
import { closeDatabase, FieldKitDatabase, type ReferencePlannedVisit } from "@/lib/sync/db";
import { markRejected, pending } from "@/lib/sync/outbox";
import { eventually } from "@/test/eventually";
import { render } from "@/test/render";

/**
 * Moving a call to another day, from the shop screen (`JRN-06`, `BR-JRN-4`) — W12 F2b.
 *
 * Against a **real** database, because what this component does is read a window off the round and
 * hand it to a date input. A test that stubbed the store would assert that a component renders its
 * props.
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

const MONDAY = "2026-04-06";
const SUNDAY = "2026-04-12";

function call(overrides: Partial<ReferencePlannedVisit> = {}): ReferencePlannedVisit {
  return {
    id: "call-1",
    outletId: "outlet-1",
    date: MONDAY,
    status: "Planned",
    source: "Generated",
    notVisitedReason: null,
    rowVersion: 7,
    movableFrom: MONDAY,
    movableTo: SUNDAY,
    ...overrides,
  };
}

let db: FieldKitDatabase;

beforeEach(() => {
  replace.mockClear();

  db = new FieldKitDatabase(`reschedulescreen:${crypto.randomUUID()}`);
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

describe("<Reschedule>", () => {
  it("stays a single button until a rep asks for it", async () => {
    await db.plannedVisits.add(call());

    render(<Reschedule plannedVisitId="call-1" />);

    // A date picker sitting open under every check-in screen makes moving a call as easy as working
    // one, which is the shape `NotVisited` refused for *skip* and the same argument applies here.
    expect(await screen.findByRole("button", { name: "Come back another day?" })).toBeTruthy();
    expect(screen.queryByLabelText("Which day?")).toBeNull();
  });

  it("offers only the days BR-JRN-4 allows, straight from the round", async () => {
    await db.plannedVisits.add(call());

    render(<Reschedule plannedVisitId="call-1" />);

    await userEvent.click(await screen.findByRole("button", { name: "Come back another day?" }));

    const input = (await screen.findByLabelText("Which day?")) as HTMLInputElement;

    // <b>The window is the server's, not this component's.</b> `min`/`max` are the two fields F2a
    // added; nothing here knows what a cycle is, which is the whole point of sending the answer
    // rather than the cycle length and the plan's first day.
    expect(input.min).toBe(MONDAY);
    expect(input.max).toBe(SUNDAY);

    // And in words, because a native picker's greyed-out days are not something every rep will read
    // as a rule they are being told about.
    expect(screen.getByText(`Any day from ${MONDAY} to ${SUNDAY}.`)).toBeTruthy();
  });

  it("offers nothing at all for a call that may not be moved", async () => {
    /*
     * An unplanned call belongs to no cycle, so the server sends null and refuses every move. The
     * same is true of any call held from before local store version 21, for the minute until the
     * next pull.
     *
     * <b>Rendering nothing rather than a disabled button</b>: a control a rep can see and never use
     * is a question they will ask their supervisor, and the answer — "that call was never in a
     * cycle" — is not one the shop screen can usefully give.
     */
    await db.plannedVisits.bulkAdd([
      call({ id: "unmovable", source: "Unplanned", movableFrom: null, movableTo: null }),
      call({ id: "movable" }),
    ]);

    /*
     * <b>Two of them, and the second is load-bearing rather than tidy.</b>
     *
     * The obvious form of this test — mount the unmovable call and assert the button is absent —
     * passes for the wrong reason: the component renders nothing while its live query is still
     * loading, so the assertion is satisfied at the first poll, before anything has been read.
     * Wrapping it in `eventually` does not help, because `eventually` stops at the first success.
     *
     * A movable sibling gives the absence something to be measured against. Waiting for *its*
     * button proves the store has been read and rendered; the count is then the claim.
     */
    render(
      <>
        <Reschedule plannedVisitId="unmovable" />
        <Reschedule plannedVisitId="movable" />
      </>,
    );

    await screen.findByRole("button", { name: "Come back another day?" });

    expect(screen.getAllByRole("button", { name: "Come back another day?" })).toHaveLength(1);
  });

  it("queues the move under the call and goes back to the round", async () => {
    await db.plannedVisits.add(call());

    render(<Reschedule plannedVisitId="call-1" />);

    await userEvent.click(await screen.findByRole("button", { name: "Come back another day?" }));

    const input = await screen.findByLabelText("Which day?");
    await userEvent.clear(input);
    await userEvent.type(input, "2026-04-08");

    await userEvent.click(screen.getByRole("button", { name: "Move it" }));

    await eventually(async () => {
      const queued = await pending(db);

      expect(queued).toHaveLength(1);
      expect(queued[0]).toMatchObject({ type: "RescheduledCall", subjectId: "call-1" });
      expect(queued[0].payload).toEqual({ plannedVisitId: "call-1", date: "2026-04-08" });
    });

    // Back to the round, where the stop now says where it went. It is still *on* the round: the
    // device does not rewrite `ref_planned_visits`, so the call leaves today when the server agrees.
    expect(replace).toHaveBeenCalledWith("/field");
  });

  it("will not move a call to the day it is already on", async () => {
    /*
     * The server treats this as a no-op and answers success — so a refusal code would be the device
     * inventing vocabulary the protocol does not have. What it must not do is *queue* it: the rep
     * would get a sync badge and a "you moved this" line for a call that never moved.
     */
    await db.plannedVisits.add(call());

    render(<Reschedule plannedVisitId="call-1" />);

    await userEvent.click(await screen.findByRole("button", { name: "Come back another day?" }));

    const input = await screen.findByLabelText("Which day?");
    await userEvent.clear(input);
    await userEvent.type(input, MONDAY);

    expect(screen.getByRole("button", { name: "Move it" })).toHaveProperty("disabled", true);
    expect(await pending(db)).toEqual([]);
  });

  it("shows the day it queued instead of offering the picker again", async () => {
    /*
     * A second move would be accepted by the server — it moves the call again, from wherever the
     * first put it — and the rep would have chosen it against the window of a date they never saw
     * the call on. So the screen reports rather than re-offers.
     */
    await db.plannedVisits.add(call());
    await db.outbox.add({
      mutationId: crypto.randomUUID(),
      type: "RescheduledCall",
      subjectId: "call-1",
      payload: { plannedVisitId: "call-1", date: "2026-04-08" },
      status: "pending",
      attempts: 0,
      createdAt: Date.now(),
      errorCode: "",
      errorDetail: "",
    });

    render(<Reschedule plannedVisitId="call-1" />);

    expect(await screen.findByText("You moved this call to 2026-04-08.")).toBeTruthy();
    expect(screen.queryByRole("button", { name: "Come back another day?" })).toBeNull();

    // And why the call is still on today's round, which is the question a rep asks next.
    expect(screen.getByText("It stays on today's round until your supervisor has it.")).toBeTruthy();
  });

  it("says so when the server refused the move", async () => {
    // `OFF-09`. The one case a rep has to do something about — a plan regenerated under them, most
    // likely — and a screen that went on saying "moved to Wednesday" would be lying to them.
    await db.plannedVisits.add(call());

    const mutationId = crypto.randomUUID();

    await db.outbox.add({
      mutationId,
      type: "RescheduledCall",
      subjectId: "call-1",
      payload: { plannedVisitId: "call-1", date: "2026-04-08" },
      status: "pending",
      attempts: 0,
      createdAt: Date.now(),
      errorCode: "",
      errorDetail: "",
    });

    await markRejected(db, mutationId, "journey.visit.unknown", "No such call.");

    render(<Reschedule plannedVisitId="call-1" />);

    expect(await screen.findByText("The move was refused. Tell your supervisor.")).toBeTruthy();
  });
});
