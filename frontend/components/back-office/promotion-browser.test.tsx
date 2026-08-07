// @vitest-environment jsdom

import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";

import type { AuthContextValue } from "@/components/auth-provider";
import { PromotionBrowser } from "@/components/back-office/promotion-browser";
import { ApiError } from "@/lib/api/client";
import { fetchIdentity } from "@/lib/api/identity";
import type { Product } from "@/lib/api/products";
import type { Promotion } from "@/lib/api/promotions";
import { render } from "@/test/render";

const auth = vi.hoisted(() => ({ current: {} as AuthContextValue }));
const fetchPromotions = vi.hoisted(() => vi.fn());
const createPromotion = vi.hoisted(() => vi.fn());
const updatePromotion = vi.hoisted(() => vi.fn());
const fetchProducts = vi.hoisted(() => vi.fn());

vi.mock("@/components/auth-provider", () => ({ useAuth: () => auth.current }));

// The list links out to the targets screen, and next-intl's Link reaches for Next's router — which
// does not resolve outside a Next build. A plain anchor keeps this file about the promotions.
//
// Every prop forwarded, not just href and children. The row's link carries an `aria-label`, and a
// lossy stub would swallow exactly the thing under test while still rendering something plausible.
vi.mock("@/i18n/navigation", () => ({
  Link: ({
    href,
    children,
    ...rest
  }: { href: string; children: React.ReactNode } & React.AnchorHTMLAttributes<HTMLAnchorElement>) => (
    <a href={href} {...rest}>
      {children}
    </a>
  ),
}));

vi.mock("@/lib/api/promotions", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/lib/api/promotions")>()),
  fetchPromotions: (...args: unknown[]) => fetchPromotions(...args),
  createPromotion: (...args: unknown[]) => createPromotion(...args),
  updatePromotion: (...args: unknown[]) => updatePromotion(...args),
}));

vi.mock("@/lib/api/products", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/lib/api/products")>()),
  fetchProducts: (...args: unknown[]) => fetchProducts(...args),
}));

function promotion(over: Partial<Promotion> = {}): Promotion {
  return {
    id: "promo-1",
    name: "Summer 10% off",
    type: "PercentOff",
    // Trailing zeroes on purpose: the string is the value, not a rendering of one.
    value: "10.00",
    currency: null,
    validFrom: "2026-06-01",
    validTo: null,
    priority: 10,
    bundle: null,
    ...over,
  };
}

const PRODUCTS: Product[] = [
  {
    id: "p-still",
    sku: "VRD-STL-100",
    name: "Veridian Still 1L",
    brandId: null,
    categoryId: null,
    taxClassId: null,
    unitOfMeasure: null,
    packSize: null,
    status: "Active",
    customFields: {},
  },
];

/** Opens the create form, having waited for the permission answer that renders the button. */
async function openNew(): Promise<void> {
  await userEvent.click(await screen.findByRole("button", { name: "New promotion" }));
}

describe("<PromotionBrowser>", () => {
  beforeEach(() => {
    fetchPromotions.mockReset().mockResolvedValue([promotion()]);
    createPromotion.mockReset().mockResolvedValue(promotion());
    updatePromotion.mockReset().mockResolvedValue(promotion());
    fetchProducts.mockReset().mockResolvedValue(PRODUCTS);

    vi.mocked(fetchIdentity).mockResolvedValue({
      subject: "subject-a",
      tenant: "fieldkit-dev",
      permissions: ["product:read", "product:write"],
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

  it("shows a discount exactly as the server sent it", async () => {
    // "10.00", not "10". The string is the value — anything that round-trips it through a JS number
    // loses the scale, and BR-PRD-8 covers a percentage for the same reason it covers a price.
    render(<PromotionBrowser />);

    expect(await screen.findByText("10.00% off")).toBeTruthy();
  });

  it("says where a tiered promotion keeps its discount rather than showing nothing", async () => {
    // The API sends null rather than "0.00" for this type, and a blank cell would read as a missing
    // value instead of one kept on the child rows.
    fetchPromotions.mockResolvedValue([
      promotion({ id: "promo-t", name: "Case deal", type: "VolumeTiered", value: null }),
    ]);

    render(<PromotionBrowser />);

    expect(await screen.findByText(/discount on its tiers/i)).toBeTruthy();
  });

  it("summarises a bundle by what it gives, not by a price it never reduces", async () => {
    fetchPromotions.mockResolvedValue([
      promotion({
        id: "promo-b",
        name: "Buy two get one",
        type: "BuyXGetY",
        value: null,
        bundle: { buyQuantity: 2, getQuantity: 1, getPercentOff: "100.00", getProductId: null },
      }),
    ]);

    render(<PromotionBrowser />);

    expect(await screen.findByText(/buy 2, get 1 at 100.00% off/i)).toBeTruthy();
  });

  it("offers the way through to what a promotion discounts", async () => {
    // Its own route, not a section of the form: what a deal *is* and what it *applies to* are
    // decided at different times, and one Save covering both would let a stray tick change which
    // products are discounted while correcting a percentage.
    render(<PromotionBrowser />);

    // Queried as a button, not a link: the shared control renders an anchor with role="button"
    // everywhere in the back office. Named per row, or four deals would offer four identical ones.
    const targets = await screen.findByRole("button", { name: "Targets for Summer 10% off" });

    expect(targets.getAttribute("href")).toBe("/products/promotions/promo-1/targets");
  });

  it("sends a percentage as typed, without parsing it", async () => {
    render(<PromotionBrowser />);
    await openNew();

    await userEvent.type(screen.getByLabelText("Name"), "Autumn deal");
    await userEvent.type(screen.getByLabelText("Percentage off"), "12.50");
    await userEvent.type(screen.getByLabelText("Valid from"), "2026-09-01");
    await userEvent.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => expect(createPromotion).toHaveBeenCalled());

    const sent = createPromotion.mock.calls[0][1];

    expect(sent.value).toBe("12.50");
    expect(typeof sent.value).toBe("string");
  });

  it("omits the value entirely for a type that has none", async () => {
    // The API refuses a value on a VolumeTiered promotion rather than ignoring it — an empty string
    // is not null, so sending `""` would be a refusal about a field the author never saw.
    render(<PromotionBrowser />);
    await openNew();

    await userEvent.selectOptions(screen.getByLabelText("Type"), "VolumeTiered");
    await userEvent.type(screen.getByLabelText("Name"), "Case deal");
    await userEvent.type(screen.getByLabelText("Valid from"), "2026-09-01");
    await userEvent.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => expect(createPromotion).toHaveBeenCalled());

    const sent = createPromotion.mock.calls[0][1];

    expect("value" in sent).toBe(false);
    expect("currency" in sent).toBe(false);
    expect("bundle" in sent).toBe(false);
  });

  it("offers a value box only to the types that carry one", async () => {
    render(<PromotionBrowser />);
    await openNew();

    expect(screen.getByLabelText("Percentage off")).toBeTruthy();
    expect(screen.queryByLabelText("Currency")).toBeNull();

    await userEvent.selectOptions(screen.getByLabelText("Type"), "FixedAmountOff");
    expect(screen.getByLabelText("Amount off")).toBeTruthy();
    expect(screen.getByLabelText("Currency")).toBeTruthy();

    await userEvent.selectOptions(screen.getByLabelText("Type"), "VolumeTiered");
    expect(screen.queryByLabelText("Percentage off")).toBeNull();
    expect(screen.queryByLabelText("Amount off")).toBeNull();
    expect(screen.queryByLabelText("Currency")).toBeNull();
  });

  it("asks for the bundle only when the type gives units away", async () => {
    render(<PromotionBrowser />);
    await openNew();

    expect(screen.queryByLabelText("Bought")).toBeNull();

    await userEvent.selectOptions(screen.getByLabelText("Type"), "BuyXGetY");

    expect(screen.getByLabelText("Bought")).toBeTruthy();
    expect(screen.getByLabelText("Given")).toBeTruthy();
    expect(screen.getByLabelText("Percentage off the given units")).toBeTruthy();
  });

  it("reads a blank giveaway product as the same product that was bought", async () => {
    // The API's null means exactly that, and it is the ordinary case — so the option is a real
    // choice standing for null, not a disabled placeholder an author has to escape from.
    render(<PromotionBrowser />);
    await openNew();

    await userEvent.selectOptions(screen.getByLabelText("Type"), "BuyXGetY");
    await userEvent.type(screen.getByLabelText("Name"), "Buy two get one");
    await userEvent.type(screen.getByLabelText("Valid from"), "2026-09-01");
    await userEvent.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => expect(createPromotion).toHaveBeenCalled());

    expect(createPromotion.mock.calls[0][1].bundle).toEqual({
      buyQuantity: 1,
      getQuantity: 1,
      getPercentOff: "100.00",
      getProductId: null,
    });
  });

  it("fetches the catalogue only for the type that can name a giveaway", async () => {
    render(<PromotionBrowser />);
    await openNew();

    expect(fetchProducts).not.toHaveBeenCalled();

    await userEvent.selectOptions(screen.getByLabelText("Type"), "BuyXGetY");

    await waitFor(() => expect(fetchProducts).toHaveBeenCalled());
    expect(await screen.findByRole("option", { name: /VRD-STL-100/ })).toBeTruthy();
  });

  it("refuses a comma decimal under the box rather than sending it", async () => {
    // "12,50" parses to 1250 under invariant culture if separators are allowed — a hundredfold
    // discount that reads as a plausible one.
    render(<PromotionBrowser />);
    await openNew();

    await userEvent.type(screen.getByLabelText("Percentage off"), "12,50");

    expect(await screen.findByText(/12\.50, not 12,50/)).toBeTruthy();
    expect((screen.getByRole("button", { name: "Save" }) as HTMLButtonElement).disabled).toBe(true);

    await userEvent.click(screen.getByRole("button", { name: "Save" }));
    expect(createPromotion).not.toHaveBeenCalled();
  });

  it("fixes the type and the currency once a promotion exists", async () => {
    // Re-typing would reinterpret the value — 15 meaning "15% off" becoming 15 meaning "€15 off" —
    // and every order already priced against it would be explained by a rule that no longer exists.
    fetchPromotions.mockResolvedValue([
      promotion({ type: "FixedAmountOff", value: "5.00", currency: "EUR" }),
    ]);

    render(<PromotionBrowser />);

    await userEvent.click(await screen.findByRole("button", { name: "Edit Summer 10% off" }));

    expect((screen.getByLabelText("Type") as HTMLSelectElement).disabled).toBe(true);
    expect((screen.getByLabelText("Currency") as HTMLInputElement).disabled).toBe(true);
  });

  it("never sends a currency on an update, because the server keeps its own", async () => {
    fetchPromotions.mockResolvedValue([
      promotion({ type: "FixedAmountOff", value: "5.00", currency: "EUR" }),
    ]);

    render(<PromotionBrowser />);

    await userEvent.click(await screen.findByRole("button", { name: "Edit Summer 10% off" }));
    await userEvent.clear(screen.getByLabelText("Amount off"));
    await userEvent.type(screen.getByLabelText("Amount off"), "6.00");
    await userEvent.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => expect(updatePromotion).toHaveBeenCalled());

    const sent = updatePromotion.mock.calls[0][2];

    expect(sent.value).toBe("6.00");
    expect("currency" in sent).toBe(false);
    expect("type" in sent).toBe(false);
  });

  it("sends the priority as a number, because the API counts with it", async () => {
    render(<PromotionBrowser />);
    await openNew();

    await userEvent.type(screen.getByLabelText("Name"), "Autumn deal");
    await userEvent.type(screen.getByLabelText("Percentage off"), "5");
    await userEvent.type(screen.getByLabelText("Valid from"), "2026-09-01");
    await userEvent.clear(screen.getByLabelText("Priority"));
    await userEvent.type(screen.getByLabelText("Priority"), "20");
    await userEvent.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => expect(createPromotion).toHaveBeenCalled());
    expect(createPromotion.mock.calls[0][1].priority).toBe(20);
  });

  it("says priority decides, not the size of the discount", async () => {
    // The intuition runs the other way round: an author who expects "the best deal wins" will
    // author one that never fires, and BR-PRD-3 never breaks a tie on discount size.
    render(<PromotionBrowser />);
    await openNew();

    expect(screen.getByText(/Higher wins .* the discount size never breaks the tie/i)).toBeTruthy();
  });

  it("keeps the window half-open in what it tells an author", async () => {
    render(<PromotionBrowser />);
    await openNew();

    expect(screen.getByText(/first day it no longer applies/i)).toBeTruthy();
  });

  it("says which permission is missing rather than that something failed", async () => {
    fetchPromotions.mockRejectedValue(new ApiError(403));

    render(<PromotionBrowser />);

    expect(await screen.findByText(/do not have permission/i)).toBeTruthy();
    expect(screen.queryByRole("button", { name: "New promotion" })).toBeNull();
  });

  it("offers no way to change anything to a caller who may only read", async () => {
    vi.mocked(fetchIdentity).mockResolvedValue({
      subject: "subject-a",
      tenant: "fieldkit-dev",
      permissions: ["product:read"],
    });

    render(<PromotionBrowser />);

    // Still readable: which deals exist and which one outranks which is the question a reader came
    // to answer.
    expect(await screen.findByText("Summer 10% off")).toBeTruthy();
    expect(screen.getByText("10.00% off")).toBeTruthy();

    await waitFor(() =>
      expect(screen.queryByRole("button", { name: "New promotion" })).toBeNull(),
    );
    expect(screen.queryByRole("button", { name: "Edit Summer 10% off" })).toBeNull();
  });

  it("passes on a refusal in the API's own words", async () => {
    createPromotion.mockRejectedValue(
      new ApiError(400, [
        { field: "name", message: "A promotion named 'Autumn deal' already exists." },
      ]),
    );

    render(<PromotionBrowser />);
    await openNew();

    await userEvent.type(screen.getByLabelText("Name"), "Autumn deal");
    await userEvent.type(screen.getByLabelText("Percentage off"), "5");
    await userEvent.type(screen.getByLabelText("Valid from"), "2026-09-01");
    await userEvent.click(screen.getByRole("button", { name: "Save" }));

    expect((await screen.findByRole("alert")).textContent).toContain("already exists");
  });
});
