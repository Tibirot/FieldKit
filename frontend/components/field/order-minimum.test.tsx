// @vitest-environment jsdom

import "fake-indexeddb/auto";

import { cleanup, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { Order } from "@/components/field/order";
import type { SyncContextValue } from "@/components/sync/sync-provider";
import { draft, orderFor } from "@/lib/orders/local-order";
import {
  closeDatabase,
  FieldKitDatabase,
  type ReferenceOrderMinimum,
  type ReferenceOutlet,
} from "@/lib/sync/db";
import { eventually } from "@/test/eventually";
import { render } from "@/test/render";

/**
 * The order minimum a rep meets before the server does (`ORD-06`, `BR-ORD-5`) — W11 slice 8b-ii.
 *
 * The rule and the store have their own suites (`lib/pricing/order-minimum.test.ts`,
 * `lib/sync/order-minimums.test.ts`). What is only visible from here is that the screen *refuses* —
 * and refuses **before** the store is touched, so an order under the minimum stays a draft the rep
 * can add to rather than a rejection they find out about on sync tomorrow.
 *
 * **These tests found a real bug in the screen, and it is worth recording how.** Written first as a
 * describe inside `order.test.tsx`, they went red on a *different* test each run — including one of
 * the pre-existing ones. Splitting the file made it look fixed, which it was not: the cause was that
 * `priced` re-subscribed its live query on every edit, and that re-subscribe intermittently produced
 * an observable that never emitted, with no error to log. A rep would have seen a priced line render
 * as "No price" permanently while the store held the right number. `<Order>` now reads the order
 * inside the query instead. The file stayed split because a distinct rule reads better on its own.
 *
 * Six cases of Cola at 4.50 is 27.00 RON net, and every threshold below is chosen against that.
 */
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
  countryCode: "RO",
  latitude: 44.4638,
  longitude: 26.0946,
  timeZoneId: "Europe/Bucharest",
  radiusMetres: 150,
  rowVersion: 4,
};

const catalogue = async () =>
  within(await screen.findByRole("list", { name: "What this shop can be sold" }));

const lines = async () => within(await screen.findByRole("list", { name: "On this order" }));

let db: FieldKitDatabase;

beforeEach(async () => {
  db = new FieldKitDatabase(`minimum:${crypto.randomUUID()}`);
  sync.current = { db, pending: 0, failed: 0, photographs: 0, running: false, outcome: null, syncNow: vi.fn() };

  await db.outlets.add(SHOP);
  await db.visits.add({
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
    steps: [],
    checkedOutAtUtc: null,
    checkOutLatitude: null,
    checkOutLongitude: null,
    outcome: null,
    outcomeReason: null,
  });

  await db.products.add({
    id: "p-1",
    sku: "P-1",
    name: "Cola 500ml",
    brandId: null,
    categoryId: null,
    taxClassId: null,
    unitOfMeasure: "case",
    packSize: 12,
    status: "Active",
    rowVersion: 1,
  });

  // A channel list in effect from long ago and never ending, so the only reason an order is refused
  // is the minimum each test configures.
  await db.priceLists.add({
    id: "list-1",
    name: "Standard",
    currency: "RON",
    effectiveFrom: "2020-01-01",
    effectiveTo: null,
    rowVersion: 1,
  });
  await db.priceAssignments.add({
    id: "assign-1",
    priceListId: "list-1",
    channelId: "channel-1",
    outletId: null,
    rowVersion: 1,
  });
  await db.priceLines.add({
    id: "pl-1",
    priceListId: "list-1",
    productId: "p-1",
    amount: "4.50",
    rowVersion: 1,
  });
  await db.assortment.add({
    id: "a-1",
    channelId: "channel-1",
    productId: "p-1",
    isMustStock: true,
    rowVersion: 1,
  });
});

afterEach(async () => {
  // Unmounted before the database is dropped — `order.test.tsx` explains why at length: Testing
  // Library's own cleanup runs last, and `useLive` treats a failed observation as terminal.
  cleanup();

  await db.delete();
  closeDatabase();
});

/** Adds one line of six cases — 27.00 RON net — and waits for the price to reach the screen. */
async function withALine() {
  render(<Order visitId="visit-1" />);

  await userEvent.type((await catalogue()).getByLabelText("How many Cola 500ml"), "6");
  await userEvent.click((await catalogue()).getAllByRole("button", { name: "Add" })[0]);

  await waitFor(async () => expect((await draft(db, "visit-1"))?.lines).toHaveLength(1));

  // Waiting on the store is not enough: the minimum is checked against the *live* price, and
  // clicking in the window before it lands answers "cannot be checked" rather than the verdict each
  // test is about. That window is real for a rep too, which is why the refusal exists.
  expect(await (await lines()).findByText("27.00 RON")).toBeTruthy();
}

async function configure(overrides: Partial<ReferenceOrderMinimum> = {}) {
  await db.orderMinimums.add({
    id: "min-1",
    channelId: "channel-1",
    outletId: null,
    amount: "50.00",
    currencyCode: "RON",
    rowVersion: 1,
    ...overrides,
  });
}

describe("<Order> and the order minimum", () => {
  it("refuses an order under the minimum, and leaves it editable", async () => {
    await configure();
    await withALine();

    await userEvent.click(screen.getByRole("button", { name: "Submit the order" }));

    expect((await screen.findByRole("alert")).textContent).toContain(
      "This shop's orders have to reach the minimum above.",
    );

    // Nothing sealed and nothing queued — the rep can still add the case that fixes it, which is the
    // only outcome worth leaving them with at a counter.
    expect(await db.outbox.count()).toBe(0);
    expect((await orderFor(db, "visit-1"))?.status).toBe("draft");
    expect(screen.getByRole("button", { name: "Remove" })).toBeTruthy();
  });

  it("shows the threshold before the rep taps, not only after", async () => {
    /*
     * What keeping resolution and the check apart bought. A rep who can see they need to reach 50
     * adds a case; one who finds out by being turned away has already decided the order was done.
     */
    await configure();
    await withALine();

    expect(await screen.findByText("This shop orders from 50.00 RON.")).toBeTruthy();
  });

  it("lets an order that meets the minimum through", async () => {
    await configure({ amount: "20.00" });
    await withALine();

    await userEvent.click(screen.getByRole("button", { name: "Submit the order" }));

    await waitFor(async () => expect(await db.outbox.count()).toBe(1));

    expect((await orderFor(db, "visit-1"))?.status).toBe("submitted");

    // The threshold line goes with the refusal it explained — a met minimum is a rule that has
    // stopped mattering, and one more number to read past on a small screen.
    await eventually(() => expect(screen.queryByText(/This shop orders from/)).toBeNull());
  });

  it("sends every order when no minimum is configured", async () => {
    // The behaviour every order has had until this slice, and what an unsynced device must keep:
    // `BR-ORD-5` applies a minimum *if configured*, and most tenants configure none.
    await withALine();

    await userEvent.click(screen.getByRole("button", { name: "Submit the order" }));

    await waitFor(async () => expect(await db.outbox.count()).toBe(1));
  });

  it("names a currency disagreement instead of calling the order too small", async () => {
    /*
     * 27 RON against a 20 EUR minimum. Comparing the digits alone would pass it, and on other numbers
     * would refuse an order comfortably over the intended threshold — either way looking exactly like
     * the rule working. A rep told "too small" would add stock nobody asked for and be refused again;
     * this is somebody's configuration to fix.
     */
    await configure({ amount: "20.00", currencyCode: "EUR" });
    await withALine();

    await userEvent.click(screen.getByRole("button", { name: "Submit the order" }));

    expect((await screen.findByRole("alert")).textContent).toContain(
      "set in a different currency to its prices",
    );

    expect(await db.outbox.count()).toBe(0);
  });

  it("prefers the shop's own minimum over its channel's", async () => {
    /*
     * The precedence, from the screen. The channel's 20 would pass this order; the shop's 500 does
     * not, so a lookup that ranked them the other way would seal an order it should have refused.
     *
     * **The ids sort against the answer, deliberately.** Written the obvious way round this test was
     * vacuous: sabotaging the scope so both candidates read as `Channel` left it green, because the
     * id tiebreak happened to pick the outlet's row anyway. `z-channel` beats `a-outlet` on id, so a
     * resolver that ignores scope now returns the 20 and this goes red.
     */
    await configure({ id: "z-channel", amount: "20.00" });
    await configure({ id: "a-outlet", channelId: null, outletId: "outlet-1", amount: "500.00" });
    await withALine();

    await userEvent.click(screen.getByRole("button", { name: "Submit the order" }));

    expect((await screen.findByRole("alert")).textContent).toContain(
      "This shop's orders have to reach the minimum above.",
    );

    expect(await db.outbox.count()).toBe(0);
  });
});
