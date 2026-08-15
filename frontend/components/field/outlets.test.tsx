// @vitest-environment jsdom

import "fake-indexeddb/auto";

import { screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { FieldOutlets } from "@/components/field/outlets";
import type { SyncContextValue } from "@/components/sync/sync-provider";
import { closeDatabase, FieldKitDatabase, type ReferenceOutlet } from "@/lib/sync/db";
import { eventually } from "@/test/eventually";
import { render } from "@/test/render";

/**
 * The rep's whole territory (`A4`) — W12½ slice 8a.
 *
 * Rendered against a **real** database, like the unplanned picker beside it: what this screen is for
 * is reading the local store, and a test that stubbed the reader would assert that a component can
 * render an array.
 *
 * The distinction under test is the one that made this screen necessary at all — the picker lists
 * only shops **not** on today's round, and this lists them all.
 */
const sync = vi.hoisted(() => ({ current: {} as SyncContextValue }));

vi.mock("@/components/sync/sync-provider", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/components/sync/sync-provider")>()),
  useSync: () => sync.current,
}));

vi.mock("@/i18n/navigation", () => ({
  Link: ({ href, children }: { href: string; children: React.ReactNode }) => (
    <a href={href}>{children}</a>
  ),
}));

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
    timeZoneId: "Europe/Bucharest",
    radiusMetres: 150,
    rowVersion: 1,
  };
}

let db: FieldKitDatabase;

/** The shops on screen, in the order a rep reads them. */
const listed = () => screen.getAllByRole("listitem").map((row) => row.textContent?.trim());

beforeEach(async () => {
  db = new FieldKitDatabase(`fieldoutlets:${crypto.randomUUID()}`);
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
    outlet("outlet-2", "Kiosk 1 Mai", "RO-0002"),
    outlet("outlet-1", "Corner Shop", "RO-0001"),
    outlet("outlet-3", "Mega Image Dorobanți", "RO-0003"),
  ]);
});

afterEach(async () => {
  await db.delete();
  closeDatabase();
});

describe("<FieldOutlets>", () => {
  it("lists every shop the device holds, by name", async () => {
    /*
     * **By name, not by insertion order** — the rows go in shuffled above so this cannot pass by
     * accident. A rep looking a shop up scans alphabetically; `outlets()` orders on the `name`
     * index, and this is the assertion that says so.
     */
    render(<FieldOutlets />);

    await eventually(() => expect(screen.getAllByRole("listitem")).toHaveLength(3));

    expect(listed()).toEqual([
      "Corner ShopRO-0001",
      "Kiosk 1 MaiRO-0002",
      "Mega Image DorobanțiRO-0003",
    ]);
  });

  it("includes a shop that is already on today's round", async () => {
    /*
     * The whole reason this screen exists. The unplanned-call picker filters those out on purpose —
     * it is asking "where could I add a call?" — which makes it useless for "where is that shop?".
     * This screen holds no plan and asks no such question.
     */
    await db.plannedVisits.add({
      id: "visit-1",
      outletId: "outlet-1",
      date: "2026-03-17",
      sequence: 1,
      status: "Planned",
      cycleLengthDays: 7,
      planStartDate: "2026-03-16",
      planEndDate: "2026-03-22",
      rowVersion: 1,
    } as never);

    render(<FieldOutlets />);

    await eventually(() => expect(screen.getAllByRole("listitem")).toHaveLength(3));
    expect(listed()[0]).toContain("Corner Shop");
  });

  it("finds a shop by name or by code", async () => {
    render(<FieldOutlets />);
    await eventually(() => expect(screen.getAllByRole("listitem")).toHaveLength(3));

    await userEvent.type(screen.getByRole("searchbox"), "dorob");
    await eventually(() => expect(listed()).toEqual(["Mega Image DorobanțiRO-0003"]));

    await userEvent.clear(screen.getByRole("searchbox"));
    await userEvent.type(screen.getByRole("searchbox"), "RO-0002");
    await eventually(() => expect(listed()).toEqual(["Kiosk 1 MaiRO-0002"]));
  });

  it("goes to the shop, with no call named", async () => {
    /*
     * The absence of `?call=` is the mechanism rather than an omission: the shop screen reads a
     * missing planned call as an unplanned visit, which is what the picker relies on too. Browsing
     * to a shop and starting a call there are the same journey, and this is its front door.
     */
    render(<FieldOutlets />);
    await eventually(() => expect(screen.getAllByRole("listitem")).toHaveLength(3));

    expect(screen.getByRole("link", { name: /Corner Shop/ }).getAttribute("href")).toBe(
      "/field/outlets/outlet-1",
    );
  });

  it("says nothing about emptiness until it has looked", async () => {
    // Three states, not two — the rule check-in and the picker both state at length. A rep who
    // reads "no shops" for one frame on a device holding four hundred learns not to trust it.
    await db.outlets.clear();

    render(<FieldOutlets />);

    expect(screen.queryByText("No shops match.")).toBeNull();
    await eventually(() => expect(screen.getByText("No shops match.")).toBeTruthy());
  });

  it("counts what it is showing, so a half-synced device is visible", async () => {
    // A rep who knows their territory has about four hundred shops can tell a filtered list from a
    // half-finished sync at a glance — but only if the number is on screen.
    render(<FieldOutlets />);

    await eventually(() => expect(screen.getByText("3 shops")).toBeTruthy());

    await userEvent.type(screen.getByRole("searchbox"), "corner");
    await eventually(() => expect(screen.getByText("1 shop")).toBeTruthy());
  });
});
