// @vitest-environment jsdom

import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";

import type { AuthContextValue } from "@/components/auth-provider";
import { OrderQueue } from "@/components/back-office/order-queue";
import { ApiError } from "@/lib/api/client";
import { fetchIdentity } from "@/lib/api/identity";
import type { Order } from "@/lib/api/orders";
import { render } from "@/test/render";

const auth = vi.hoisted(() => ({ current: {} as AuthContextValue }));
const fetchOrders = vi.hoisted(() => vi.fn());
const fetchOutlets = vi.hoisted(() => vi.fn());

vi.mock("@/components/auth-provider", () => ({ useAuth: () => auth.current }));

vi.mock("@/lib/api/orders", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/lib/api/orders")>()),
  fetchOrders: (...args: unknown[]) => fetchOrders(...args),
}));

vi.mock("@/lib/api/outlets", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/lib/api/outlets")>()),
  fetchOutlets: (...args: unknown[]) => fetchOutlets(...args),
}));

const WAITING: Order = {
  id: "order-1",
  visitId: "visit-1",
  outletId: "outlet-1",
  userId: "subject-maria",
  status: "Submitted",
  currencyCode: "RON",
  total: 271.5,
  taxTotal: 51.59,
  serverTotal: null,
  serverTaxTotal: null,
  agreement: "NotRepriced",
  capturedAtUtc: "2026-10-05T09:30:00Z",
  lines: [
    { productId: "p1", quantity: 6, unitOfMeasure: "case", packSize: 12, unitPrice: 4.5, lineTotal: 27 },
    { productId: "p2", quantity: 10, unitOfMeasure: "case", packSize: 12, unitPrice: 24.45, lineTotal: 244.5 },
  ],
  rejection: null,
};

const OUTLETS = {
  items: [
    { id: "outlet-1", code: "OUT-1", name: "Corner Shop", channelId: "c1", channelName: "TT", segment: null, banner: null, status: "Active", territory: null },
  ],
  total: 1,
};

describe("<OrderQueue>", () => {
  beforeEach(() => {
    vi.clearAllMocks();

    auth.current = {
      status: "authenticated",
      user: { access_token: "token", profile: { sub: "subject-a" } },
    } as unknown as AuthContextValue;

    fetchOrders.mockResolvedValue([WAITING]);
    fetchOutlets.mockResolvedValue(OUTLETS);

    vi.mocked(fetchIdentity).mockResolvedValue({
      subject: "subject-a",
      tenant: "fieldkit-dev",
      permissions: ["visit:read"],
    });
  });

  it("opens on what is waiting for a decision", async () => {
    // The queue's whole job. Opening on everything would make a supervisor filter before they can
    // start, and opening on nothing would hide the work.
    render(<OrderQueue />);

    await screen.findByRole("list");

    expect(fetchOrders).toHaveBeenCalledWith("token", "Submitted", expect.anything());
  });

  it("shows the shop, the lines and the total the shop agreed to", async () => {
    render(<OrderQueue />);

    const row = within(await screen.findByRole("list")).getByText(/Corner Shop/).closest("li")!;

    expect(within(row).getByText("2 lines")).toBeTruthy();
    expect(within(row).getByText("271.50 RON")).toBeTruthy();
    expect(within(row).getByText("Waiting")).toBeTruthy();
  });

  it("flags a server that priced it differently without replacing the number", async () => {
    /*
     * `BR-ORD-2`: the server re-prices and *flags*, never applies. The total shown stays the
     * device's — what the rep and the shopkeeper settled — and the disagreement is a note beside it.
     *
     * Both are asserted, because either alone would be wrong: hiding the flag leaves a supervisor
     * comparing a total against a price list that no longer produces it, and showing the server's
     * figure as *the* total reports a number nobody at the counter agreed to.
     */
    fetchOrders.mockResolvedValue([
      { ...WAITING, agreement: "Differs", serverTotal: 265.0, serverTaxTotal: 50.35 },
    ]);

    render(<OrderQueue />);

    const row = within(await screen.findByRole("list")).getByText(/Corner Shop/).closest("li")!;

    expect(within(row).getByText("271.50 RON")).toBeTruthy();
    expect(within(row).getByText(/The server priced this at 265.00 RON/)).toBeTruthy();
  });

  it("says nothing about pricing when the two agree", async () => {
    // The flag has to mean something when it appears, so an agreeing order carries no note — and an
    // order the server could not price at all is not a disagreement either.
    fetchOrders.mockResolvedValue([
      { ...WAITING, agreement: "Agrees", serverTotal: 271.5, serverTaxTotal: 51.59 },
    ]);

    render(<OrderQueue />);

    await screen.findByRole("list");

    expect(screen.queryByText(/The server priced this at/)).toBeNull();
  });

  it("shows why a rejected order was refused", async () => {
    // An order that was refused and does not say why is the thing this screen exists to prevent —
    // the rep gets it back on their device (`BR-ORD-9`) and has to know what to fix.
    fetchOrders.mockResolvedValue([
      {
        ...WAITING,
        status: "Rejected",
        rejection: { reason: "BelowMinimum", offendingProductId: null, note: "Needs 20 cases." },
      },
    ]);

    render(<OrderQueue />);

    expect(await screen.findByText(/below the order minimum/)).toBeTruthy();
    expect(screen.getByText(/Needs 20 cases\./)).toBeTruthy();
  });

  it("says so plainly when a rejection carries no note", async () => {
    // The note is optional — `OutletClosed` often needs no explanation — and an empty sentence would
    // read as a missing field rather than as a deliberate silence.
    fetchOrders.mockResolvedValue([
      {
        ...WAITING,
        status: "Rejected",
        rejection: { reason: "OutletClosed", offendingProductId: null, note: null },
      },
    ]);

    render(<OrderQueue />);

    expect(await screen.findByText(/No note\./)).toBeTruthy();
  });

  it("asks again when a reader changes the filter", async () => {
    render(<OrderQueue />);

    await screen.findByRole("list");

    await userEvent.selectOptions(screen.getByLabelText("Show"), "Rejected");

    await waitFor(() =>
      expect(fetchOrders).toHaveBeenCalledWith("token", "Rejected", expect.anything()));
  });

  it("distinguishes an empty queue from an empty filter", async () => {
    // "Nothing is waiting" is good news; "no orders here" is a filter that found nothing. Same
    // absence, different sentences, because only the first is something to be pleased about.
    fetchOrders.mockResolvedValue([]);

    render(<OrderQueue />);

    expect(await screen.findByText(/Nothing is waiting on a decision/)).toBeTruthy();

    await userEvent.selectOptions(screen.getByLabelText("Show"), "Rejected");

    expect(await screen.findByText(/No orders here/)).toBeTruthy();
  });

  it("says which refusal it met", async () => {
    fetchOrders.mockRejectedValue(new ApiError(403, "Forbidden"));

    render(<OrderQueue />);

    expect((await screen.findByRole("alert")).textContent).toMatch(/do not have permission/);
  });
});
