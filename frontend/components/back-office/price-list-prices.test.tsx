// @vitest-environment jsdom

import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";

import type { AuthContextValue } from "@/components/auth-provider";
import { PriceListPrices } from "@/components/back-office/price-list-prices";
import { ApiError } from "@/lib/api/client";
import { fetchIdentity } from "@/lib/api/identity";
import type { PriceLine, PriceList } from "@/lib/api/price-lists";
import type { Product } from "@/lib/api/products";
import { render } from "@/test/render";

const auth = vi.hoisted(() => ({ current: {} as AuthContextValue }));
const fetchPriceLists = vi.hoisted(() => vi.fn());
const fetchPrices = vi.hoisted(() => vi.fn());
const setPrices = vi.hoisted(() => vi.fn());
const fetchProducts = vi.hoisted(() => vi.fn());

vi.mock("@/components/auth-provider", () => ({ useAuth: () => auth.current }));
vi.mock("next/navigation", () => ({ useParams: () => ({ id: "pl-1" }) }));

// The header now renders a <Breadcrumb>, which reads the path through next-intl. Stubbed the way
// the navigation's own tests stub it, so these assertions stay about this screen.
vi.mock("@/i18n/navigation", () => ({
  Link: ({ href, children }: { href: string; children: React.ReactNode }) => (
    <a href={href}>{children}</a>
  ),
  usePathname: () => "/products/price-lists/pl-1",
}));

vi.mock("@/lib/api/price-lists", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/lib/api/price-lists")>()),
  fetchPriceLists: (...args: unknown[]) => fetchPriceLists(...args),
  fetchPrices: (...args: unknown[]) => fetchPrices(...args),
  setPrices: (...args: unknown[]) => setPrices(...args),
}));

vi.mock("@/lib/api/products", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/lib/api/products")>()),
  fetchProducts: (...args: unknown[]) => fetchProducts(...args),
}));

/** Waits for the permission answer — see the note in outlet-assortment.test.tsx. */
async function ready(): Promise<void> {
  await screen.findByRole("button", { name: "Save prices" });
}

const LIST: PriceList = {
  id: "pl-1",
  name: "Modern Trade 2026",
  currency: "EUR",
  effectiveFrom: "2026-01-01",
  effectiveTo: null,
};

function product(id: string, sku: string, name: string): Product {
  return {
    id,
    sku,
    name,
    brandId: null,
    categoryId: null,
    taxClassId: null,
    unitOfMeasure: null,
    packSize: null,
    status: "Active",
    customFields: {},
  };
}

const PRODUCTS: Product[] = [
  product("p-still", "VRD-STL-100", "Veridian Still 1L"),
  product("p-spark", "VRD-SPK-050", "Veridian Sparkling 0.5L"),
];

/** Trailing zeroes on purpose: the string is the value, not a rendering of one. */
const PRICES: PriceLine[] = [
  {
    productId: "p-still",
    sku: "VRD-STL-100",
    name: "Veridian Still 1L",
    price: { amount: "12.50", currency: "EUR" },
  },
];

describe("<PriceListPrices>", () => {
  beforeEach(() => {
    fetchPriceLists.mockReset().mockResolvedValue([LIST]);
    fetchPrices.mockReset().mockResolvedValue(PRICES);
    fetchProducts.mockReset().mockResolvedValue(PRODUCTS);
    setPrices.mockReset().mockResolvedValue(PRICES);

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

  it("shows a stored amount exactly as the server sent it", async () => {
    // "12.50", not "12.5". The string is the value — anything that round-trips it through a JS
    // number loses the scale, and BR-PRD-8 exists so that never happens.
    render(<PriceListPrices />);
    await ready();

    expect((screen.getByLabelText("Price for Veridian Still 1L") as HTMLInputElement).value).toBe("12.50");
  });

  it("sends amounts as typed, without parsing them", async () => {
    render(<PriceListPrices />);
    await ready();

    await userEvent.type(screen.getByLabelText("Price for Veridian Sparkling 0.5L"), "9.90");
    await userEvent.click(screen.getByRole("button", { name: "Save prices" }));

    await waitFor(() => expect(setPrices).toHaveBeenCalled());

    const sent = setPrices.mock.calls[0][2] as { productId: string; amount: unknown }[];

    expect(sent).toEqual(
      expect.arrayContaining([
        { productId: "p-still", amount: "12.50" },
        { productId: "p-spark", amount: "9.90" },
      ]),
    );

    // The assertion that matters: strings on the wire, never numbers.
    expect(sent.every((line) => typeof line.amount === "string")).toBe(true);
  });

  it("refuses a comma decimal under the box rather than sending it", async () => {
    // "12,50" parses to 1250 under invariant culture if separators are allowed — a hundredfold
    // error that reads as a plausible price. The server refuses it too; catching it here means the
    // message lands on the field instead of arriving as a refusal about a list.
    render(<PriceListPrices />);
    await ready();

    await userEvent.type(screen.getByLabelText("Price for Veridian Sparkling 0.5L"), "12,50");

    expect(await screen.findByText(/12\.50, not 12,50/)).toBeTruthy();
    expect((screen.getByRole("button", { name: "Save prices" }) as HTMLButtonElement).disabled).toBe(true);

    await userEvent.click(screen.getByRole("button", { name: "Save prices" }));
    expect(setPrices).not.toHaveBeenCalled();
  });

  it("uses a text box, so a comma-decimal locale cannot swallow the value", async () => {
    // type="number" hands back a value the browser has already interpreted, and on a comma-decimal
    // locale it reports "12,50" as empty — a price that silently disappears on save.
    render(<PriceListPrices />);
    await ready();

    const box = screen.getByLabelText("Price for Veridian Still 1L") as HTMLInputElement;

    expect(box.type).toBe("text");
    expect(box.inputMode).toBe("decimal");
  });

  it("treats a blank as unpriced rather than as zero", async () => {
    // The API replaces the price set, so omission is how a product becomes unpriced in this list.
    // Sending "0" instead would price it at nothing, which is a real and different decision.
    render(<PriceListPrices />);
    await ready();

    await userEvent.clear(screen.getByLabelText("Price for Veridian Still 1L"));
    await userEvent.click(screen.getByRole("button", { name: "Save prices" }));

    await waitFor(() => expect(setPrices).toHaveBeenCalled());
    expect(setPrices.mock.calls[0][2]).toEqual([]);
  });

  it("shows the list's currency once, not per row", async () => {
    // A list has exactly one (BR-PRD-1), which is what makes its prices summable.
    render(<PriceListPrices />);
    await ready();

    expect(screen.getByText(/Net prices in EUR/)).toBeTruthy();
  });

  it("counts what is priced against the whole catalogue", async () => {
    render(<PriceListPrices />);
    await ready();

    expect(screen.getByRole("status").textContent).toContain("1 of 2 products priced");
  });

  it("offers nothing to save until an amount changes", async () => {
    render(<PriceListPrices />);

    const save = await screen.findByRole("button", { name: "Save prices" });
    expect((save as HTMLButtonElement).disabled).toBe(true);

    await userEvent.type(screen.getByLabelText("Price for Veridian Sparkling 0.5L"), "1.00");
    expect((save as HTMLButtonElement).disabled).toBe(false);

    await userEvent.clear(screen.getByLabelText("Price for Veridian Sparkling 0.5L"));
    expect((save as HTMLButtonElement).disabled).toBe(true);
  });

  it("says a price list that does not exist is missing rather than broken", async () => {
    fetchPriceLists.mockResolvedValue([]);

    render(<PriceListPrices />);

    expect(await screen.findByText(/does not exist/i)).toBeTruthy();
  });

  it("says which permission is missing rather than that something failed", async () => {
    fetchPrices.mockRejectedValue(new ApiError(403));

    render(<PriceListPrices />);

    expect(await screen.findByText(/do not have permission/i)).toBeTruthy();
  });

  it("offers no way to change anything to a caller who may only read", async () => {
    vi.mocked(fetchIdentity).mockResolvedValue({
      subject: "subject-a",
      tenant: "fieldkit-dev",
      permissions: ["product:read"],
    });

    render(<PriceListPrices />);

    const box = await screen.findByLabelText("Price for Veridian Still 1L");

    await waitFor(() => expect((box as HTMLInputElement).disabled).toBe(true));
    expect(screen.queryByRole("button", { name: "Save prices" })).toBeNull();
    expect((box as HTMLInputElement).value).toBe("12.50");
  });

  it("passes on a refusal in the API's own words", async () => {
    setPrices.mockRejectedValue(
      new ApiError(400, [{ field: "prices", message: "'12,50' is not a decimal amount." }]),
    );

    render(<PriceListPrices />);
    await ready();

    await userEvent.type(screen.getByLabelText("Price for Veridian Sparkling 0.5L"), "9.90");
    await userEvent.click(screen.getByRole("button", { name: "Save prices" }));

    expect((await screen.findByRole("alert")).textContent).toContain("not a decimal amount");
  });
});
