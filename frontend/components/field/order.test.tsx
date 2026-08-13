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
  type LocalVisit,
  type ReferenceOutlet,
  type ReferenceProduct,
} from "@/lib/sync/db";
import { render } from "@/test/render";

/**
 * The order a rep builds at a counter (`ORD-01`, `ORD-02`, `ORD-03`) — W11 slice 7.
 *
 * Against a real database and the real `priceOrder`, because every claim worth making is about the
 * join: that what the rep sees is what the engine computed, and that what the store keeps is what
 * `/sync/push` will carry. A mocked pricing function would leave a screen that renders numbers
 * beautifully and stores the wrong ones.
 *
 * The arithmetic itself is not re-asserted here — it has cross-language vectors, and slice 7d's
 * tests cover the gathering. What is only visible from this level is the **wiring**: quantity in,
 * store written, totals on screen.
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
  radiusMetres: 150,
  rowVersion: 4,
};

function visit(overrides: Partial<LocalVisit> = {}): LocalVisit {
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
    steps: [],
    checkedOutAtUtc: null,
    checkOutLatitude: null,
    checkOutLongitude: null,
    outcome: null,
    outcomeReason: null,
    ...overrides,
  };
}

function product(id: string, name: string, overrides: Partial<ReferenceProduct> = {}): ReferenceProduct {
  return {
    id,
    sku: id.toUpperCase(),
    name,
    brandId: null,
    categoryId: null,
    taxClassId: null,
    unitOfMeasure: "case",
    packSize: 12,
    status: "Active",
    rowVersion: 1,
    ...overrides,
  };
}

const catalogue = async () =>
  within(await screen.findByRole("list", { name: "What this shop can be sold" }));

const lines = async () => within(await screen.findByRole("list", { name: "On this order" }));

let db: FieldKitDatabase;

beforeEach(async () => {
  db = new FieldKitDatabase(`order:${crypto.randomUUID()}`);
  sync.current = { db, pending: 0, failed: 0, photographs: 0, running: false, outcome: null, syncNow: vi.fn() };

  await db.outlets.add(SHOP);
  await db.visits.add(visit());

  await db.products.bulkAdd([product("p-1", "Cola 500ml"), product("p-2", "Water 2L")]);

  // A channel list, in effect from long ago and never ending — so the *only* reason a line prices
  // or does not is the thing each test is about.
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
  await db.priceLines.bulkAdd([
    { id: "pl-1", priceListId: "list-1", productId: "p-1", amount: "4.50", rowVersion: 1 },
    { id: "pl-2", priceListId: "list-1", productId: "p-2", amount: "6.30", rowVersion: 1 },
  ]);

  await db.assortment.bulkAdd([
    { id: "a-1", channelId: "channel-1", productId: "p-1", isMustStock: true, rowVersion: 1 },
    { id: "a-2", channelId: "channel-1", productId: "p-2", isMustStock: false, rowVersion: 1 },
  ]);
});

afterEach(async () => {
  /*
   * Unmounted **before** the database is deleted, and the order matters.
   *
   * Testing Library registers its own cleanup at import time and Vitest runs `afterEach` hooks
   * last-registered-first, so without this line the database is dropped while the previous test's
   * components are still subscribed. `useLive` treats a failed observation as terminal — an errored
   * observable never emits again — so the *next* test's screen could sit on its initial `null` and
   * render a priced line as "No price".
   *
   * It presented as one test failing only when run with the others, which is the shape of every
   * cross-test leak: isolation passes, the suite does not.
   */
  cleanup();

  await db.delete();
  closeDatabase();
});

describe("<Order> and what a rep can put on it", () => {
  it("offers the outlet's assortment, not the tenant's catalogue", async () => {
    /*
     * `BR-ORD-1` enforced by *not offering the line*. A rep who could add an off-assortment product
     * would build an order the server stores and rejects (slice 4b), which strands the work until
     * they can get back to it — and the refusal is free here, standing at the counter.
     */
    await db.products.add(product("p-3", "Premium Whisky"));

    render(<Order visitId="visit-1" />);

    expect(await (await catalogue()).findByText("Cola 500ml")).toBeTruthy();
    expect((await catalogue()).getByText("Water 2L")).toBeTruthy();
    expect(screen.queryByText("Premium Whisky")).toBeNull();
  });

  it("prices a line the moment it is added, and stores what the engine said", async () => {
    /*
     * The whole point of the screen, asserted at both ends: the number on screen and the number in
     * the store have to be the engine's, because `BR-ORD-2` promises the server's recomputation on
     * push will match, and `captured()` sends what the store holds.
     */
    render(<Order visitId="visit-1" />);

    await userEvent.type((await catalogue()).getByLabelText("How many Cola 500ml"), "6");
    await userEvent.click((await catalogue()).getAllByRole("button", { name: "Add" })[0]);

    expect(await (await lines()).findByText("27.00 RON")).toBeTruthy();
    expect((await lines()).getByText(/6 case/)).toBeTruthy();

    const held = await draft(db, "visit-1");

    expect(held?.currencyCode).toBe("RON");
    expect(held?.lines).toHaveLength(1);
    expect(held?.lines[0]).toMatchObject({
      productId: "p-1",
      quantity: "6",
      unitOfMeasure: "case",
      packSize: 12,
      unitPrice: "4.5",
      lineTotal: "27",
    });
  });

  it("shows the four numbers a shopkeeper is told", async () => {
    // Subtotal, discount, tax and total. Tax is the one the device has only been able to compute
    // since slice 7c, and the one the wire has nowhere to put — see the note on `<Totals>`.
    await db.products.put(product("p-1", "Cola 500ml", { taxClassId: "standard" }));
    await db.taxRates.add({
      id: "rate-1",
      taxClassId: "standard",
      countryCode: "RO",
      percentage: "19.00",
      effectiveFrom: "2020-01-01",
      effectiveTo: null,
      rowVersion: 1,
    });

    render(<Order visitId="visit-1" />);

    await userEvent.type(await (await catalogue()).findByLabelText("How many Cola 500ml"), "6");
    await userEvent.click((await catalogue()).getAllByRole("button", { name: "Add" })[0]);

    const totals = within(await screen.findByRole("region", { name: "Order total" }));

    expect(totals.getByText("27.00 RON")).toBeTruthy();
    expect(totals.getByText("5.13 RON")).toBeTruthy();
    expect(totals.getByText("32.13 RON")).toBeTruthy();
  });

  it("stores the net, not the gross, because that is the field the wire has", async () => {
    /*
     * The gap this screen ran into, pinned so it cannot be closed by accident.
     *
     * `OrderLine.LineTotal` is documented as "what the device made of the line **after any promotion
     * it applied**" — the *net*. There is no tax field anywhere on `CapturedOrder`, so the gross the
     * rep reads out to the shopkeeper is not the number the back office receives. Storing the gross
     * here would put tax into a column the server sums into a total that has no tax in it, and the
     * two sides would disagree by exactly the VAT on every order.
     */
    await db.products.put(product("p-1", "Cola 500ml", { taxClassId: "standard" }));
    await db.taxRates.add({
      id: "rate-1",
      taxClassId: "standard",
      countryCode: "RO",
      percentage: "19.00",
      effectiveFrom: "2020-01-01",
      effectiveTo: null,
      rowVersion: 1,
    });

    render(<Order visitId="visit-1" />);

    await userEvent.type(await (await catalogue()).findByLabelText("How many Cola 500ml"), "6");
    await userEvent.click((await catalogue()).getAllByRole("button", { name: "Add" })[0]);

    await waitFor(async () => expect((await draft(db, "visit-1"))?.lines).toHaveLength(1));

    const held = await draft(db, "visit-1");

    expect(held?.lines[0].lineTotal).toBe("27");
    expect(held?.total).toBe("27");
  });

  it("replaces the line when the same product is added twice", async () => {
    // A rep who picks a product again has changed their mind about the quantity, not asked for
    // twice as much — and the aggregate refuses a duplicate product outright, so summing here would
    // build an order the server is guaranteed to reject.
    render(<Order visitId="visit-1" />);

    await userEvent.type(await (await catalogue()).findByLabelText("How many Cola 500ml"), "6");
    await userEvent.click((await catalogue()).getAllByRole("button", { name: "Add" })[0]);

    await waitFor(async () => expect((await draft(db, "visit-1"))?.lines).toHaveLength(1));

    await userEvent.type((await catalogue()).getByLabelText("How many Cola 500ml"), "2");
    await userEvent.click((await catalogue()).getAllByRole("button", { name: "Add" })[0]);

    await waitFor(async () => expect((await draft(db, "visit-1"))?.lines[0].quantity).toBe("2"));

    expect((await draft(db, "visit-1"))?.lines).toHaveLength(1);
    expect(await (await lines()).findByText("9.00 RON")).toBeTruthy();
  });

  it("takes a line off again", async () => {
    render(<Order visitId="visit-1" />);

    await userEvent.type(await (await catalogue()).findByLabelText("How many Cola 500ml"), "6");
    await userEvent.click((await catalogue()).getAllByRole("button", { name: "Add" })[0]);

    await waitFor(async () => expect((await draft(db, "visit-1"))?.lines).toHaveLength(1));

    await userEvent.click(await screen.findByRole("button", { name: "Remove" }));

    await waitFor(async () => expect((await draft(db, "visit-1"))?.lines).toHaveLength(0));
  });

  it("refuses a quantity that is not a positive number, and says which", async () => {
    render(<Order visitId="visit-1" />);

    await userEvent.type(await (await catalogue()).findByLabelText("How many Cola 500ml"), "0");
    await userEvent.click((await catalogue()).getAllByRole("button", { name: "Add" })[0]);

    expect((await screen.findByRole("alert")).textContent).toContain(
      "Enter how many, as a number greater than zero.",
    );

    // Nothing was written, and no empty draft was left behind for slice 8 to wonder about.
    expect(await draft(db, "visit-1")).toBeUndefined();
  });

  it("refuses a product this shop has no price for, rather than adding it at nothing", async () => {
    /*
     * The engine's answer, not a second opinion: `priceOrder` reports the product as `unpriced` and
     * the screen refuses on that. Adding it anyway would put a line on the order that the totals
     * silently omit — a number the rep cannot check against anything.
     */
    await db.priceLines.delete("pl-2");

    render(<Order visitId="visit-1" />);

    await userEvent.type(await (await catalogue()).findByLabelText("How many Water 2L"), "3");
    await userEvent.click((await catalogue()).getAllByRole("button", { name: "Add" })[1]);

    expect((await screen.findByRole("alert")).textContent).toContain(
      "This shop has no price for that product today, so it cannot be ordered.",
    );

    expect(await draft(db, "visit-1")).toBeUndefined();
  });

  it("survives a reload, because the order is in the store and not in React", async () => {
    // `ORD-05` and `OFF-01b`. A phone that dies mid-order loses at most the number half-typed into
    // the box — which is why every add goes through Dexie rather than through component state.
    const first = render(<Order visitId="visit-1" />);

    await userEvent.type(await (await catalogue()).findByLabelText("How many Cola 500ml"), "6");
    await userEvent.click((await catalogue()).getAllByRole("button", { name: "Add" })[0]);

    await waitFor(async () => expect((await draft(db, "visit-1"))?.lines).toHaveLength(1));

    first.unmount();

    render(<Order visitId="visit-1" />);

    expect(await (await lines()).findByText("27.00 RON")).toBeTruthy();
  });

  it("will not take an order on a visit that is finished", async () => {
    // `BR-ORD-4` from the other end: a sealed visit cannot grow an order. Stated rather than
    // rendered as a screen of disabled controls, the same call the visit screen makes.
    await db.visits.put(visit({ status: "checkedOut", checkedOutAtUtc: "2026-03-17T10:00:00.000Z" }));

    render(<Order visitId="visit-1" />);

    expect(await screen.findByText("This visit is finished")).toBeTruthy();
    expect(screen.queryByRole("list", { name: "What this shop can be sold" })).toBeNull();
  });

  it("says so when the visit is not on this device", async () => {
    render(<Order visitId="visit-nope" />);

    expect(await screen.findByText("That visit is not on this device")).toBeTruthy();
  });
});

describe("<Order> and submitting it", () => {
  /** Adds one line of six cases, so every test below starts from a sendable order. */
  async function withALine() {
    render(<Order visitId="visit-1" />);

    await userEvent.type((await catalogue()).getByLabelText("How many Cola 500ml"), "6");
    await userEvent.click((await catalogue()).getAllByRole("button", { name: "Add" })[0]);

    await waitFor(async () => expect((await draft(db, "visit-1"))?.lines).toHaveLength(1));
  }

  it("seals the order and queues it in one go", async () => {
    /*
     * `ORD-07`, and the two writes are one fact. `submit()` owns the transaction; what this asserts
     * is that the screen actually calls it and that both halves landed — an order marked submitted
     * with no outbox row is work the rep believes was sent and never was.
     */
    await withALine();

    await userEvent.click(await screen.findByRole("button", { name: "Submit the order" }));

    await waitFor(async () => expect(await db.outbox.count()).toBe(1));

    const sent = await orderFor(db, "visit-1");

    expect(sent?.status).toBe("submitted");
    expect(sent?.capturedAtUtc).not.toBeNull();

    const queued = (await db.outbox.toArray())[0];

    expect(queued.type).toBe("CapturedOrder");
    expect(queued.subjectId).toBe(sent?.id);
    expect(queued.status).toBe("pending");
  });

  it("puts the order on the wire as numbers, not as the strings it stores", async () => {
    /*
     * The one conversion `captured()` is allowed to make, checked from the outside because this is
     * the only place it is observable. `CapturedOrderLine` takes bare `decimal` and nothing
     * configures `AllowReadingFromString`, so a quoted `"27.00"` is a **400** — which fails the whole
     * batch and is retried on every reconnect forever rather than recorded and stopped.
     */
    await withALine();

    await userEvent.click(await screen.findByRole("button", { name: "Submit the order" }));

    await waitFor(async () => expect(await db.outbox.count()).toBe(1));

    const payload = (await db.outbox.toArray())[0].payload as Record<string, unknown>;
    const line = (payload.lines as Record<string, unknown>[])[0];

    expect(typeof payload.total).toBe("number");
    expect(payload.total).toBe(27);
    expect(typeof line.unitPrice).toBe("number");
    expect(line.lineTotal).toBe(27);
  });

  it("refuses an empty order and says why, rather than doing nothing", async () => {
    // The store refuses it too — an order for nothing is not an order — but a button that silently
    // declines is a rep tapping it harder. Nothing reaches the outbox.
    render(<Order visitId="visit-1" />);

    await userEvent.type((await catalogue()).getByLabelText("How many Cola 500ml"), "6");
    await userEvent.click((await catalogue()).getAllByRole("button", { name: "Add" })[0]);

    await waitFor(async () => expect((await draft(db, "visit-1"))?.lines).toHaveLength(1));

    await userEvent.click(await screen.findByRole("button", { name: "Remove" }));

    await waitFor(async () => expect((await draft(db, "visit-1"))?.lines).toHaveLength(0));

    await userEvent.click(screen.getByRole("button", { name: "Submit the order" }));

    expect((await screen.findByRole("alert")).textContent).toContain(
      "Add at least one product before submitting.",
    );

    expect(await db.outbox.count()).toBe(0);
  });

  it("stops offering to change a sealed order (BR-ORD-4)", async () => {
    /*
     * The lock, from the screen. `putLine` and `removeLine` both refuse a non-draft, so this is
     * about not *offering* the action — and about the blink the naive version had: reading `draft()`
     * here would leave a sealed order rendering as "nothing on this order yet" with a catalogue
     * under it, telling a rep who just sent an order that they never started one.
     */
    await withALine();

    await userEvent.click(await screen.findByRole("button", { name: "Submit the order" }));

    // Asserted on the screen already mounted, which is the stronger claim: the live query flips it
    // to sealed the moment the transaction commits, with no navigation and no remount involved.
    expect(await screen.findByText("This order is queued.")).toBeTruthy();

    // The lines are still shown — it is a record of what went — but nothing can act on them.
    expect((await lines()).getByText(/6 case/)).toBeTruthy();
    expect(screen.queryByRole("button", { name: "Remove" })).toBeNull();
    expect(screen.queryByRole("button", { name: "Submit the order" })).toBeNull();
    expect(screen.queryByRole("list", { name: "What this shop can be sold" })).toBeNull();
  });
});
