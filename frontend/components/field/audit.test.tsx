// @vitest-environment jsdom

import "fake-indexeddb/auto";

import { cleanup, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { Audit } from "@/components/field/audit";
import type { SyncContextValue } from "@/components/sync/sync-provider";
import { auditFor, draftFor, putAvailability } from "@/lib/audits/local-audit";
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

/*
 * The camera stops at the canvas, so the canvas is where the fake goes.
 *
 * jsdom implements no 2D context, so the real `downscale` throws before it resizes anything — mocking
 * it is the only way to reach the code *after* the shutter. `B5`'s arithmetic has its own tests in
 * `fitWithin`, and the encode is verified in a browser.
 *
 * At the top level because that is where it runs: `vi.mock` is hoisted above every test whatever it
 * is nested inside, and vitest warns about the pretence.
 */
vi.mock("@/lib/photos/downscale", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/lib/photos/downscale")>()),
  downscale: async (file: Blob) => file,
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

/*
 * `find`, never `get`, for anything that appears only once the audit exists.
 *
 * `waitFor(db.audits.count() === 1)` says the *store* has caught up, which is one beat ahead of the
 * screen: the live query has still to emit and React to render, and the Seal section is mounted on
 * `held`. Grabbing the button synchronously after that wait went red about one run in five under the
 * whole suite — a flake nobody would have read as a real signal.
 */

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

describe("<Audit> and the questionnaire", () => {
  const FORM = {
    id: "form-1",
    name: "Cold chain",
    rowVersion: 1,
    questions: [
      {
        order: 1,
        key: "chiller",
        text: "Is the chiller working?",
        type: "Boolean",
        mandatory: true,
        options: [],
      },
      {
        order: 2,
        key: "placement",
        text: "Where is the display?",
        type: "SingleChoice",
        mandatory: false,
        options: ["Aisle end", "Checkout"],
      },
      {
        order: 3,
        key: "notes",
        text: "Anything else?",
        type: "Text",
        mandatory: false,
        options: [],
      },
    ],
  };

  it("shows nothing at all when the tenant has no questionnaire", async () => {
    // The ordinary case: most audits are a shelf and no form. A picker offering nothing would be a
    // control that only ever says "None".
    render(<Audit visitId="visit-1" />);

    await (await shelf()).findByText("Cola 500ml");

    expect(screen.queryByText("Questionnaire")).toBeNull();
  });

  it("uses the only questionnaire without asking", async () => {
    /*
     * Nothing in the model says which form applies here — a workflow step carries a type and a label
     * and no form id. With one there is no choice to make, and a dropdown at every shop offering a
     * single option is a tap that teaches a rep nothing.
     */
    await db.surveys.add(FORM);

    render(<Audit visitId="visit-1" />);

    expect(await screen.findByText("Is the chiller working?")).toBeTruthy();
    expect(screen.queryByLabelText("Which questionnaire")).toBeNull();
  });

  it("asks which questionnaire when the tenant has several", async () => {
    await db.surveys.bulkAdd([FORM, { ...FORM, id: "form-2", name: "Promotions", questions: [] }]);

    render(<Audit visitId="visit-1" />);

    expect(await screen.findByLabelText("Which questionnaire")).toBeTruthy();
    // Nothing is chosen for the rep, so no questions until they pick.
    expect(screen.queryByText("Is the chiller working?")).toBeNull();
  });

  it("stores an answer with the question's text, not just its key", async () => {
    /*
     * The form may be reworded — or the question dropped — between the rep answering and the push
     * landing. An answer that needs the form re-read to mean anything is one a reader cannot trust,
     * and the server makes the same call.
     */
    await db.surveys.add(FORM);

    render(<Audit visitId="visit-1" />);

    await userEvent.click(await screen.findByRole("button", { name: "Yes to Is the chiller working?" }));

    await waitFor(async () =>
      expect((await auditFor(db, "visit-1"))?.answers).toEqual([
        { questionKey: "chiller", questionText: "Is the chiller working?", value: "true" },
      ]),
    );

    expect((await auditFor(db, "visit-1"))?.surveyFormId).toBe("form-1");
  });

  it("un-answers a yes/no question when the chosen answer is tapped again", async () => {
    // The same toggle the shelf's three answers have — and here it also matters for `BR-AUD-7`,
    // which counts a mandatory question with no answer.
    await db.surveys.add(FORM);

    render(<Audit visitId="visit-1" />);

    const yes = await screen.findByRole("button", { name: "Yes to Is the chiller working?" });

    await userEvent.click(yes);
    await waitFor(async () => expect((await auditFor(db, "visit-1"))?.answers).toHaveLength(1));

    await userEvent.click(yes);
    await waitFor(async () => expect((await auditFor(db, "visit-1"))?.answers).toEqual([]));
  });

  it("refuses to finish while a mandatory question is unanswered (BR-AUD-7)", async () => {
    /*
     * The rule this slice exists for, enforced here and deliberately nowhere else: the server would
     * test the answers against the questionnaire as it reads *today*, so a form that gained a
     * mandatory question after the rep answered would refuse an audit for a question that did not
     * exist when they worked the shelf.
     */
    await db.surveys.add(FORM);

    render(<Audit visitId="visit-1" />);

    await userEvent.click(await screen.findByRole("button", { name: "Cola 500ml is on the shelf" }));
    await waitFor(async () => expect(await db.audits.count()).toBe(1));

    // Named before the rep taps, so they know *which* question rather than that there is one.
    const outstanding = within(
      await screen.findByRole("list", { name: "These still need an answer before you can finish:" }),
    );

    expect(outstanding.getByText("Is the chiller working?")).toBeTruthy();
    // The optional ones are not chased — `BR-AUD-7` is about mandatory questions.
    expect(outstanding.queryByText("Where is the display?")).toBeNull();

    await userEvent.click(await screen.findByRole("button", { name: "Finish the audit" }));

    expect((await screen.findByRole("alert")).textContent).toContain(
      "Answer the questions listed above before finishing.",
    );
    expect(await db.outbox.count()).toBe(0);

    await userEvent.click(screen.getByRole("button", { name: "Yes to Is the chiller working?" }));

    // Waited for, because the answer goes to Dexie and comes back through the live query — the same
    // beat a rep watches the list clear over. Tapping Finish inside that beat is refused again, which
    // is honest: the gate reads the audit as the screen currently holds it.
    await waitFor(() =>
      expect(
        screen.queryByRole("list", {
          name: "These still need an answer before you can finish:",
        }),
      ).toBeNull(),
    );

    await userEvent.click(await screen.findByRole("button", { name: "Finish the audit" }));

    await waitFor(async () => expect(await db.outbox.count()).toBe(1), { timeout: 10_000 });

    // `it`'s own timeout as well as `waitFor`'s: vitest gives a test 5s, so a bare `waitFor(…, 10s)`
    // is headroom the runner never lets it reach — the test dies at 5s with "Test timed out" instead
    // of the assertion. Which is how this one first went red, under the full suite.
  }, 15_000);

  it("takes the refusal back down once the question is answered", async () => {
    /*
     * <b>Found in a browser.</b> The refusal reads "Answer the questions listed above before
     * finishing" and the list above it vanishes the moment the rep answers — leaving a complaint
     * pointing at nothing, about a rule the rep has just satisfied.
     */
    await db.surveys.add(FORM);

    render(<Audit visitId="visit-1" />);

    await userEvent.click(await screen.findByRole("button", { name: "Cola 500ml is on the shelf" }));
    await waitFor(async () => expect(await db.audits.count()).toBe(1));

    await userEvent.click(await screen.findByRole("button", { name: "Finish the audit" }));
    expect(await screen.findByRole("alert")).toBeTruthy();

    await userEvent.click(screen.getByRole("button", { name: "Yes to Is the chiller working?" }));

    await waitFor(() => expect(screen.queryByRole("alert")).toBeNull());
  });

  it("does not gate on an optional question", async () => {
    // `BR-AUD-7` is about mandatory questions only. Gating on every question would make an optional
    // one a contradiction in terms.
    await db.surveys.add({ ...FORM, questions: [FORM.questions[2]] });

    render(<Audit visitId="visit-1" />);

    await userEvent.click(await screen.findByRole("button", { name: "Cola 500ml is on the shelf" }));
    await waitFor(async () => expect(await db.audits.count()).toBe(1));

    await userEvent.click(await screen.findByRole("button", { name: "Finish the audit" }));

    await waitFor(async () => expect(await db.outbox.count()).toBe(1), { timeout: 10_000 });
  }, 15_000);
});

describe("<Audit> and finishing it", () => {
  it("seals the audit and queues it in one go", async () => {
    render(<Audit visitId="visit-1" />);

    await userEvent.click(await screen.findByRole("button", { name: "Cola 500ml is on the shelf" }));
    await waitFor(async () => expect(await db.audits.count()).toBe(1));

    await userEvent.click(await screen.findByRole("button", { name: "Finish the audit" }));

    // Longer than the 1s `waitFor` default: sealing is a two-store transaction and a router
    // replace, and under the whole suite this went red about one run in three. A flaky test is a
    // test people learn to re-run.
    await waitFor(async () => expect(await db.outbox.count()).toBe(1), { timeout: 10_000 });

    const sent = await auditFor(db, "visit-1");

    expect(sent?.status).toBe("sealed");
    expect(sent?.capturedAtUtc).not.toBeNull();
    expect((await db.outbox.toArray())[0].type).toBe("CapturedAudit");
  }, 15_000);

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

    await userEvent.click(await screen.findByRole("button", { name: "Finish the audit" }));

    // Longer than the 1s `waitFor` default: sealing is a two-store transaction and a router
    // replace, and under the whole suite this went red about one run in three. A flaky test is a
    // test people learn to re-run.
    await waitFor(async () => expect(await db.outbox.count()).toBe(1), { timeout: 10_000 });
    expect(screen.queryByRole("alert")).toBeNull();
  }, 15_000);

  it("stops offering to change a sealed audit (BR-AUD-6)", async () => {
    /*
     * The lock, from the screen. The store refuses a non-draft too, so this is about not *offering*
     * the action — and about the blink the naive version would have: reading `draft()` here would
     * leave a sealed audit rendering as one nobody had started, with a fresh shelf under it.
     */
    render(<Audit visitId="visit-1" />);

    await userEvent.click(await screen.findByRole("button", { name: "Cola 500ml is on the shelf" }));
    await waitFor(async () => expect(await db.audits.count()).toBe(1));

    await userEvent.click(await screen.findByRole("button", { name: "Finish the audit" }));

    // Ten seconds, like every other seal-path wait in this file: sealing is a two-store transaction
    // and a router replace, and under the whole suite the 1s default went red about one run in three.
    expect(await screen.findByText("This audit is queued.", undefined, { timeout: 10_000 })).toBeTruthy();

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

describe("<Audit> and sealing what is actually stored", () => {
  it("seals on what the store holds now, not on the snapshot it last rendered", async () => {
    /*
     * <b>Regression. It surfaced as a suite flake, and it was a real race.</b>
     *
     * The first tap on a shelf is *two* writes — `draftFor`, then `putAvailability` — so between them
     * the screen legitimately holds an audit that has measured nothing, with the Finish button already
     * rendered on it. Tapping through that gap was refused with "check at least one product" for a
     * product the rep had just checked, and they had to tap again for a seal the store would have taken.
     *
     * Staged here the way the race stages itself: an empty draft the screen has rendered, then an
     * answer written straight to the store, then the tap. What the seal must read is the store.
     */
    const started = await draftFor(db, {
      visitId: "visit-1",
      outletId: "outlet-1",
      weightSetVersion: 3,
      now: new Date("2026-03-17T09:30:00.000Z"),
    });

    render(<Audit visitId="visit-1" />);

    const finish = await screen.findByRole("button", { name: "Finish the audit" });

    await putAvailability(db, started.id, "p-1", "Present", new Date("2026-03-17T09:31:00.000Z"));

    /*
     * A **native** click, not `userEvent.click`: the write is committed but the live query's re-emit
     * is still a microtask away, and `userEvent` would flush it before dispatching. Clicking the DOM
     * directly lands the tap in the gap — which is the whole race, staged rather than waited for.
     */
    finish.click();

    await waitFor(async () => expect(await db.outbox.count()).toBe(1), { timeout: 10_000 });
    expect(screen.queryByText("Check at least one product before finishing.")).toBeNull();
  }, 15_000);
});

describe("<Audit> and the score", () => {
  it("scores what the rep has measured so far, pillar by pillar (AUD-06)", async () => {
    /*
     * Two answered, one found: availability is 50%. The other two pillars are skipped — no facings
     * counted, no shelf price read — so the total renormalises over availability alone rather than
     * dragging in two zeroes (`BR-AUD-2`, W10 slice 0).
     *
     * <b>The denominator is the lines the rep answered, not the whole MSL.</b> Answering one product
     * and finding it reads 100%, which overstates a shelf nobody finished walking — and it is
     * deliberate here because it is what the server does: `Audit.Ingest` scores the entries it stored,
     * and `BR-AUD-5` has the two numbers agree exactly. A device that divided by the MSL instead
     * would be the more useful screen and the wrong one.
     */
    render(<Audit visitId="visit-1" />);

    await userEvent.click(await screen.findByRole("button", { name: "Cola 500ml is on the shelf" }));
    await userEvent.click(await screen.findByRole("button", { name: "Water 2L is not stocked here" }));

    const pillars = within(await screen.findByRole("list", { name: "What the score is made of" }));

    await waitFor(() => expect(pillars.getByText("50.00% · worth 50%")).toBeTruthy());
    expect(screen.getByLabelText("Perfect store").textContent).toBe("50.00%");

    // Said, not implied: a rep seeing 50 cannot otherwise tell an unmeasured pillar from a bad one.
    expect(pillars.getAllByText("Not measured")).toHaveLength(2);
  });

  it("says nothing is scored yet rather than showing a zero", async () => {
    /*
     * An audit whose every pillar was skipped has no weighted mean to take. Printing 0% would tell a
     * rep they had failed a shop they have not finished measuring — and 9c made this reachable, since
     * an audit can now exist on a questionnaire answer alone.
     */
    await db.surveys.add({
      id: "form-1",
      name: "Cold chain",
      rowVersion: 1,
      questions: [
        { order: 1, key: "chiller", text: "Chiller working?", type: "Boolean", mandatory: false, options: [] },
      ],
    });

    render(<Audit visitId="visit-1" />);

    await userEvent.click(await screen.findByRole("button", { name: "Yes to Chiller working?" }));

    expect((await screen.findByLabelText("Perfect store")).textContent).toBe("Not scored yet");
  });

  it("scores against the weighting the audit names, not the newest (BR-AUD-8)", async () => {
    /*
     * <b>The rule this section is most likely to get wrong.</b> `BR-AUD-8` fixes the version when the
     * draft starts, and the server scores the audit against *that* version. A screen reading "the
     * newest" would restate the rep's morning after an overnight re-weighting synced — and would
     * disagree with the number the back office stores.
     *
     * Version 3 weights availability at 50; version 4 weights it at 100. The rep started before the
     * re-weighting synced, so the draft names 3 while the device now holds both — the state a rep is
     * in whenever a round outlives a publish.
     *
     * <b>Both versions are in place before the render, deliberately.</b> The first shape of this test
     * added version 4 *after* asserting, and it caught nothing: swapping the lookup for
     * `currentScoreWeightSet` still passed, because the assertion ran before the re-emit. A test whose
     * verdict depends on which of two async things happens first is not a test.
     */
    await db.scoreWeights.add({
      id: "w-4",
      version: 4,
      publishedAtUtc: "2026-06-01T00:00:00.000Z",
      weights: [
        { pillar: "Availability", percentage: "100.00" },
        { pillar: "ShareOfShelf", percentage: "0.00" },
        { pillar: "PriceCompliance", percentage: "0.00" },
      ],
      rowVersion: 1,
    });

    // The draft the rep started this morning, naming the weighting that was current then.
    const started = await draftFor(db, {
      visitId: "visit-1",
      outletId: "outlet-1",
      weightSetVersion: 3,
      now: new Date("2026-03-17T09:30:00.000Z"),
    });
    await putAvailability(db, started.id, "p-1", "Present", new Date("2026-03-17T09:31:00.000Z"));
    await putAvailability(db, started.id, "p-2", "Absent", new Date("2026-03-17T09:32:00.000Z"));

    render(<Audit visitId="visit-1" />);

    const pillars = within(await screen.findByRole("list", { name: "What the score is made of" }));

    // Worth 50, not 100: the audit names version 3, and version 3 is what it is scored against.
    await waitFor(() => expect(pillars.getByText("50.00% · worth 50%")).toBeTruthy());
    expect(pillars.queryByText("50.00% · worth 100%")).toBeNull();
  });

  it("refuses to guess when the weighting names a pillar it cannot compute", async () => {
    /*
     * A weight set published by a newer server, holding a pillar this build has never heard of.
     * Dropping it would change the denominator the weighted mean is taken over, so the device would
     * show a confident number the back office then contradicts — which is exactly what `BR-AUD-5`
     * exists to prevent. Saying so is the honest answer.
     */
    await db.scoreWeights.add({
      id: "w-9",
      version: 9,
      publishedAtUtc: "2026-07-01T00:00:00.000Z",
      weights: [
        { pillar: "Availability", percentage: "50.00" },
        { pillar: "PlanogramCompliance", percentage: "50.00" },
      ],
      rowVersion: 1,
    });

    render(<Audit visitId="visit-1" />);

    await userEvent.click(await screen.findByRole("button", { name: "Cola 500ml is on the shelf" }));

    expect(
      (await screen.findByText(/cannot be shown here/)).textContent,
    ).toContain("It will still be scored when it reaches the back office.");

    expect(screen.queryByRole("list", { name: "What the score is made of" })).toBeNull();
  });

  it("still shows the score after the audit is sealed", async () => {
    // What the rep sent, and what the server will store against it. A breakdown that vanishes at the
    // seal leaves them nothing to have been told.
    render(<Audit visitId="visit-1" />);

    await userEvent.click(await screen.findByRole("button", { name: "Cola 500ml is on the shelf" }));
    await userEvent.click(await screen.findByRole("button", { name: "Finish the audit" }));

    await waitFor(async () => expect(await db.outbox.count()).toBe(1), { timeout: 10_000 });

    // One line answered and found: 100% of what was measured, which is the number the server will
    // store against this audit.
    expect((await screen.findByLabelText("Perfect store")).textContent).toBe("100.00%");
  }, 15_000);
});

describe("<Audit> and photographs", () => {
  /*
   * The camera is faked at the file input, not at the canvas: `downscale` is a no-op in jsdom, which
   * has no 2D context, so it is stubbed for these tests and covered by `fitWithin` and a browser.
   * What is under test here is everything after the shutter — the pair of rows, the section the rep
   * chose, and what the seal carries.
   */
  const photograph = () =>
    new File([new Uint8Array(4096)], "shelf.jpg", { type: "image/jpeg" });

  it("stores the image and points the audit at it", async () => {
    render(<Audit visitId="visit-1" />);

    await userEvent.upload(await screen.findByLabelText("Take a photo"), photograph());

    await waitFor(async () => expect(await db.blobs.count()).toBe(1));

    const stored = (await db.blobs.toArray())[0];
    const audit = (await auditFor(db, "visit-1"))!;

    // The pair, which is the whole point: a reference with no blob can never be uploaded, and a blob
    // with no reference is a picture nothing will ask for.
    expect(audit.photos).toEqual([{ section: "General", objectKey: stored.objectKey }]);
    expect(stored.auditId).toBe(audit.id);
  });

  it("starts the audit on the first photograph, as the first shelf answer does", async () => {
    // A shop that will not let a rep count the shelf still lets them photograph the display, and
    // `AUD-05` calls that a real audit. It must not need an availability tap first.
    render(<Audit visitId="visit-1" />);

    await (await shelf()).findByText("Cola 500ml");
    expect(await db.audits.count()).toBe(0);

    await userEvent.upload(screen.getByLabelText("Take a photo"), photograph());

    await waitFor(async () => expect(await db.audits.count()).toBe(1));
  });

  it("files the photograph under the section the rep chose", async () => {
    // `AuditSection` is what a supervisor filters by, and only the person holding the camera knows
    // whether this is the chiller, the gondola end or a shelf edge.
    render(<Audit visitId="visit-1" />);

    await userEvent.selectOptions(
      await screen.findByLabelText("This photo shows"),
      "PriceCompliance",
    );
    await userEvent.upload(screen.getByLabelText("Take a photo"), photograph());

    await waitFor(async () =>
      expect((await auditFor(db, "visit-1"))?.photos[0]?.section).toBe("PriceCompliance"),
    );
  });

  it("removes the image with the reference", async () => {
    // Leaving the blob would upload bytes no audit mentions and no supervisor can reach.
    render(<Audit visitId="visit-1" />);

    await userEvent.upload(await screen.findByLabelText("Take a photo"), photograph());
    await waitFor(async () => expect(await db.blobs.count()).toBe(1));

    await userEvent.click(await screen.findByRole("button", { name: "Remove" }));

    await waitFor(async () => expect(await db.blobs.count()).toBe(0));
    expect((await auditFor(db, "visit-1"))?.photos).toEqual([]);
  });

  it("finishes an audit that is only a photograph (AUD-05)", async () => {
    /*
     * The case the section exists for, and the one a device could easily refuse: `Audit.Check` counts
     * photographs as work, so a screen that demanded a number first would refuse an audit the server
     * would have taken.
     */
    render(<Audit visitId="visit-1" />);

    await userEvent.upload(await screen.findByLabelText("Take a photo"), photograph());
    await waitFor(async () => expect(await db.audits.count()).toBe(1));

    await userEvent.click(await screen.findByRole("button", { name: "Finish the audit" }));

    await waitFor(async () => expect(await db.outbox.count()).toBe(1), { timeout: 10_000 });

    const payload = (await db.outbox.toArray())[0].payload as {
      photos: { section: string; objectKey: string }[];
    };

    expect(payload.photos).toHaveLength(1);
    expect(payload.photos[0].section).toBe("General");
  }, 15_000);

  it("stops offering the camera once the audit is sealed, while still showing what was taken", async () => {
    // `BR-AUD-6`. The pictures are a record of what was sent — and the blobs are still on the device,
    // because nothing has uploaded them yet.
    render(<Audit visitId="visit-1" />);

    await userEvent.upload(await screen.findByLabelText("Take a photo"), photograph());
    await waitFor(async () => expect(await db.audits.count()).toBe(1));

    await userEvent.click(await screen.findByRole("button", { name: "Finish the audit" }));
    await waitFor(async () => expect(await db.outbox.count()).toBe(1), { timeout: 10_000 });

    expect(screen.queryByLabelText("Take a photo")).toBeNull();
    expect(screen.queryByRole("button", { name: "Remove" })).toBeNull();
    expect(await db.blobs.count()).toBe(1);
  }, 15_000);
});
