// @vitest-environment jsdom

import "fake-indexeddb/auto";

import { cleanup, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { Audit } from "@/components/field/audit";
import type { SyncContextValue } from "@/components/sync/sync-provider";
import { auditFor } from "@/lib/audits/local-audit";
import {
  closeDatabase,
  FieldKitDatabase,
  type LocalVisit,
  type ReferenceOutlet,
  type ReferenceProduct,
} from "@/lib/sync/db";
import { render } from "@/test/render";

/**
 * The shelf as the rep found it (`AUD-01`, `BR-AUD-1`) — W11 slice 9a.
 *
 * Against a real database and the real store, because every claim worth making here is about the
 * join: that the list is the outlet's **MSL** rather than its assortment, that a tap reaches Dexie
 * rather than React state, and that what is sealed is what `/sync/push` will carry.
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

function product(id: string, name: string): ReferenceProduct {
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
  };
}

const shelf = async () => within(await screen.findByRole("list", { name: "Must-stock products" }));

let db: FieldKitDatabase;

beforeEach(async () => {
  db = new FieldKitDatabase(`audit:${crypto.randomUUID()}`);
  sync.current = { db, pending: 0, failed: 0, running: false, outcome: null, syncNow: vi.fn() };

  await db.outlets.add(SHOP);
  await db.visits.add(visit());

  await db.products.bulkAdd([
    product("p-1", "Cola 500ml"),
    product("p-2", "Water 2L"),
    product("p-3", "Premium Whisky"),
  ]);

  // Two must-stock, one merely sellable — `BR-AUD-1` drives availability from the MSL, and the two
  // sets being different sizes is the whole reason the distinction is worth a test.
  await db.assortment.bulkAdd([
    { id: "a-1", channelId: "channel-1", productId: "p-1", isMustStock: true, rowVersion: 1 },
    { id: "a-2", channelId: "channel-1", productId: "p-2", isMustStock: true, rowVersion: 1 },
    { id: "a-3", channelId: "channel-1", productId: "p-3", isMustStock: false, rowVersion: 1 },
  ]);

  // A channel list in effect from long ago and never ending, so the only reason a product has no
  // expected price is the test that removes it (`BR-AUD-3`, W11 slice 9b).
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

  await db.scoreWeights.add({
    id: "w-3",
    version: 3,
    publishedAtUtc: "2026-01-01T00:00:00.000Z",
    weights: [
      { pillar: "Availability", percentage: "50.00" },
      { pillar: "ShareOfShelf", percentage: "30.00" },
      { pillar: "PriceCompliance", percentage: "20.00" },
    ],
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

describe("<Audit> and the shelf", () => {
  it("asks about the must-stock list, not the whole assortment", async () => {
    /*
     * `BR-AUD-1`. A shop may be allowed to sell a hundred products and be *required* to stock twenty;
     * auditing the wider set would ask a rep to answer for products nobody committed to, and the
     * availability pillar would score a shop against a list it never agreed to.
     */
    render(<Audit visitId="visit-1" />);

    expect(await (await shelf()).findByText("Cola 500ml")).toBeTruthy();
    expect((await shelf()).getByText("Water 2L")).toBeTruthy();
    expect(screen.queryByText("Premium Whisky")).toBeNull();
  });

  it("records an answer in the store, not in React state", async () => {
    // `OFF-01b`: a phone that dies halfway down the aisle must lose nothing, so every tap is a write.
    render(<Audit visitId="visit-1" />);

    await userEvent.click(await screen.findByRole("button", { name: "Cola 500ml is on the shelf" }));

    await waitFor(async () =>
      expect((await auditFor(db, "visit-1"))?.availability).toEqual([
        { productId: "p-1", status: "Present" },
      ]),
    );
  });

  it("starts the audit on the first answer, not on opening the screen", async () => {
    // A rep who opens the step, sees the shelf and is called away leaves nothing behind — no empty
    // audit for the seal to refuse, and nothing for "one per visit" to trip over.
    render(<Audit visitId="visit-1" />);

    await (await shelf()).findByText("Cola 500ml");

    expect(await db.audits.count()).toBe(0);

    await userEvent.click(screen.getByRole("button", { name: "Cola 500ml is on the shelf" }));

    await waitFor(async () => expect(await db.audits.count()).toBe(1));
  });

  it("records the weighting the audit will be scored against", async () => {
    // `BR-AUD-8`, fixed at capture. The version is the one fact that cannot be recovered later.
    render(<Audit visitId="visit-1" />);

    await userEvent.click(await screen.findByRole("button", { name: "Cola 500ml is on the shelf" }));

    await waitFor(async () => expect((await auditFor(db, "visit-1"))?.weightSetVersion).toBe(3));
  });

  it("un-answers a line when the chosen answer is tapped again", async () => {
    /*
     * All three are assertions about the shelf, so a rep who taps the wrong row needs a way back to
     * having said nothing — leaving a wrong answer standing is worse than leaving the line blank.
     */
    render(<Audit visitId="visit-1" />);

    const absent = await screen.findByRole("button", { name: "Cola 500ml is not stocked here" });

    await userEvent.click(absent);
    await waitFor(() => expect(absent.getAttribute("aria-pressed")).toBe("true"));

    await userEvent.click(absent);

    await waitFor(async () => expect((await auditFor(db, "visit-1"))?.availability).toEqual([]));
  });

  it("replaces an answer rather than recording the product twice", async () => {
    // The server refuses a product measured twice in one section (`DuplicateProduct`), so an append
    // would build an audit that cannot be sent.
    render(<Audit visitId="visit-1" />);

    await userEvent.click(
      await screen.findByRole("button", { name: "Cola 500ml is not stocked here" }),
    );
    await userEvent.click(screen.getByRole("button", { name: "Cola 500ml is on the shelf" }));

    await waitFor(async () =>
      expect((await auditFor(db, "visit-1"))?.availability).toEqual([
        { productId: "p-1", status: "Present" },
      ]),
    );
  });

  it("counts what is answered, not what is present", async () => {
    // The progress line is about how much of the shelf the rep has worked. Counting only `Present`
    // would tell a rep who found everything absent that they had not started.
    render(<Audit visitId="visit-1" />);

    await userEvent.click(
      await screen.findByRole("button", { name: "Cola 500ml is not stocked here" }),
    );

    expect(await screen.findByText("1 of 2 checked.")).toBeTruthy();
  });
});

describe("<Audit> and the numbers", () => {
  it("counts facings against the product, and the category total against the shelf", async () => {
    // `BR-AUD-2`'s two halves. The numerator belongs to a product; the denominator is a fact about
    // the shelf, which is why it is one box rather than a column.
    render(<Audit visitId="visit-1" />);

    await userEvent.type(await screen.findByLabelText("Facings of Cola 500ml"), "6");
    await userEvent.type(screen.getByLabelText("Total facings in the category"), "40");

    await waitFor(async () => {
      const held = await auditFor(db, "visit-1");

      expect(held?.facings).toEqual([{ productId: "p-1", facings: 6 }]);
      expect(held?.categoryFacings).toBe(40);
    });
  });

  it("goes back to uncounted rather than zero when the total is cleared", async () => {
    /*
     * The distinction the whole pillar rests on: without a total, share-of-shelf is *skipped* and the
     * score renormalises over what was measured. A zero would say the shop stocks none of the
     * category, which is a claim nobody made.
     *
     * <b>Typed and then cleared, not merely untouched.</b> Asserting the draft's default proves only
     * that the initial value is null — a sabotage pass collapsing `null` into `0` on the *write* left
     * that version of this test green, because it never wrote.
     */
    render(<Audit visitId="visit-1" />);

    const total = await screen.findByLabelText("Total facings in the category");

    await userEvent.type(total, "40");
    await waitFor(async () => expect((await auditFor(db, "visit-1"))?.categoryFacings).toBe(40));

    await userEvent.clear(total);
    await waitFor(async () => expect((await auditFor(db, "visit-1"))?.categoryFacings).toBeNull());
  });

  it("shows what the device expects a product to cost, without pre-filling the box", async () => {
    /*
     * `BR-AUD-3` judges compliance from what the rep *read*. Pre-filling the expected price would
     * make "the rep confirmed it" and "the rep did not look" the same record, on that exact field.
     */
    render(<Audit visitId="visit-1" />);

    expect(await screen.findByText("Expected 4.50 RON")).toBeTruthy();
    expect((screen.getByLabelText("Shelf price of Cola 500ml") as HTMLInputElement).value).toBe("");
  });

  it("stores a shelf price with the expected price it was read against", async () => {
    // Stored beside the observation rather than re-derived at the seal — a list republished in
    // between would otherwise move the number the rep is measured by, after the fact.
    render(<Audit visitId="visit-1" />);

    await userEvent.type(await screen.findByLabelText("Shelf price of Cola 500ml"), "4.79");

    await waitFor(async () =>
      expect((await auditFor(db, "visit-1"))?.prices).toEqual([
        { productId: "p-1", observed: "4.79", expected: "4.50", currencyCode: "RON" },
      ]),
    );
  });

  it("says so when no list prices a product, and still takes the reading", async () => {
    // An unpriced product is not a compliance failure — but the reading is the only evidence that
    // the price list has a gap here, so it is kept.
    // Only Cola's price is removed — both must-stock rows would otherwise say the same thing, and
    // the assertion would pass without proving it is *this* product's row that changed.
    await db.priceLines.delete("pl-1");

    render(<Audit visitId="visit-1" />);

    const row = async (product: string) =>
      within((await screen.findByLabelText(`Shelf price of ${product}`)).closest("li")!);

    /*
     * `findByText` on the priced row **first**, and that ordering is the fix for a flake this test
     * had. The rows render as soon as the MSL loads; the expected prices arrive on a second live
     * query a tick later, so the unpriced row reads "No expected price" before anything has been
     * resolved — asserting it first passed two runs in three for the wrong reason.
     */
    expect(await (await row("Water 2L")).findByText("Expected 6.30 RON")).toBeTruthy();
    expect((await row("Cola 500ml")).getByText("No expected price")).toBeTruthy();

    await userEvent.type(screen.getByLabelText("Shelf price of Cola 500ml"), "4.79");

    await waitFor(async () =>
      expect((await auditFor(db, "visit-1"))?.prices[0]).toMatchObject({
        observed: "4.79",
        expected: null,
      }),
    );
  });

  it("starts the audit from a count, not only from an availability answer", async () => {
    // A rep may work a shelf by counting first and ticking afterwards. Either order has to be the
    // beginning of an audit, or the first thing they do is silently discarded.
    render(<Audit visitId="visit-1" />);

    await screen.findByLabelText("Facings of Cola 500ml");
    expect(await db.audits.count()).toBe(0);

    await userEvent.type(screen.getByLabelText("Facings of Cola 500ml"), "6");

    await waitFor(async () => expect(await db.audits.count()).toBe(1));
  });
});

describe("<Audit> and finishing it", () => {
  it("seals the audit and queues it in one go", async () => {
    render(<Audit visitId="visit-1" />);

    await userEvent.click(await screen.findByRole("button", { name: "Cola 500ml is on the shelf" }));
    await waitFor(async () => expect(await db.audits.count()).toBe(1));

    await userEvent.click(screen.getByRole("button", { name: "Finish the audit" }));

    // Longer than the 1s `waitFor` default: sealing is a two-store transaction and a router
    // replace, and under the whole suite this went red about one run in three. A flaky test is a
    // test people learn to re-run.
    await waitFor(async () => expect(await db.outbox.count()).toBe(1), { timeout: 10_000 });

    const sent = await auditFor(db, "visit-1");

    expect(sent?.status).toBe("sealed");
    expect(sent?.capturedAtUtc).not.toBeNull();
    expect((await db.outbox.toArray())[0].type).toBe("CapturedAudit");
  });

  it("finishes an audit that only measured numbers", async () => {
    /*
     * <b>Regression, found in a browser.</b> 9b widened what counts as measured — facings and prices
     * are pillars in their own right — and the store was widened while the *screen* kept refusing
     * anything without an availability answer. A rep who counted the shelf and read the labels was
     * told to "check at least one product" for an audit the store would have taken.
     *
     * The unit test for the widening called `seal()` directly, so it passed; every screen test ticked
     * availability first, so none of them went near it. Two layers of the same rule, and only one
     * moved.
     */
    render(<Audit visitId="visit-1" />);

    await userEvent.type(await screen.findByLabelText("Facings of Cola 500ml"), "6");
    await waitFor(async () => expect(await db.audits.count()).toBe(1));

    await userEvent.click(screen.getByRole("button", { name: "Finish the audit" }));

    // Longer than the 1s `waitFor` default: sealing is a two-store transaction and a router
    // replace, and under the whole suite this went red about one run in three. A flaky test is a
    // test people learn to re-run.
    await waitFor(async () => expect(await db.outbox.count()).toBe(1), { timeout: 10_000 });
    expect(screen.queryByRole("alert")).toBeNull();
  });

  it("stops offering to change a sealed audit (BR-AUD-6)", async () => {
    /*
     * The lock, from the screen. The store refuses a non-draft too, so this is about not *offering*
     * the action — and about the blink the naive version would have: reading `draft()` here would
     * leave a sealed audit rendering as one nobody had started, with a fresh shelf under it.
     */
    render(<Audit visitId="visit-1" />);

    await userEvent.click(await screen.findByRole("button", { name: "Cola 500ml is on the shelf" }));
    await waitFor(async () => expect(await db.audits.count()).toBe(1));

    await userEvent.click(screen.getByRole("button", { name: "Finish the audit" }));

    expect(await screen.findByText("This audit is queued.")).toBeTruthy();

    // The answers are still shown — it is a record of what went — but nothing can act on them.
    expect(
      (screen.getByRole("button", { name: "Cola 500ml is on the shelf" }) as HTMLButtonElement)
        .disabled,
    ).toBe(true);
    expect(screen.queryByRole("button", { name: "Finish the audit" })).toBeNull();
  });

  it("says nothing can be audited when the shop has no must-stock list", async () => {
    // A real state: a channel whose assortment carries no MSL flag. Better said than rendered as an
    // empty list with a Finish button the store would refuse.
    await db.assortment.clear();

    render(<Audit visitId="visit-1" />);

    expect(
      await screen.findByText(
        "This shop has no must-stock products, so there is nothing to check.",
      ),
    ).toBeTruthy();
  });

  it("refuses to start when the tenant has published no weighting", async () => {
    /*
     * `BR-AUD-8` records the version at capture and the server refuses an audit naming one it cannot
     * find (`UnknownWeightSet`). Without this, a rep would walk the whole shelf, seal, push, and have
     * the audit marked `failed` with nothing to retry — so it is refused before the first tap.
     */
    await db.scoreWeights.clear();

    render(<Audit visitId="visit-1" />);

    expect(await screen.findByText("No scoring set up yet")).toBeTruthy();
    expect(screen.queryByRole("list", { name: "Must-stock products" })).toBeNull();
  });

  it("says so on a visit that is already finished", async () => {
    await db.visits.put(visit({ status: "checkedOut" }));

    render(<Audit visitId="visit-1" />);

    expect(await screen.findByText("This visit is finished")).toBeTruthy();
  });
});
