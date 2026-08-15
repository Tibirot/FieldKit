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
const rejectOrder = vi.hoisted(() => vi.fn());

vi.mock("@/components/auth-provider", () => ({ useAuth: () => auth.current }));

vi.mock("@/lib/api/orders", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/lib/api/orders")>()),
  fetchOrders: (...args: unknown[]) => fetchOrders(...args),
  rejectOrder: (...args: unknown[]) => rejectOrder(...args),
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

function allow(...permissions: string[]) {
  vi.mocked(fetchIdentity).mockResolvedValue({
    subject: "subject-a",
    tenant: "fieldkit-dev",
    permissions,
  });
}

/** Opens the form on the only order in the queue. */
async function openTheForm() {
  await userEvent.click(await screen.findByRole("button", { name: "Reject" }));
}

describe("rejecting an order", () => {
  beforeEach(() => {
    vi.clearAllMocks();

    auth.current = {
      status: "authenticated",
      user: { access_token: "token", profile: { sub: "subject-a" } },
    } as unknown as AuthContextValue;

    fetchOrders.mockResolvedValue([WAITING]);
    fetchOutlets.mockResolvedValue(OUTLETS);
    rejectOrder.mockResolvedValue({ ...WAITING, status: "Rejected" });
    allow("visit:read", "order:reject");
  });

  it("sends the reason, and the line only when one was named", async () => {
    /*
     * The note and the line are both optional and are sent as *absent* rather than as empty strings:
     * the server would take `""` as a note and store a blank one, which reads to a rep as a
     * supervisor who could not be bothered rather than as one who had nothing to add.
     */
    render(<OrderQueue />);

    await openTheForm();

    await userEvent.selectOptions(screen.getByLabelText("Reason"), "OutletClosed");
    await userEvent.click(screen.getByRole("button", { name: "Reject the order" }));

    await waitFor(() =>
      expect(rejectOrder).toHaveBeenCalledWith("token", "order-1", {
        reason: "OutletClosed",
        offendingProductId: undefined,
        note: undefined,
      }));
  });

  it("points at a line without editing it", async () => {
    // `BR-ORD-4` denies everybody the right to change what a rep captured, so the line is a pointer
    // and the control is a picker over the order's own lines — never a quantity field.
    render(<OrderQueue />);

    await openTheForm();

    await userEvent.selectOptions(screen.getByLabelText("Reason"), "OffAssortment");
    await userEvent.selectOptions(screen.getByLabelText("Line"), "p2");
    await userEvent.type(screen.getByLabelText("Note for the rep"), "Delisted last week.");
    await userEvent.click(screen.getByRole("button", { name: "Reject the order" }));

    await waitFor(() =>
      expect(rejectOrder).toHaveBeenCalledWith("token", "order-1", {
        reason: "OffAssortment",
        offendingProductId: "p2",
        note: "Delisted last week.",
      }));
  });

  it("offers only the lines that are on the order", async () => {
    // The server refuses anything else with `order.rejection.unknownLine`, so a control that could
    // produce that refusal would be a control that wastes a trip.
    render(<OrderQueue />);

    await openTheForm();

    const options = within(screen.getByLabelText("Line")).getAllByRole("option");

    expect(options.map((option) => (option as HTMLOptionElement).value)).toEqual(["", "p1", "p2"]);
  });

  it("refreshes the lists once the order has moved", async () => {
    // Rejecting moves an order between two queries — the queue it leaves and the rejected list it
    // joins. Invalidating only the one on screen would leave the other stale until a reload.
    render(<OrderQueue />);

    await openTheForm();
    await userEvent.click(screen.getByRole("button", { name: "Reject the order" }));

    await waitFor(() => expect(rejectOrder).toHaveBeenCalled());

    // A second fetch after the mutation: the first was the initial load.
    await waitFor(() => expect(fetchOrders.mock.calls.length).toBeGreaterThan(1));
  });

  it("says plainly when somebody else decided first", async () => {
    /*
     * The 409 the server raises when the order is no longer `Submitted`. It is not a mistake the
     * reader made and retrying will never fix it, so "try again" would be the wrong instruction —
     * what needs refreshing is the list.
     */
    rejectOrder.mockRejectedValue(new ApiError(409, [{ field: null, message: "Only a submitted order can be rejected; this one is Rejected." }]));

    render(<OrderQueue />);

    await openTheForm();
    await userEvent.click(screen.getByRole("button", { name: "Reject the order" }));

    expect((await screen.findByRole("alert")).textContent).toMatch(/Somebody already decided/);
  });

  it("shows what the server refused, in the reader's language", async () => {
    /*
     * Anything that is not the 409 comes back through the refusal catalogue, so a note that is too
     * long is explained rather than reported as a generic failure.
     *
     * **This test failed first, and on the right thing.** The catalogue entry I wrote said "A note
     * is at most {max} characters" — but `/api/orders/{id}/rejection` interpolates the limit into its
     * English sentence and sends **no `args`**, unlike its sibling `notSubmitted` which does. A
     * placeholder with nothing to fill it makes next-intl report the error and return *the key path*,
     * so the alert read `Refusals.order.rejection.noteTooLong` — precisely the failure
     * [ADR-0012](../../../docs/architecture/adr/0012-server-message-localization.md) exists to
     * prevent, and precisely what `refusals.ts` warns is the cost of the coupling.
     *
     * Fixed on the catalogue side rather than by changing a shipped endpoint's contract for a copy
     * decision.
     */
    rejectOrder.mockRejectedValue(
      new ApiError(400, [
        { field: "note", message: "A note is at most 500 characters.", code: "order.rejection.noteTooLong" },
      ]));

    render(<OrderQueue />);

    await openTheForm();
    await userEvent.click(screen.getByRole("button", { name: "Reject the order" }));

    expect((await screen.findByRole("alert")).textContent).toMatch(/at most 500 characters/);
  });

  it("does not offer the control to somebody who may not reject", async () => {
    // `order:reject` is an operator's permission and a rep does not hold it. Hiding the button is
    // about not offering a door that will not open — the endpoint still checks.
    allow("visit:read");

    render(<OrderQueue />);

    await screen.findByRole("list");

    expect(screen.queryByRole("button", { name: "Reject" })).toBeNull();
  });

  it("does not offer the control on an order already decided", async () => {
    // Only a submitted order can be rejected — the server answers 409 otherwise, and a button that
    // exists to produce a conflict is a button that should not exist.
    fetchOrders.mockResolvedValue([
      {
        ...WAITING,
        status: "Rejected",
        rejection: { reason: "OutletClosed", offendingProductId: null, note: null },
      },
    ]);

    render(<OrderQueue />);

    await screen.findByRole("list");

    expect(screen.queryByRole("button", { name: "Reject" })).toBeNull();
  });
});
