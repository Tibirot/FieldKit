"use client";

import { useQuery } from "@tanstack/react-query";
import { Plus } from "lucide-react";
import { useTranslations } from "next-intl";
import { useMemo, useState } from "react";

import { useAuth } from "@/components/auth-provider";
import { ProductForm } from "@/components/back-office/product-form";
import { Button } from "@/components/ui/button";
import { ApiError } from "@/lib/api/client";
import {
  brandsKey,
  categoriesKey,
  fetchBrands,
  fetchCategories,
  fetchProducts,
  productsKey,
  withAncestry,
  type Product,
} from "@/lib/api/products";
import { usePermissions } from "@/lib/auth/use-permissions";

/**
 * The catalogue a tenant sells (`PRD-01`).
 *
 * **Classification is optional here, and that is why this screen ships before the vocabularies do.**
 * The channel browser exists because the Phase 1 demo tried to create an outlet in a tenant with no
 * channels and found the dropdown empty with no way to fill it — but a channel is mandatory
 * (`BR-OUT-1`), so an empty list was a dead end. A product's brand, category and tax class are all
 * optional, so an unauthored vocabulary is a narrower catalogue rather than a blocked one. The
 * selects say so in words instead of rendering a silent blank, and managing the vocabularies is the
 * next slice.
 *
 * **Search is client-side, and only honest because the list is too.** `/api/products` is unpaged, so
 * the whole catalogue is already here; filtering it in the browser adds no lie about what has been
 * fetched. When the API grows paging this moves server-side with it — a client-side filter over a
 * page would quietly search one page and look like it searched everything.
 */
export function ProductBrowser() {
  const t = useTranslations("Products");
  const { user } = useAuth();
  const { has } = usePermissions();

  const accessToken = user?.access_token;
  const subject = user?.profile.sub;
  const enabled = Boolean(accessToken && subject);

  const products = useQuery({
    enabled,
    queryKey: productsKey(subject ?? ""),
    queryFn: ({ signal }) => fetchProducts(accessToken!, signal),
  });

  // Fetched for the row labels rather than the form's dropdowns — a row showing a brand id would be
  // a row nobody can read. Cached under the same keys the form uses, so opening it costs nothing.
  const brands = useQuery({
    enabled,
    queryKey: brandsKey(subject ?? ""),
    queryFn: ({ signal }) => fetchBrands(accessToken!, signal),
  });

  const categories = useQuery({
    enabled,
    queryKey: categoriesKey(subject ?? ""),
    queryFn: ({ signal }) => fetchCategories(accessToken!, signal),
  });

  const [editing, setEditing] = useState<Product | "new" | null>(null);
  const [search, setSearch] = useState("");

  const brandName = useMemo(
    () => new Map((brands.data ?? []).map((brand) => [brand.id, brand.name])),
    [brands.data],
  );

  const categoryPath = useMemo(
    () => new Map(withAncestry(categories.data ?? []).map((c) => [c.id, c.path])),
    [categories.data],
  );

  const rows = useMemo(() => {
    const needle = search.trim().toLowerCase();
    const all = products.data ?? [];

    if (needle === "") return all;

    // SKU and name, which are the two things someone has in hand when they come looking — a code
    // read off a case, or a name a rep used on the phone.
    return all.filter(
      (product) =>
        product.sku.toLowerCase().includes(needle) || product.name.toLowerCase().includes(needle),
    );
  }, [products.data, search]);

  const canWrite = has("product:write");

  return (
    <div className="flex flex-col gap-4">
      {/* Hidden once the list has failed, not merely disabled. A search box above "you do not have
          permission to view products" filters a list that is not there — a dead control that
          explains nothing, which is the pattern the navigation rejects for the same reason. Kept
          while the query is pending so the toolbar does not jump into place on arrival. */}
      {products.isError ? null : (
        <div className="flex flex-wrap items-center gap-3">
          <input
            type="search"
            value={search}
            onChange={(event) => setSearch(event.target.value)}
            placeholder={t("searchPlaceholder")}
            aria-label={t("search")}
            className="h-9 w-full max-w-xs rounded-lg border border-input bg-background px-3 text-sm focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
          />

          {canWrite ? (
            <Button type="button" size="sm" className="ml-auto" onClick={() => setEditing("new")}>
              <Plus className="size-4" />
              {t("newProduct")}
            </Button>
          ) : null}
        </div>
      )}

      {editing !== null ? (
        <ProductForm
          // Remounted per target: react-hook-form captures its defaults on the first render.
          key={editing === "new" ? "new" : editing.id}
          product={editing === "new" ? undefined : editing}
          onDone={() => setEditing(null)}
          onCancel={() => setEditing(null)}
        />
      ) : null}

      {products.isPending ? (
        <p className="text-sm text-muted-foreground">{t("loading")}</p>
      ) : products.isError ? (
        <p role="alert" className="text-sm text-destructive">
          {products.error instanceof ApiError && products.error.status === 403
            ? t("forbidden")
            : t("failed")}
        </p>
      ) : rows.length === 0 ? (
        // Two different nothings: a catalogue nobody has filled in yet, and a search that matched
        // none of it. Telling them apart is the difference between "add your first product" and
        // "try a different word".
        <p className="text-sm text-muted-foreground">
          {(products.data ?? []).length === 0 ? t("empty") : t("noMatches", { search })}
        </p>
      ) : (
        <ul className="flex flex-col divide-y divide-border rounded-xl border border-border">
          {rows.map((product) => (
            <li key={product.id} className="flex flex-wrap items-center gap-x-3 gap-y-1 px-4 py-2.5 text-sm">
              <span className="font-mono text-xs text-muted-foreground">{product.sku}</span>
              <span className="font-medium">{product.name}</span>

              {product.status === "Discontinued" ? (
                // Said on the row rather than filtered out: a discontinued line is still in the
                // catalogue, still on old orders, and hiding it would make it look deleted.
                <span className="rounded-full bg-muted px-2 py-0.5 text-[11px] text-muted-foreground">
                  {t("statusDiscontinued")}
                </span>
              ) : null}

              <span className="text-xs text-muted-foreground">
                {[
                  product.brandId ? brandName.get(product.brandId) : null,
                  product.categoryId ? categoryPath.get(product.categoryId) : null,
                  product.packSize && product.unitOfMeasure
                    ? `${product.unitOfMeasure} × ${product.packSize}`
                    : product.unitOfMeasure,
                ]
                  .filter(Boolean)
                  .join(" · ")}
              </span>

              {canWrite ? (
                <div className="ml-auto">
                  <Button
                    type="button"
                    size="sm"
                    variant="outline"
                    onClick={() => setEditing(product)}
                    aria-label={t("editNamed", { name: product.name })}
                  >
                    {t("edit")}
                  </Button>
                </div>
              ) : null}
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}
