// @vitest-environment jsdom

import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";

import type { AuthContextValue } from "@/components/auth-provider";
import { OrderMinimums } from "@/components/back-office/order-minimums";
import type { Channel } from "@/lib/api/channels";
import { ApiError } from "@/lib/api/client";
import { fetchIdentity } from "@/lib/api/identity";
import type { OrderMinimum } from "@/lib/api/order-minimums";
import type { OutletDetail } from "@/lib/api/outlets";
import type { PriceList } from "@/lib/api/price-lists";
import { render } from "@/test/render";

/**
 * Where a minimum is set (`ORD-06`, `BR-ORD-5`) — W11 slice 8b-iii.
 *
 * The rule itself is checked in `OrderMinimumTests` (server) and `lib/pricing/order-minimum.test.ts`
 * (device). What is only visible from here is the **authoring contract**, and one clause of it does
 * real damage if it slips: the `PUT` replaces the whole set, so every row the screen holds must be
 * in every save. A screen that sent only what an author touched would withdraw the rest — silently,
 * and the symptom would be a rep somewhere no longer being refused.
 */
const auth = vi.hoisted(() => ({ current: {} as AuthContextValue }));
const fetchOrderMinimums = vi.hoisted(() => vi.fn());
const setOrderMinimums = vi.hoisted(() => vi.fn());
const fetchChannels = vi.hoisted(() => vi.fn());
const fetchPriceLists = vi.hoisted(() => vi.fn());
const fetchOutlets = vi.hoisted(() => vi.fn());
const fetchOutlet = vi.hoisted(() => vi.fn());

vi.mock("@/components/auth-provider", () => ({ useAuth: () => auth.current }));

vi.mock("@/lib/api/order-minimums", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/lib/api/order-minimums")>()),
  fetchOrderMinimums: (...args: unknown[]) => fetchOrderMinimums(...args),
  setOrderMinimums: (...args: unknown[]) => setOrderMinimums(...args),
}));

vi.mock("@/lib/api/channels", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/lib/api/channels")>()),
  fetchChannels: (...args: unknown[]) => fetchChannels(...args),
}));

vi.mock("@/lib/api/price-lists", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/lib/api/price-lists")>()),
  fetchPriceLists: (...args: unknown[]) => fetchPriceLists(...args),
}));

vi.mock("@/lib/api/outlets", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/lib/api/outlets")>()),
  fetchOutlets: (...args: unknown[]) => fetchOutlets(...args),
  fetchOutlet: (...args: unknown[]) => fetchOutlet(...args),
}));

/**
 * Waits for the permission answer.
 *
 * `usePermissions` lives in the editor, whose identity query starts a tick after the first paint —
 * asserting before it lands finds boxes that are still disabled, and typing into a disabled input
 * passes by doing nothing.
 */
async function ready(): Promise<void> {
  await screen.findByRole("button", { name: "Save minimums" });
}

const CHANNELS: Channel[] = [
  { id: "ch-mt", name: "Modern Trade" },
  { id: "ch-tt", name: "Traditional Trade" },
];

const LISTS: PriceList[] = [
  {
    id: "pl-1",
    name: "Standard",
    currency: "RON",
    effectiveFrom: "2026-01-01",
    effectiveTo: null,
  },
];

function outlet(id: string, code: string, name: string): OutletDetail {
  return {
    id,
    code,
    name,
    channelId: "ch-mt",
    channelName: "Modern Trade",
    segment: null,
    banner: null,
    status: "Active",
    territory: null,
    timeZoneId: "Europe/Bucharest",
    address: null,
    location: null,
    contacts: [],
    customFields: {},
  };
}

const CENTRAL = outlet("out-1", "RO-0001", "Veridian Central");

const STORED: OrderMinimum[] = [
  { id: "m-1", channelId: "ch-mt", outletId: null, amount: "150.00", currencyCode: "RON" },
  { id: "m-2", channelId: null, outletId: "out-1", amount: "50.00", currencyCode: "RON" },
];

const amountFor = (scope: string) => screen.getByLabelText(`Minimum for ${scope}`) as HTMLInputElement;
const currencyFor = (scope: string) =>
  screen.getByLabelText(`Currency for ${scope}`) as HTMLInputElement;

describe("<OrderMinimums>", () => {
  beforeEach(() => {
    fetchOrderMinimums.mockReset().mockResolvedValue(STORED);
    setOrderMinimums.mockReset().mockResolvedValue(STORED);
    fetchChannels.mockReset().mockResolvedValue(CHANNELS);
    fetchPriceLists.mockReset().mockResolvedValue(LISTS);
    fetchOutlets.mockReset().mockResolvedValue({ items: [], total: 0, page: 1, pageSize: 10 });
    fetchOutlet.mockReset().mockResolvedValue(CENTRAL);

    vi.mocked(fetchIdentity).mockResolvedValue({
      subject: "subject-a",
      tenant: "fieldkit-dev",
      permissions: ["product:read", "product:write", "outlet:read", "channel:read"],
    });

    auth.current = {
      status: "authenticated",
      user: { access_token: "token", profile: { sub: "subject-a" } },
      workspace: "fieldkit-dev",
      signIn: vi.fn(),
      signOut: vi.fn(),
      completeSignIn: vi.fn(),
    } as unknown as AuthContextValue;
  });

  it("seeds each scope from what the server holds, and leaves the rest blank", async () => {
    render(<OrderMinimums />);
    await ready();

    expect(amountFor("Modern Trade").value).toBe("150.00");
    expect(currencyFor("Modern Trade").value).toBe("RON");

    // Blank is a real state and the ordinary one: no minimum in Traditional Trade.
    expect(amountFor("Traditional Trade").value).toBe("");
    expect(currencyFor("Traditional Trade").value).toBe("");
  });

  it("names an outlet that already has one rather than showing its id", async () => {
    // The row carries an id and nothing else; a screen that showed it raw would be asking an author
    // to recognise a GUID.
    render(<OrderMinimums />);
    await ready();

    expect(amountFor("Veridian Central · RO-0001").value).toBe("50.00");
  });

  it("sends every scope that has an amount, not only the one that was edited", async () => {
    /*
     * The clause of the authoring contract that does real damage if it slips. The `PUT` replaces the
     * whole set, so a save carrying only the edited row would withdraw every other minimum — and the
     * symptom is a rep somewhere no longer being refused, which nothing on this screen would show.
     */
    render(<OrderMinimums />);
    await ready();

    await userEvent.clear(amountFor("Traditional Trade"));
    await userEvent.type(amountFor("Traditional Trade"), "80");

    await userEvent.click(screen.getByRole("button", { name: "Save minimums" }));

    await waitFor(() => expect(setOrderMinimums).toHaveBeenCalled());

    const sent = setOrderMinimums.mock.calls[0][1];

    expect(sent).toHaveLength(3);
    expect(sent).toContainEqual({
      channelId: "ch-mt",
      outletId: null,
      amount: "150.00",
      currencyCode: "RON",
    });
    expect(sent).toContainEqual({
      channelId: "ch-tt",
      outletId: null,
      amount: "80",
      currencyCode: "RON",
    });
    expect(sent).toContainEqual({
      channelId: null,
      outletId: "out-1",
      amount: "50.00",
      currencyCode: "RON",
    });
  });

  it("withdraws a minimum by clearing its amount, with no delete button", async () => {
    // `BR-ORD-5` applies a minimum *if configured*, and the server refuses zero precisely so that
    // "none" and "a minimum of nothing" cannot become two different states. Clearing is the gesture.
    render(<OrderMinimums />);
    await ready();

    await userEvent.clear(amountFor("Modern Trade"));
    await userEvent.click(screen.getByRole("button", { name: "Save minimums" }));

    await waitFor(() => expect(setOrderMinimums).toHaveBeenCalled());

    const sent = setOrderMinimums.mock.calls[0][1];

    expect(sent).toHaveLength(1);
    expect(sent[0].outletId).toBe("out-1");
  });

  it("suggests the currency the tenant prices in, once an amount is typed", async () => {
    /*
     * `BR-ORD-7` takes an order's currency from the list that priced it, and nothing makes that agree
     * with what somebody types here. A mismatch is a refusal the **rep** meets, at a counter, about a
     * misconfiguration they cannot fix — so the suggestion is what stops it happening at all.
     *
     * Only once an amount exists: a currency box filled in beside an empty amount reads as a minimum
     * somebody forgot to finish.
     */
    render(<OrderMinimums />);
    await ready();

    expect(currencyFor("Traditional Trade").value).toBe("");

    await userEvent.type(amountFor("Traditional Trade"), "8");

    expect(currencyFor("Traditional Trade").value).toBe("RON");
  });

  it("suggests nothing when the tenant prices in more than one currency", async () => {
    // No single right answer — the currency that matters is the one on the list reaching *this*
    // channel. An empty box asks; a wrong guess would save without complaint.
    fetchPriceLists.mockResolvedValue([
      ...LISTS,
      { id: "pl-2", name: "Export", currency: "EUR", effectiveFrom: "2026-01-01", effectiveTo: null },
    ]);

    render(<OrderMinimums />);
    await ready();

    await userEvent.type(amountFor("Traditional Trade"), "8");

    expect(currencyFor("Traditional Trade").value).toBe("");
  });

  it.each(["12,50", "1e3", "Infinity"])(
    "refuses to save “%s” before sending it",
    async (amount) => {
      /*
       * `"12,50"` is the case the shared `looksLikeAnAmount` was written for: refused rather than
       * read as 12.50, because invariant parsing would make it **1250** if thousands separators were
       * allowed — a hundredfold error that reads as a plausible threshold.
       *
       * `"1e3"` and `"Infinity"` are the ones that separate it from `Number(value)`, which accepts
       * both. A sabotage pass swapping in the naive check left the `"12,50"` case green —
       * `Number("12,50")` is `NaN` too — so on its own that case proved only that *something*
       * rejects malformed input, not which something.
       *
       * `" 12 "` is **not** in this list, and the helper's own comment used to claim it was: it
       * trims before matching, so surrounding space is accepted and the trimmed value is what gets
       * sent. Corrected there while writing this.
       *
       * The server refuses all of these regardless; what is at stake here is whether the author is
       * told beside the field or by a round trip.
       */
      render(<OrderMinimums />);
      await ready();

      await userEvent.clear(amountFor("Modern Trade"));
      await userEvent.type(amountFor("Modern Trade"), amount);

      expect(amountFor("Modern Trade").getAttribute("aria-invalid")).toBe("true");
      expect(
        (screen.getByRole("button", { name: "Save minimums" }) as HTMLButtonElement).disabled,
      ).toBe(true);
      expect(setOrderMinimums).not.toHaveBeenCalled();
    },
  );

  it("refuses zero rather than sending a minimum of nothing", async () => {
    render(<OrderMinimums />);
    await ready();

    await userEvent.clear(amountFor("Modern Trade"));
    await userEvent.type(amountFor("Modern Trade"), "0");

    expect(amountFor("Modern Trade").getAttribute("aria-invalid")).toBe("true");
  });

  it("keeps work in progress when the server's answer is refetched", async () => {
    /*
     * **Regression, found in a browser.** The editor was keyed on the query's `dataUpdatedAt` so it
     * would reseed after a save — which meant *any* refetch remounted it, and React Query refetches
     * on window focus. An author who alt-tabbed mid-edit came back to an empty screen: every amount
     * typed, every outlet searched for and added, gone, with nothing said.
     *
     * No unit test could see it because none of them refetch. This one drives the refetch through
     * the save, which is the same mechanism: the mutation invalidates, the query re-runs, and the
     * editor must survive it.
     */
    render(<OrderMinimums />);
    await ready();

    // Added but deliberately left blank — so it is not part of what the save sends, and can only
    // still be on screen afterwards if the editor was not thrown away.
    fetchOutlets.mockResolvedValue({
      items: [outlet("out-2", "RO-0002", "Veridian North")],
      total: 1,
      page: 1,
      pageSize: 10,
    });

    await userEvent.type(screen.getByLabelText("Search outlets"), "RO-0002");

    await userEvent.click(await screen.findByRole("button", { name: "Add Veridian North (RO-0002)" }));

    expect(amountFor("Veridian North · RO-0002")).toBeTruthy();

    await userEvent.type(amountFor("Traditional Trade"), "80");
    await userEvent.click(screen.getByRole("button", { name: "Save minimums" }));

    await waitFor(() => expect(setOrderMinimums).toHaveBeenCalled());

    // The refetch has landed; the row the author added is still theirs to fill in.
    await waitFor(() => expect(amountFor("Veridian North · RO-0002")).toBeTruthy());
  });

  it("shows a server refusal in the reader's language", async () => {
    // ADR-0012 stage 2: the code is translated, and an untranslated one falls back to the server's
    // own sentence rather than to a dotted name.
    setOrderMinimums.mockRejectedValue(
      new ApiError(400, [
        {
          field: "minimums",
          message: "No such channel in this tenant.",
          code: "product.orderMinimum.unknownChannel",
        },
      ]),
    );

    render(<OrderMinimums />);
    await ready();

    await userEvent.type(amountFor("Traditional Trade"), "80");
    await userEvent.click(screen.getByRole("button", { name: "Save minimums" }));

    expect((await screen.findByRole("alert")).textContent).toContain("That channel does not exist.");
  });

  it("shows a reader the minimums without offering to change them", async () => {
    // `product:read` without `product:write`. The boxes still show what is configured — a reader who
    // could not see the thresholds could not answer why a rep was refused.
    vi.mocked(fetchIdentity).mockResolvedValue({
      subject: "subject-a",
      tenant: "fieldkit-dev",
      permissions: ["product:read", "outlet:read", "channel:read"],
    });

    render(<OrderMinimums />);

    expect(await screen.findByText("Modern Trade")).toBeTruthy();

    await waitFor(() => expect(amountFor("Modern Trade").disabled).toBe(true));
    expect(screen.queryByRole("button", { name: "Save minimums" })).toBeNull();
  });
});
