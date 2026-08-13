// @vitest-environment jsdom

import "fake-indexeddb/auto";

import { screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { UnplannedCall } from "@/components/field/unplanned-call";
import type { SyncContextValue } from "@/components/sync/sync-provider";
import { closeDatabase, FieldKitDatabase, type ReferenceOutlet } from "@/lib/sync/db";
import { enqueue, markRejected } from "@/lib/sync/outbox";
import { eventually } from "@/test/eventually";
import { render } from "@/test/render";

/**
 * Starting a call at a shop that is not on today's round (`JRN-06`) — W11½ R4.
 *
 * Rendered against a **real** database, like the journey screen it hangs beneath: what this section
 * is for is reading the local store, and a test that stubbed the reader would assert the component
 * renders an array.
 */
const sync = vi.hoisted(() => ({ current: {} as SyncContextValue }));

vi.mock("@/components/sync/sync-provider", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/components/sync/sync-provider")>()),
  useSync: () => sync.current,
}));

const TODAY = "2026-03-17";

function outlet(id: string, name: string, code: string): ReferenceOutlet {
  return {
    id,
    code,
    name,
    channelId: "channel-1",
    segment: "A",
    status: "Active",
    latitude: null,
    longitude: null,
    countryCode: "RO",
    radiusMetres: 150,
    rowVersion: 1,
  };
}

let db: FieldKitDatabase;

beforeEach(async () => {
  db = new FieldKitDatabase(`unplannedcall:${crypto.randomUUID()}`);
  sync.current = {
    db,
    photographs: 0,
    pending: 0,
    failed: 0,
    running: false,
    outcome: null,
    syncNow: vi.fn(),
  };

  await db.outlets.bulkAdd([
    outlet("outlet-1", "Corner Shop", "RO-0001"),
    outlet("outlet-2", "Kiosk 1 Mai", "RO-0002"),
    outlet("outlet-3", "Mega Image Dorobanți", "RO-0003"),
  ]);
});

afterEach(async () => {
  await db.delete();
  closeDatabase();
});

describe("<UnplannedCall>", () => {
  it("stays out of the way until a rep asks for it", async () => {
    render(<UnplannedCall date={TODAY} />);

    // The round is what a rep should be working. A list of every shop on the territory sitting open
    // above it invites a day worked off the plan.
    expect(await screen.findByRole("button", { name: "Calling somewhere else?" })).toBeTruthy();
    expect(screen.queryByText("Corner Shop")).toBeNull();
  });

  it("offers the shops this device holds", async () => {
    render(<UnplannedCall date={TODAY} />);

    await userEvent.click(await screen.findByRole("button", { name: "Calling somewhere else?" }));

    expect(await screen.findByText("Corner Shop")).toBeTruthy();
    expect(screen.getByText("Kiosk 1 Mai")).toBeTruthy();
    expect(screen.getByText("RO-0003")).toBeTruthy();
  });

  it("leaves out a shop already on today's round", async () => {
    // The round already offers it, and a stop opened from here would not carry the planned call —
    // so the supervisor's coverage figure would show the call still outstanding.
    await db.plannedVisits.add({
      id: "call-1",
      outletId: "outlet-2",
      date: TODAY,
      status: "Planned",
      source: "Generated",
      notVisitedReason: null,
      rowVersion: 1,
    });

    render(<UnplannedCall date={TODAY} />);

    await userEvent.click(await screen.findByRole("button", { name: "Calling somewhere else?" }));

    expect(await screen.findByText("Corner Shop")).toBeTruthy();
    await eventually(() => expect(screen.queryByText("Kiosk 1 Mai")).toBeNull());
  });

  it("sends the rep to check-in with no planned call, which is what makes it unplanned", async () => {
    render(<UnplannedCall date={TODAY} />);

    await userEvent.click(await screen.findByRole("button", { name: "Calling somewhere else?" }));

    const link = await screen.findByRole("link", { name: /Corner Shop/ });

    // The absence of `?call=` is the whole mechanism: the shop screen treats a missing planned call
    // as an unplanned visit. A link that carried one would be the same call reached another way.
    expect(link.getAttribute("href")).toBe("/en/field/outlets/outlet-1");
  });

  it("narrows by code, which is what tells two shops of a chain apart", async () => {
    render(<UnplannedCall date={TODAY} />);

    await userEvent.click(await screen.findByRole("button", { name: "Calling somewhere else?" }));
    await userEvent.type(await screen.findByLabelText("Which shop?"), "RO-0002");

    expect(await screen.findByText("Kiosk 1 Mai")).toBeTruthy();
    await eventually(() => expect(screen.queryByText("Corner Shop")).toBeNull());
  });

  it("says why a call here was refused — the only place it can be seen (W11½ R5)", async () => {
    /*
     * An unplanned call is queued under the *shop's* id, and the round has no row for a shop it
     * never planned — so this list is the only surface that can carry the answer.
     *
     * The refusal used here is the one regression F9 hit in a browser: the server takes an unplanned
     * call only onto a published round covering the day, and the dev rep had none. Before R5 that
     * reached the rep as an unexplained pending count.
     */
    const entry = await enqueue(db, {
      type: "UnplannedCall",
      subjectId: "outlet-1",
      payload: { outletId: "outlet-1", date: TODAY },
    });

    await markRejected(
      db,
      entry.mutationId,
      "journey.plan.noneForDate",
      "You have no published round covering that day.",
    );

    render(<UnplannedCall date={TODAY} />);

    await userEvent.click(await screen.findByRole("button", { name: "Calling somewhere else?" }));

    expect((await screen.findByRole("alert")).textContent).toBe(
      "You have no published round covering that day.",
    );
  });

  it("keeps one shop's refusal off another shop's row", async () => {
    // Both are queued under an outlet id, so a reader that took the first failure in the outbox
    // would blame whichever shop happened to be rendered first.
    const entry = await enqueue(db, {
      type: "UnplannedCall",
      subjectId: "outlet-1",
      payload: { outletId: "outlet-1", date: TODAY },
    });

    await markRejected(db, entry.mutationId, "journey.plan.noneForDate", "No round covers that day.");

    render(<UnplannedCall date={TODAY} />);

    await userEvent.click(await screen.findByRole("button", { name: "Calling somewhere else?" }));
    await screen.findByRole("alert");

    // One row explains itself; the other three say nothing.
    expect(screen.getAllByRole("alert")).toHaveLength(1);
  });

  it("says so when there is nothing to call at", async () => {
    await db.outlets.clear();

    render(<UnplannedCall date={TODAY} />);

    await userEvent.click(await screen.findByRole("button", { name: "Calling somewhere else?" }));

    expect(await screen.findByText(/No other shops on this device/)).toBeTruthy();
  });
});
