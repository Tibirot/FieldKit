// @vitest-environment jsdom

import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";

import type { AuthContextValue } from "@/components/auth-provider";
import { ClassificationBrowser } from "@/components/back-office/classification-browser";
import { ApiError } from "@/lib/api/client";
import { fetchIdentity } from "@/lib/api/identity";
import type { Category, Vocabulary } from "@/lib/api/products";
import { render } from "@/test/render";

const auth = vi.hoisted(() => ({ current: {} as AuthContextValue }));
const fetchBrands = vi.hoisted(() => vi.fn());
const fetchCategories = vi.hoisted(() => vi.fn());
const fetchTaxClasses = vi.hoisted(() => vi.fn());
const createVocabulary = vi.hoisted(() => vi.fn());
const updateVocabulary = vi.hoisted(() => vi.fn());
const deleteVocabulary = vi.hoisted(() => vi.fn());

vi.mock("@/components/auth-provider", () => ({ useAuth: () => auth.current }));

// A tax class links out to its rates, and next-intl's Link reaches for Next's router — which does
// not resolve outside a Next build. Every prop forwarded, so the row's `aria-label` survives.
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

vi.mock("@/lib/api/products", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/lib/api/products")>()),
  fetchBrands: (...args: unknown[]) => fetchBrands(...args),
  fetchCategories: (...args: unknown[]) => fetchCategories(...args),
  fetchTaxClasses: (...args: unknown[]) => fetchTaxClasses(...args),
  createVocabulary: (...args: unknown[]) => createVocabulary(...args),
  updateVocabulary: (...args: unknown[]) => updateVocabulary(...args),
  deleteVocabulary: (...args: unknown[]) => deleteVocabulary(...args),
}));

const BRANDS: Vocabulary[] = [{ id: "b-veridian", name: "Veridian" }];
const TAX_CLASSES: Vocabulary[] = [{ id: "t-reduced", name: "Reduced" }];

/** Two leaves called "Other" under different parents — the case a bare name gets wrong. */
const CATEGORIES: Category[] = [
  { id: "c-bev", name: "Beverages", parentId: null },
  { id: "c-water", name: "Water", parentId: "c-bev" },
  { id: "c-other-bev", name: "Other", parentId: "c-bev" },
  { id: "c-food", name: "Food", parentId: null },
  { id: "c-other-food", name: "Other", parentId: "c-food" },
];

/**
 * The section for one vocabulary, once it has loaded.
 *
 * Async because each vocabulary fetches independently — a synchronous lookup runs before the query
 * resolves and finds nothing, which is exactly how the first draft of this file failed.
 */
async function section(heading: string): Promise<HTMLElement> {
  return (await screen.findByRole("heading", { name: heading })).closest("section")!;
}

describe("<ClassificationBrowser>", () => {
  beforeEach(() => {
    fetchBrands.mockReset().mockResolvedValue(BRANDS);
    fetchCategories.mockReset().mockResolvedValue(CATEGORIES);
    fetchTaxClasses.mockReset().mockResolvedValue(TAX_CLASSES);
    createVocabulary.mockReset().mockResolvedValue(BRANDS[0]);
    updateVocabulary.mockReset().mockResolvedValue(BRANDS[0]);
    deleteVocabulary.mockReset().mockResolvedValue(undefined);

    // Restored per test — one case below narrows the caller, and the mock is module-level, so
    // without this every test after it would run read-only and pass for the wrong reason.
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

  it("shows all three vocabularies on one page", async () => {
    // One job — saying how the catalogue is organised — done in one sitting. Three routes would
    // make that three trips.
    render(<ClassificationBrowser />);

    expect(await screen.findByRole("heading", { name: "Categories" })).toBeTruthy();
    expect(screen.getByRole("heading", { name: "Brands" })).toBeTruthy();
    expect(screen.getByRole("heading", { name: "Tax classes" })).toBeTruthy();
  });

  it("shows a category with its ancestry, so two leaves called Other are distinguishable", async () => {
    render(<ClassificationBrowser />);

    const categories = within(await section("Categories"));

    expect(categories.getByText("Beverages / Other")).toBeTruthy();
    expect(categories.getByText("Food / Other")).toBeTruthy();
  });

  it("offers a parent select for categories and none for the flat vocabularies", async () => {
    // The one difference between the three, and the reason a single component serves all of them:
    // it is one nullable field, not a different screen.
    render(<ClassificationBrowser />);

    await screen.findByRole("heading", { name: "Categories" });

    await userEvent.click(
      within(await section("Categories")).getByRole("button", { name: "New category" }),
    );
    expect(screen.getByLabelText("Parent")).toBeTruthy();

    await userEvent.click(within(await section("Brands")).getByRole("button", { name: "New brand" }));
    expect(within(await section("Brands")).queryByLabelText("Parent")).toBeNull();
  });

  it("creates a top-level category, sending a null parent rather than an empty string", async () => {
    render(<ClassificationBrowser />);
    await screen.findByRole("heading", { name: "Categories" });

    const categories = within(await section("Categories"));
    await userEvent.click(categories.getByRole("button", { name: "New category" }));

    await userEvent.type(screen.getByLabelText("Name"), "Snacks");
    await userEvent.click(categories.getByRole("button", { name: "Save" }));

    await waitFor(() => expect(createVocabulary).toHaveBeenCalled());

    expect(createVocabulary.mock.calls[0][1]).toBe("categories");
    expect(createVocabulary.mock.calls[0][2]).toEqual({ name: "Snacks", parentId: null });
  });

  it("creates a brand without a parent field at all", async () => {
    // A flat vocabulary must not send parentId — the API has no such parameter, and sending null
    // would be this component leaking a shape the brand endpoint never asked for.
    render(<ClassificationBrowser />);
    await screen.findByRole("heading", { name: "Brands" });

    await userEvent.click(within(await section("Brands")).getByRole("button", { name: "New brand" }));
    await userEvent.type(screen.getByLabelText("Name"), "Aqua");
    await userEvent.click(within(await section("Brands")).getByRole("button", { name: "Save" }));

    await waitFor(() => expect(createVocabulary).toHaveBeenCalled());

    expect(createVocabulary.mock.calls[0][1]).toBe("brands");
    expect(createVocabulary.mock.calls[0][2]).toEqual({ name: "Aqua" });
  });

  it("re-parents a category", async () => {
    render(<ClassificationBrowser />);
    await screen.findByRole("heading", { name: "Categories" });

    const categories = within(await section("Categories"));
    await userEvent.click(categories.getByRole("button", { name: "Rename Water" }));

    await userEvent.selectOptions(screen.getByLabelText("Parent"), "c-food");
    await userEvent.click(categories.getByRole("button", { name: "Save" }));

    await waitFor(() => expect(updateVocabulary).toHaveBeenCalled());

    expect(updateVocabulary.mock.calls[0][2]).toBe("c-water");
    expect(updateVocabulary.mock.calls[0][3]).toEqual({ name: "Water", parentId: "c-food" });
  });

  it("offers a category every parent but itself, descendants included", async () => {
    // Its own subtree is deliberately still offered. The API refuses a move into it with a reason
    // (product.category.cycle), and omitting the option would leave someone hunting for a category
    // that is visibly in the list — a refusal that explains beats a control that quietly hides.
    render(<ClassificationBrowser />);
    await screen.findByRole("heading", { name: "Categories" });

    await userEvent.click(
      within(await section("Categories")).getByRole("button", { name: "Rename Beverages" }),
    );

    const options = Array.from(
      screen.getByLabelText("Parent").querySelectorAll("option"),
    ).map((option) => option.textContent);

    expect(options).not.toContain("Beverages");
    expect(options).toContain("Beverages / Water");
    expect(options).toContain("Top level");
  });

  it("passes on the API's refusal, which names the count and the next step", async () => {
    deleteVocabulary.mockRejectedValue(
      new ApiError(409, [
        { field: null, message: "12 product(s) are branded 'Veridian'. Reclassify them first." },
      ]),
    );

    render(<ClassificationBrowser />);
    await screen.findByRole("heading", { name: "Brands" });

    await userEvent.click(within(await section("Brands")).getByRole("button", { name: "Delete Veridian" }));

    expect((await screen.findByRole("alert")).textContent).toContain("12 product(s)");
  });

  it("keeps the other sections usable when one cannot load", async () => {
    // Loaded independently rather than behind one combined query: a failure in one is not a reason
    // to take the other two away.
    fetchCategories.mockRejectedValue(new ApiError(500));

    render(<ClassificationBrowser />);

    expect(await screen.findByRole("heading", { name: "Brands" })).toBeTruthy();
    expect(screen.getByText("Veridian")).toBeTruthy();
    expect(screen.queryByRole("heading", { name: "Categories" })).toBeNull();
  });

  it("offers no way to change anything to a caller who may only read", async () => {
    vi.mocked(fetchIdentity).mockResolvedValue({
      subject: "subject-a",
      tenant: "fieldkit-dev",
      permissions: ["product:read"],
    });

    render(<ClassificationBrowser />);
    await screen.findByRole("heading", { name: "Brands" });

    expect(screen.queryByRole("button", { name: "New brand" })).toBeNull();
    expect(screen.queryByRole("button", { name: "Rename Veridian" })).toBeNull();
    expect(screen.queryByRole("button", { name: "Delete Veridian" })).toBeNull();

    // Still readable, which is the point of the split: a product row shows a brand name, and
    // somebody has to be able to see where it came from.
    expect(screen.getByText("Veridian")).toBeTruthy();
  });
});
