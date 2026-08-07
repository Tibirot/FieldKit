// @vitest-environment jsdom

import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";

import type { AuthContextValue } from "@/components/auth-provider";
import { ProductBrowser } from "@/components/back-office/product-browser";
import { ApiError } from "@/lib/api/client";
import { fetchIdentity } from "@/lib/api/identity";
import type { Category, Product, Vocabulary } from "@/lib/api/products";
import { render } from "@/test/render";

const auth = vi.hoisted(() => ({ current: {} as AuthContextValue }));
const fetchProducts = vi.hoisted(() => vi.fn());
const fetchBrands = vi.hoisted(() => vi.fn());
const fetchCategories = vi.hoisted(() => vi.fn());
const fetchTaxClasses = vi.hoisted(() => vi.fn());
const createProduct = vi.hoisted(() => vi.fn());
const updateProduct = vi.hoisted(() => vi.fn());
const fetchFieldDefinitions = vi.hoisted(() => vi.fn());

vi.mock("@/components/auth-provider", () => ({ useAuth: () => auth.current }));

vi.mock("@/lib/api/products", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/lib/api/products")>()),
  fetchProducts: (...args: unknown[]) => fetchProducts(...args),
  fetchBrands: (...args: unknown[]) => fetchBrands(...args),
  fetchCategories: (...args: unknown[]) => fetchCategories(...args),
  fetchTaxClasses: (...args: unknown[]) => fetchTaxClasses(...args),
  createProduct: (...args: unknown[]) => createProduct(...args),
  updateProduct: (...args: unknown[]) => updateProduct(...args),
}));

vi.mock("@/lib/api/field-definitions", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/lib/api/field-definitions")>()),
  fetchFieldDefinitions: (...args: unknown[]) => fetchFieldDefinitions(...args),
}));

const BRANDS: Vocabulary[] = [{ id: "b-veridian", name: "Veridian" }];
const TAX_CLASSES: Vocabulary[] = [{ id: "t-reduced", name: "Reduced" }];

/** Two leaves called "Other" under different parents — the case a flat dropdown gets wrong. */
const CATEGORIES: Category[] = [
  { id: "c-bev", name: "Beverages", parentId: null },
  { id: "c-water", name: "Water", parentId: "c-bev" },
  { id: "c-still", name: "Still", parentId: "c-water" },
  { id: "c-food", name: "Food", parentId: null },
  { id: "c-other-bev", name: "Other", parentId: "c-bev" },
  { id: "c-other-food", name: "Other", parentId: "c-food" },
];

const PRODUCTS: Product[] = [
  {
    id: "p-1",
    sku: "VRD-STL-100",
    name: "Veridian Still 1L",
    brandId: "b-veridian",
    categoryId: "c-still",
    taxClassId: "t-reduced",
    unitOfMeasure: "CS",
    packSize: 12,
    status: "Active",
    customFields: {},
  },
  {
    id: "p-2",
    sku: "VRD-SPK-050",
    name: "Veridian Sparkling 0.5L",
    brandId: null,
    categoryId: null,
    taxClassId: null,
    unitOfMeasure: null,
    packSize: null,
    status: "Discontinued",
    customFields: {},
  },
];

describe("<ProductBrowser>", () => {
  beforeEach(() => {
    fetchProducts.mockReset().mockResolvedValue(PRODUCTS);
    fetchBrands.mockReset().mockResolvedValue(BRANDS);
    fetchCategories.mockReset().mockResolvedValue(CATEGORIES);
    fetchTaxClasses.mockReset().mockResolvedValue(TAX_CLASSES);
    fetchFieldDefinitions.mockReset().mockResolvedValue([]);
    createProduct.mockReset().mockResolvedValue(PRODUCTS[0]);
    updateProduct.mockReset().mockResolvedValue(PRODUCTS[0]);

    // Restored per test, not left to the setup file's default. One test below narrows the caller to
    // product:read, and `mockResolvedValue` on a module-level mock persists — so without this, every
    // test declared after it would silently run as a read-only user and pass for the wrong reason.
    vi.mocked(fetchIdentity).mockResolvedValue({
      subject: "subject-a",
      tenant: "fieldkit-dev",
      permissions: ["product:read", "product:write", "config:read"],
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

  it("lists the catalogue with the classification spelled out rather than as ids", async () => {
    render(<ProductBrowser />);

    const items = await screen.findAllByRole("listitem");

    expect(items).toHaveLength(2);
    expect(items[0].textContent).toContain("VRD-STL-100");
    expect(items[0].textContent).toContain("Veridian");
    expect(items[0].textContent).toContain("Beverages / Water / Still");
    expect(items[0].textContent).toContain("CS × 12");
  });

  it("says a product is discontinued rather than hiding it", async () => {
    // A discontinued line is still in the catalogue and still on old orders. Filtering it out would
    // make it look deleted, and the SKU cannot be reused.
    render(<ProductBrowser />);

    const items = await screen.findAllByRole("listitem");

    expect(items[1].textContent).toContain("Discontinued");
  });

  it("searches by SKU and by name", async () => {
    render(<ProductBrowser />);
    await screen.findAllByRole("listitem");

    const search = screen.getByRole("searchbox", { name: /search products/i });

    await userEvent.type(search, "SPK");
    expect((await screen.findAllByRole("listitem")).map((i) => i.textContent)).toHaveLength(1);

    await userEvent.clear(search);
    await userEvent.type(search, "still");

    const byName = await screen.findAllByRole("listitem");
    expect(byName).toHaveLength(1);
    expect(byName[0].textContent).toContain("VRD-STL-100");
  });

  it("tells an empty catalogue apart from a search that found nothing", async () => {
    // Two different nothings: "add your first product" and "try a different word" are different
    // instructions, and one message for both would be wrong half the time.
    render(<ProductBrowser />);
    await screen.findAllByRole("listitem");

    await userEvent.type(screen.getByRole("searchbox", { name: /search products/i }), "zzz");
    expect(await screen.findByText(/no product matches/i)).toBeTruthy();

    fetchProducts.mockResolvedValue([]);
    render(<ProductBrowser />);

    expect(await screen.findByText(/no products yet/i)).toBeTruthy();
  });

  it("says which permission is missing rather than that something failed", async () => {
    fetchProducts.mockRejectedValue(new ApiError(403));

    render(<ProductBrowser />);

    expect(await screen.findByText(/do not have permission/i)).toBeTruthy();
  });

  it("offers no search box over a list it could not load", async () => {
    // Found in the browser, signed in as an admin — who holds no product permissions. The message
    // rendered correctly and a search box sat above it, filtering a list that was not there. A dead
    // control that explains nothing is what the navigation refuses to render for the same reason,
    // and the original version of this suite missed it by asserting only that the message appeared.
    fetchProducts.mockRejectedValue(new ApiError(403));

    render(<ProductBrowser />);
    await screen.findByText(/do not have permission/i);

    expect(screen.queryByRole("searchbox")).toBeNull();
    expect(screen.queryByRole("button", { name: /new product/i })).toBeNull();
  });

  it("offers no way to write to a caller who may only read", async () => {
    // Someone may maintain the catalogue's *use* — assortments, price lists — without being able to
    // mint SKUs. Overridden at the fetch boundary so the hook and its "pending counts as denied"
    // rule still run.
    vi.mocked(fetchIdentity).mockResolvedValue({
      subject: "subject-a",
      tenant: "fieldkit-dev",
      permissions: ["product:read"],
    });

    render(<ProductBrowser />);
    await screen.findAllByRole("listitem");

    expect(screen.queryByRole("button", { name: /new product/i })).toBeNull();
    expect(screen.queryByRole("button", { name: /edit veridian still 1l/i })).toBeNull();
    expect(screen.getByText("Veridian Still 1L")).toBeTruthy();
  });

  it("creates a product, sending the SKU", async () => {
    render(<ProductBrowser />);
    await screen.findAllByRole("listitem");

    await userEvent.click(screen.getByRole("button", { name: /new product/i }));

    await userEvent.type(screen.getByLabelText(/^SKU/), "VRD-STL-330");
    await userEvent.type(screen.getByLabelText(/^Name/), "Veridian Still 330ml");
    await userEvent.click(screen.getByRole("button", { name: /^save$/i }));

    await waitFor(() => expect(createProduct).toHaveBeenCalled());

    expect(createProduct.mock.calls[0][1]).toMatchObject({
      sku: "VRD-STL-330",
      name: "Veridian Still 330ml",
      // Optional everywhere, and absent means null rather than "" — the shape the API expects.
      brandId: null,
      categoryId: null,
      taxClassId: null,
      packSize: null,
      status: "Active",
    });
  });

  it("cannot change a SKU when editing, and does not send one", async () => {
    // The API has no parameter for it: a different code is a different product, and every order line
    // already pointing at this id would then describe something else.
    render(<ProductBrowser />);
    await screen.findAllByRole("listitem");

    await userEvent.click(screen.getByRole("button", { name: /edit veridian still 1l/i }));

    expect(screen.getByLabelText(/^SKU/)).toHaveProperty("disabled", true);

    await userEvent.click(screen.getByRole("button", { name: /^save$/i }));

    await waitFor(() => expect(updateProduct).toHaveBeenCalled());
    expect(updateProduct.mock.calls[0][2]).not.toHaveProperty("sku");
  });

  it("puts a refusal the API attributed under the field it named", async () => {
    createProduct.mockRejectedValue(
      new ApiError(400, [{ field: "sku", message: "A product with SKU 'VRD-STL-330' already exists." }]),
    );

    render(<ProductBrowser />);
    await screen.findAllByRole("listitem");

    await userEvent.click(screen.getByRole("button", { name: /new product/i }));
    await userEvent.type(screen.getByLabelText(/^SKU/), "VRD-STL-330");
    await userEvent.type(screen.getByLabelText(/^Name/), "Duplicate");
    await userEvent.click(screen.getByRole("button", { name: /^save$/i }));

    expect(await screen.findByText(/already exists/)).toBeTruthy();
  });

  it("shows a category with its ancestry, so two leaves called Other are distinguishable", async () => {
    render(<ProductBrowser />);
    await screen.findAllByRole("listitem");

    await userEvent.click(screen.getByRole("button", { name: /new product/i }));

    const options = Array.from(
      screen.getByLabelText(/^Category/).querySelectorAll("option"),
    ).map((option) => option.textContent);

    expect(options).toContain("Beverages / Other");
    expect(options).toContain("Food / Other");
    expect(options).toContain("Beverages / Water / Still");
  });

  it("says an unauthored vocabulary is optional rather than rendering a silent blank", async () => {
    // Unlike an outlet's channel (BR-OUT-1), a product's classification is optional — so an empty
    // list narrows the catalogue rather than blocking it, and the screen says which.
    fetchBrands.mockResolvedValue([]);

    render(<ProductBrowser />);
    await screen.findAllByRole("listitem");

    await userEvent.click(screen.getByRole("button", { name: /new product/i }));

    expect(await screen.findByText(/none authored yet/i)).toBeTruthy();
  });

  it("refuses a pack size below one under the field", async () => {
    // A pack of zero is a typo, not "no pack size" — caught here so it is a message under the
    // control rather than a round trip that comes back refused.
    render(<ProductBrowser />);
    await screen.findAllByRole("listitem");

    await userEvent.click(screen.getByRole("button", { name: /new product/i }));
    await userEvent.type(screen.getByLabelText(/^SKU/), "VRD-BAD-001");
    await userEvent.type(screen.getByLabelText(/^Name/), "Pack of nothing");
    await userEvent.type(screen.getByLabelText(/^Pack size/), "0");
    await userEvent.click(screen.getByRole("button", { name: /^save$/i }));

    await waitFor(() => expect(createProduct).not.toHaveBeenCalled());
  });
});
