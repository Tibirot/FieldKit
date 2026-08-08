import { apiDelete, apiGet, apiSend } from "@/lib/api/client";

/**
 * Whether a product can still be ordered (`PRD-01`).
 *
 * A string on the wire, not an ordinal — the API serializes it by name so a client never keeps a
 * private table of numbers that silently changes meaning when a member is inserted.
 */
export type ProductStatus = "Active" | "Discontinued";

/** A product as the catalogue lists it (`PRD-01`). */
export type Product = {
  id: string;
  sku: string;
  name: string;
  brandId: string | null;
  categoryId: string | null;
  taxClassId: string | null;
  unitOfMeasure: string | null;
  packSize: number | null;
  status: ProductStatus;
  customFields: Record<string, unknown>;
};

/**
 * What a product is created or renamed with.
 *
 * **No `sku` on update, deliberately** — the API does not accept one, because changing a SKU is a
 * different product rather than a rename, and every order line already pointing at this id would
 * then describe something else.
 */
export type ProductWrite = {
  name: string;
  brandId: string | null;
  categoryId: string | null;
  taxClassId: string | null;
  unitOfMeasure: string | null;
  packSize: number | null;
  status: ProductStatus;
  customFields: Record<string, unknown>;
};

export type CreateProduct = ProductWrite & { sku: string };

export function fetchProducts(accessToken: string, signal?: AbortSignal): Promise<Product[]> {
  return apiGet<Product[]>("/api/products", accessToken, signal);
}

/**
 * Cached per signed-in subject, like every other reference list.
 *
 * **Unpaged, matching the API.** A catalogue is bigger than a channel list and this will need paging
 * eventually — but the API does not offer it yet, and inventing a client-side page over a full
 * download would be a control that lies about what it costs. When `/api/products` grows paging this
 * key grows a page argument, and the search below moves server-side with it.
 */
export const productsKey = (subject: string) => ["products", subject] as const;

export function createProduct(accessToken: string, product: CreateProduct): Promise<Product> {
  return apiSend<Product>("POST", "/api/products", accessToken, product);
}

export function updateProduct(
  accessToken: string,
  id: string,
  product: ProductWrite,
): Promise<Product> {
  return apiSend<Product>("PUT", `/api/products/${id}`, accessToken, product);
}

// ── Classification vocabularies ────────────────────────────────────────────────────────────────

/** A named entry in one of the three classification vocabularies (`PRD-01`). */
export type Vocabulary = { id: string; name: string };

/** A category, which unlike a brand or a tax class sits in a tree. */
export type Category = Vocabulary & { parentId: string | null };

export function fetchBrands(accessToken: string, signal?: AbortSignal): Promise<Vocabulary[]> {
  return apiGet<Vocabulary[]>("/api/products/brands", accessToken, signal);
}

export function fetchTaxClasses(accessToken: string, signal?: AbortSignal): Promise<Vocabulary[]> {
  return apiGet<Vocabulary[]>("/api/products/tax-classes", accessToken, signal);
}

export function fetchCategories(accessToken: string, signal?: AbortSignal): Promise<Category[]> {
  return apiGet<Category[]>("/api/products/categories", accessToken, signal);
}

export const brandsKey = (subject: string) => ["brands", subject] as const;
export const taxClassesKey = (subject: string) => ["tax-classes", subject] as const;
export const categoriesKey = (subject: string) => ["categories", subject] as const;

/**
 * The three vocabularies, as the classification screen addresses them.
 *
 * Brands and tax classes are the **same shape** on the wire — `{ id, name }` with the same four
 * verbs — so they are described here rather than given a module each. A category adds a parent and
 * nothing else, which is why it rides along instead of getting its own file: three near-identical
 * clients would drift, and the difference that matters is one nullable field.
 */
export type VocabularyKind = "brands" | "categories" | "tax-classes";

const PATHS: Record<VocabularyKind, string> = {
  brands: "/api/products/brands",
  categories: "/api/products/categories",
  "tax-classes": "/api/products/tax-classes",
};

/** What a vocabulary entry is written with. `parentId` is only ever sent for a category. */
export type VocabularyWrite = { name: string; parentId?: string | null };

export function createVocabulary(
  accessToken: string,
  kind: VocabularyKind,
  body: VocabularyWrite,
): Promise<Vocabulary> {
  return apiSend<Vocabulary>("POST", PATHS[kind], accessToken, body);
}

export function updateVocabulary(
  accessToken: string,
  kind: VocabularyKind,
  id: string,
  body: VocabularyWrite,
): Promise<Vocabulary> {
  return apiSend<Vocabulary>("PUT", `${PATHS[kind]}/${id}`, accessToken, body);
}

/**
 * Removes an entry.
 *
 * Refused with a 409 while anything still points at it — a brand or tax class any product is
 * classified by, a category with children or with products filed under it. The API says how many and
 * what to do about it; this just passes the refusal along, because "12 product(s) are branded
 * 'Veridian'. Reclassify them first." is an instruction and "could not delete" is not.
 */
export function deleteVocabulary(
  accessToken: string,
  kind: VocabularyKind,
  id: string,
): Promise<void> {
  return apiDelete(`${PATHS[kind]}/${id}`, accessToken);
}

/**
 * Category names with their ancestry, so a flat `<select>` can show a tree.
 *
 * "Beverages / Water / Still" rather than "Still", because a tenant's tree routinely has the same
 * leaf name under two parents — every category tree with "Other" in it does — and a dropdown listing
 * both as "Other" asks the author to guess.
 *
 * Stops on a repeat rather than looping. A cycle is unreachable through the API, which refuses a
 * re-parent that would create one, but this runs on data the client did not author and a hang is a
 * far worse failure than a truncated label.
 */
export function withAncestry(categories: readonly Category[]): { id: string; path: string }[] {
  const byId = new Map(categories.map((category) => [category.id, category]));

  const path = (category: Category): string => {
    const parts: string[] = [];
    const seen = new Set<string>();

    for (let current: Category | undefined = category; current && !seen.has(current.id); ) {
      seen.add(current.id);
      parts.unshift(current.name);
      current = current.parentId ? byId.get(current.parentId) : undefined;
    }

    return parts.join(" / ");
  };

  return categories
    .map((category) => ({ id: category.id, path: path(category) }))
    .sort((left, right) => left.path.localeCompare(right.path));
}

// ── Tax rates ──────────────────────────────────────────────────────────────────────────────────

/**
 * One rate of a tax class, in one country, over one half-open window (`PRD-07`).
 *
 * `percentage` is a string for the same reason a price is (`BR-PRD-8`): it is a decimal the device
 * pricing engine will compute with, and a JSON number is an IEEE-754 double the moment a browser
 * parses it.
 */
export type TaxRate = {
  id: string;
  countryCode: string;
  percentage: string;
  effectiveFrom: string;
  effectiveTo: string | null;
};

/** One rate as an author states it — no id, because a PUT replaces the set rather than editing it. */
export type TaxRateWrite = {
  countryCode: string;
  percentage: string;
  effectiveFrom: string;
  effectiveTo: string | null;
};

export function fetchTaxRates(
  accessToken: string,
  taxClassId: string,
  signal?: AbortSignal,
): Promise<TaxRate[]> {
  return apiGet<TaxRate[]>(`/api/products/tax-classes/${taxClassId}/rates`, accessToken, signal);
}

/**
 * Replaces every rate of a tax class.
 *
 * **An empty set is a real state**: the class then has no rate anywhere, which resolves to *unknown*
 * rather than to zero. A zero-rated class is a rate of `0`, authored deliberately — the difference
 * between "we have not said" and "we have said none", which `TaxEngine.Resolve` keeps.
 */
export function setTaxRates(
  accessToken: string,
  taxClassId: string,
  rates: readonly TaxRateWrite[],
): Promise<TaxRate[]> {
  return apiSend<TaxRate[]>(
    "PUT",
    `/api/products/tax-classes/${taxClassId}/rates`,
    accessToken,
    { rates },
  );
}

export const taxRatesKey = (subject: string, taxClassId: string) =>
  ["tax-classes", subject, taxClassId, "rates"] as const;
