// @vitest-environment jsdom

import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";

import type { AuthContextValue } from "@/components/auth-provider";
import { PromotionTargets } from "@/components/back-office/promotion-targets";
import { ApiError } from "@/lib/api/client";
import { fetchIdentity } from "@/lib/api/identity";
import type { Category, Product } from "@/lib/api/products";
import type { Promotion, PromotionTarget } from "@/lib/api/promotions";
import { render } from "@/test/render";

const auth = vi.hoisted(() => ({ current: {} as AuthContextValue }));
const fetchPromotions = vi.hoisted(() => vi.fn());
const fetchTargets = vi.hoisted(() => vi.fn());
const setTargets = vi.hoisted(() => vi.fn());
const fetchProducts = vi.hoisted(() => vi.fn());
const fetchCategories = vi.hoisted(() => vi.fn());

vi.mock("@/components/auth-provider", () => ({ useAuth: () => auth.current }));
vi.mock("next/navigation", () => ({ useParams: () => ({ id: "promo-1" }) }));

vi.mock("@/lib/api/promotions", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/lib/api/promotions")>()),
  fetchPromotions: (...args: unknown[]) => fetchPromotions(...args),
  fetchTargets: (...args: unknown[]) => fetchTargets(...args),
  setTargets: (...args: unknown[]) => setTargets(...args),
}));

vi.mock("@/lib/api/products", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/lib/api/products")>()),
  fetchProducts: (...args: unknown[]) => fetchProducts(...args),
  fetchCategories: (...args: unknown[]) => fetchCategories(...args),
}));

/** Waits for the permission answer — see the note in outlet-assortment.test.tsx. */
async function ready(): Promise<void> {
  await screen.findByRole("button", { name: "Save targets" });
}

const PROMOTION: Promotion = {
  id: "promo-1",
  name: "Summer refresh",
  type: "PercentOff",
  value: "10.00",
  currency: null,
  validFrom: "2026-06-01",
  validTo: null,
  priority: 10,
  bundle: null,
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

const STILL = product("p-still", "VRD-STL-100", "Veridian Still 1L");
const SPARKLING = product("p-spark", "VRD-SPK-050", "Veridian Sparkling 0.5L");

/**
 * Two leaves called "Other" under different parents — the case that makes the ancestry path
 * necessary rather than decorative.
 */
const CATEGORIES: Category[] = [
  { id: "c-bev", name: "Beverages", parentId: null },
  { id: "c-water", name: "Water", parentId: "c-bev" },
  { id: "c-water-other", name: "Other", parentId: "c-water" },
  { id: "c-snacks", name: "Snacks", parentId: null },
  { id: "c-snacks-other", name: "Other", parentId: "c-snacks" },
];

const TARGETS: PromotionTarget[] = [
  { productId: null, categoryId: "c-water" },
  { productId: "p-still", categoryId: null },
];

describe("<PromotionTargets>", () => {
  beforeEach(() => {
    fetchPromotions.mockReset().mockResolvedValue([PROMOTION]);
    fetchTargets.mockReset().mockResolvedValue(TARGETS);
    setTargets.mockReset().mockResolvedValue(TARGETS);
    fetchProducts.mockReset().mockResolvedValue([STILL, SPARKLING]);
    fetchCategories.mockReset().mockResolvedValue(CATEGORIES);

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

  it("shows a category with its ancestry, so two leaves named alike are told apart", async () => {
    // A tenant's tree routinely has the same leaf name under two parents — every tree with "Other"
    // in it does — and a list showing both as "Other" asks the author to guess which one they are
    // about to discount.
    render(<PromotionTargets />);
    await ready();

    expect(screen.getByLabelText("Target Beverages / Water / Other")).toBeTruthy();
    expect(screen.getByLabelText("Target Snacks / Other")).toBeTruthy();
  });

  it("says a category covers everything filed below it", async () => {
    // The reason to use a category target at all, and invisible from a list of names: resolution
    // walks a product's category and every category above it.
    render(<PromotionTargets />);
    await ready();

    expect(screen.getByText(/also covers everything filed below it/i)).toBeTruthy();
  });

  it("shows what a promotion already targets", async () => {
    render(<PromotionTargets />);
    await ready();

    expect(
      (screen.getByLabelText("Target Beverages / Water") as HTMLInputElement).checked,
    ).toBe(true);
    expect((screen.getByLabelText("Target Snacks") as HTMLInputElement).checked).toBe(false);
    expect(screen.getByText("Veridian Still 1L")).toBeTruthy();
  });

  it("sends both kinds of target together, because the PUT replaces the whole set", async () => {
    render(<PromotionTargets />);
    await ready();

    await userEvent.click(screen.getByLabelText("Target Snacks"));
    await userEvent.click(screen.getByRole("button", { name: "Save targets" }));

    await waitFor(() => expect(setTargets).toHaveBeenCalled());

    const sent = setTargets.mock.calls[0][2];

    expect(sent.categoryIds).toEqual(expect.arrayContaining(["c-water", "c-snacks"]));
    expect(sent.categoryIds).toHaveLength(2);
    expect(sent.productIds).toEqual(["p-still"]);
  });

  it("lets a promotion be taken out of play by emptying its targets", async () => {
    // An empty set is a real decision, not a mistake to guard against: the promotion then discounts
    // nothing, without its window being edited or a record other things point at being deleted.
    render(<PromotionTargets />);
    await ready();

    await userEvent.click(screen.getByLabelText("Target Beverages / Water"));
    await userEvent.click(
      screen.getByRole("button", { name: "Remove Veridian Still 1L (VRD-STL-100)" }),
    );

    expect(screen.getByRole("status").textContent).toMatch(/discounts nothing/i);

    await userEvent.click(screen.getByRole("button", { name: "Save targets" }));

    await waitFor(() => expect(setTargets).toHaveBeenCalled());
    expect(setTargets.mock.calls[0][2]).toEqual({ productIds: [], categoryIds: [] });
  });

  it("searches the catalogue it already holds rather than asking again", async () => {
    // The products endpoint is unpaged, so filtering in memory searches everything the server has —
    // the opposite conclusion to the outlet picker on a price list's scope, reached from the same
    // argument about not searching one page while looking like you searched the base.
    render(<PromotionTargets />);
    await ready();

    expect(fetchProducts).toHaveBeenCalledTimes(1);

    await userEvent.type(screen.getByLabelText("Search products"), "spark");

    expect(await screen.findByRole("button", { name: "Add Veridian Sparkling 0.5L (VRD-SPK-050)" }))
      .toBeTruthy();
    expect(fetchProducts).toHaveBeenCalledTimes(1);
  });

  it("adds a searched product to the target set", async () => {
    render(<PromotionTargets />);
    await ready();

    await userEvent.type(screen.getByLabelText("Search products"), "spark");
    await userEvent.click(
      await screen.findByRole("button", { name: "Add Veridian Sparkling 0.5L (VRD-SPK-050)" }),
    );
    await userEvent.click(screen.getByRole("button", { name: "Save targets" }));

    await waitFor(() => expect(setTargets).toHaveBeenCalled());
    expect(setTargets.mock.calls[0][2].productIds).toEqual(["p-still", "p-spark"]);
  });

  it("cannot add the same product twice", async () => {
    render(<PromotionTargets />);
    await ready();

    await userEvent.type(screen.getByLabelText("Search products"), "still");

    const add = await screen.findByRole("button", {
      name: "Add Veridian Still 1L (VRD-STL-100)",
    });

    expect((add as HTMLButtonElement).disabled).toBe(true);
  });

  it("keeps a product the catalogue did not contain, rather than dropping it on save", async () => {
    // The row exists server-side regardless of whether this screen could name it. Hiding it would
    // remove it from the next PUT — a silent untargeting caused by a product that was filtered,
    // renamed away or simply not in the response.
    fetchTargets.mockResolvedValue([{ productId: "p-gone", categoryId: null }]);

    render(<PromotionTargets />);
    await ready();

    expect(screen.getByText(/could not be loaded/i)).toBeTruthy();

    await userEvent.click(screen.getByLabelText("Target Snacks"));
    await userEvent.click(screen.getByRole("button", { name: "Save targets" }));

    await waitFor(() => expect(setTargets).toHaveBeenCalled());
    expect(setTargets.mock.calls[0][2].productIds).toEqual(["p-gone"]);
  });

  it("counts removing a product as a change, not only adding one", async () => {
    // A removal leaves nothing new behind to compare against, so a dirty check that only looks for
    // unfamiliar entries misses it — and untargeting a line would silently not save.
    render(<PromotionTargets />);

    const save = await screen.findByRole("button", { name: "Save targets" });
    expect((save as HTMLButtonElement).disabled).toBe(true);

    await userEvent.click(
      screen.getByRole("button", { name: "Remove Veridian Still 1L (VRD-STL-100)" }),
    );
    expect((save as HTMLButtonElement).disabled).toBe(false);
  });

  it("counts everything the promotion discounts, categories and products alike", async () => {
    render(<PromotionTargets />);
    await ready();

    expect(screen.getByRole("status").textContent).toContain("2 things");
  });

  it("says a promotion that does not exist is missing rather than broken", async () => {
    fetchPromotions.mockResolvedValue([]);

    render(<PromotionTargets />);

    expect(await screen.findByText(/does not exist/i)).toBeTruthy();
  });

  it("says which permission is missing rather than that something failed", async () => {
    fetchTargets.mockRejectedValue(new ApiError(403));

    render(<PromotionTargets />);

    expect(await screen.findByText(/do not have permission/i)).toBeTruthy();
  });

  it("offers no way to change anything to a caller who may only read", async () => {
    vi.mocked(fetchIdentity).mockResolvedValue({
      subject: "subject-a",
      tenant: "fieldkit-dev",
      permissions: ["product:read"],
    });

    render(<PromotionTargets />);

    const box = await screen.findByLabelText("Target Beverages / Water");

    await waitFor(() => expect((box as HTMLInputElement).disabled).toBe(true));
    expect(screen.queryByRole("button", { name: "Save targets" })).toBeNull();
    expect(screen.queryByLabelText("Search products")).toBeNull();

    // Still readable: what a deal discounts is the question a reader came to answer.
    expect((box as HTMLInputElement).checked).toBe(true);
    expect(screen.getByText("Veridian Still 1L")).toBeTruthy();
  });

  it("passes on a refusal in the API's own words", async () => {
    setTargets.mockRejectedValue(
      new ApiError(400, [{ field: "categoryIds", message: "That category does not exist." }]),
    );

    render(<PromotionTargets />);
    await ready();

    await userEvent.click(screen.getByLabelText("Target Snacks"));
    await userEvent.click(screen.getByRole("button", { name: "Save targets" }));

    expect((await screen.findByRole("alert")).textContent).toContain("does not exist");
  });

  it("says a tenant with no categories has none, rather than showing an empty box", async () => {
    fetchCategories.mockResolvedValue([]);
    fetchTargets.mockResolvedValue([]);

    render(<PromotionTargets />);
    await ready();

    const categories = screen.getByRole("heading", { name: "Categories" }).parentElement!;

    expect(within(categories).getByText(/No categories yet/i)).toBeTruthy();
  });
});
