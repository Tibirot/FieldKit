"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { X } from "lucide-react";
import { useTranslations } from "next-intl";
import { useParams } from "next/navigation";
import { useMemo, useState } from "react";

import { useAuth } from "@/components/auth-provider";
import { Button } from "@/components/ui/button";
import { ApiError } from "@/lib/api/client";
import {
  categoriesKey,
  fetchCategories,
  fetchProducts,
  productsKey,
  withAncestry,
  type Category,
  type Product,
} from "@/lib/api/products";
import {
  fetchPromotions,
  fetchTargets,
  promotionsKey,
  setTargets,
  targetsKey,
  type Promotion,
  type PromotionTarget,
} from "@/lib/api/promotions";
import { usePermissions } from "@/lib/auth/use-permissions";

const CONTROL =
  "h-9 w-full rounded-lg border border-input bg-background px-3 text-sm text-foreground"
  + " focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none";

/**
 * What a promotion discounts (`PRD-05`).
 *
 * **A promotion with no targets discounts nothing.** Emptying this set is how one is taken out of
 * play without editing its window or deleting a record other things point at — the same meaning an
 * emptied price-list scope has, and the reason neither is refused.
 *
 * **A category target reaches everything filed below it.** Resolution walks a product's category and
 * every category above it (`PRD-06`), so targeting "Beverages" catches a product filed under
 * "Beverages / Water / Still" without naming either. That is the whole point of category targets and
 * it is invisible from a list of names, so the screen says it.
 *
 * **Where it applies is not here.** Channels and outlets are `PromotionAssignment`, its own
 * aggregate and its own slice — exactly as a price list's prices and its scope are separate.
 */
export function PromotionTargets() {
  const t = useTranslations("PromotionTargets");
  const { user } = useAuth();
  const params = useParams<{ id: string }>();

  const accessToken = user?.access_token;
  const subject = user?.profile.sub;
  const id = params.id;
  const enabled = Boolean(accessToken && subject && id);

  const promotions = useQuery({
    enabled,
    queryKey: promotionsKey(subject ?? ""),
    queryFn: ({ signal }) => fetchPromotions(accessToken!, signal),
  });

  const products = useQuery({
    enabled,
    queryKey: productsKey(subject ?? ""),
    queryFn: ({ signal }) => fetchProducts(accessToken!, signal),
  });

  const categories = useQuery({
    enabled,
    queryKey: categoriesKey(subject ?? ""),
    queryFn: ({ signal }) => fetchCategories(accessToken!, signal),
  });

  const targets = useQuery({
    enabled,
    queryKey: targetsKey(subject ?? "", id ?? ""),
    queryFn: ({ signal }) => fetchTargets(accessToken!, id, signal),
  });

  const failed = [promotions, products, categories, targets].find((query) => query.isError);

  if (failed) {
    const error = failed.error;

    // The targets read 404s on a promotion this tenant does not have, which is also what another
    // tenant's id looks like from here.
    if (error instanceof ApiError && error.status === 404) {
      return (
        <p role="alert" className="text-sm text-destructive">
          {t("notFound")}
        </p>
      );
    }

    return (
      <p role="alert" className="text-sm text-destructive">
        {error instanceof ApiError && error.status === 403 ? t("forbidden") : t("failed")}
      </p>
    );
  }

  if (!promotions.data || !products.data || !categories.data || !targets.data) {
    return <p className="text-sm text-muted-foreground">{t("loading")}</p>;
  }

  const promotion = promotions.data.find((candidate) => candidate.id === id);

  if (!promotion) {
    return (
      <p role="alert" className="text-sm text-destructive">
        {t("notFound")}
      </p>
    );
  }

  return (
    <div className="flex max-w-3xl flex-col gap-4">
      <header>
        <p className="font-mono text-[11.5px] text-muted-foreground">{t("crumb")}</p>
        <h1 className="text-lg font-semibold tracking-tight">{promotion.name}</h1>
        <p className="mt-1 text-sm text-muted-foreground">{t("intro")}</p>
      </header>

      <TargetEditor
        // Remounted per promotion, so the boxes reseed from what the server holds.
        key={promotion.id}
        promotion={promotion}
        products={products.data}
        categories={categories.data}
        targets={targets.data}
      />
    </div>
  );
}

/** The editable half, seeded once from what the server holds. */
function TargetEditor({
  promotion,
  products,
  categories,
  targets,
}: {
  promotion: Promotion;
  products: readonly Product[];
  categories: readonly Category[];
  targets: readonly PromotionTarget[];
}) {
  const t = useTranslations("PromotionTargets");
  const client = useQueryClient();
  const { user } = useAuth();
  const { has } = usePermissions();

  const accessToken = user?.access_token;

  const storedCategories = useMemo(
    () =>
      new Set(
        targets
          .map((target) => target.categoryId)
          .filter((categoryId): categoryId is string => categoryId !== null),
      ),
    [targets],
  );

  const storedProducts = useMemo(
    () =>
      targets
        .map((target) => target.productId)
        .filter((productId): productId is string => productId !== null),
    [targets],
  );

  const [chosenCategories, setChosenCategories] = useState<Set<string>>(
    () => new Set(storedCategories),
  );
  const [chosenProducts, setChosenProducts] = useState<string[]>(() => [...storedProducts]);
  const [search, setSearch] = useState("");
  const [refused, setRefused] = useState<readonly string[]>([]);

  const save = useMutation({
    mutationFn: () =>
      setTargets(accessToken!, promotion.id, {
        productIds: chosenProducts,
        categoryIds: [...chosenCategories],
      }),

    onSuccess: async () => {
      setRefused([]);
      await client.invalidateQueries({ queryKey: ["promotions"] });
    },

    onError: (error) =>
      setRefused(
        error instanceof ApiError && error.problems.length > 0
          ? error.problems.map((problem) => problem.message)
          : [t("saveFailed")],
      ),
  });

  /**
   * Every category with its ancestry, alphabetically by path.
   *
   * "Beverages / Water / Still" rather than "Still": a tenant's tree routinely has the same leaf
   * name under two parents, and a list showing both as "Still" asks the author to guess which one
   * they are about to discount. Sorting by path also puts a subtree together, which a flat
   * name-sorted list would scatter.
   */
  const rows = useMemo(
    () => withAncestry(categories).sort((a, b) => a.path.localeCompare(b.path)),
    [categories],
  );

  const byId = useMemo(() => new Map(products.map((product) => [product.id, product])), [products]);

  /**
   * Searched in memory, unlike the outlet picker on a price list's scope.
   *
   * The catalogue endpoint returns the whole tenant's products in one response, so filtering here
   * searches everything the server has rather than whichever page happened to arrive — the exact
   * argument that made the outlet search go to the server, reaching the opposite conclusion because
   * the endpoint is paged and this one is not.
   */
  const found = useMemo(() => {
    const needle = search.trim().toLowerCase();

    if (needle === "") return [];

    return products
      .filter(
        (product) =>
          product.sku.toLowerCase().includes(needle)
          || product.name.toLowerCase().includes(needle),
      )
      .slice(0, 10);
  }, [products, search]);

  const dirty = useMemo(() => {
    if (chosenCategories.size !== storedCategories.size) return true;
    if (chosenProducts.length !== storedProducts.length) return true;

    for (const categoryId of chosenCategories) if (!storedCategories.has(categoryId)) return true;

    const before = new Set(storedProducts);

    return chosenProducts.some((productId) => !before.has(productId));
  }, [chosenCategories, storedCategories, chosenProducts, storedProducts]);

  const canWrite = has("product:write");
  const reach = chosenCategories.size + chosenProducts.length;

  return (
    <div className="flex flex-col gap-6">
      <p className="text-sm text-muted-foreground" role="status">
        {reach === 0 ? t("discountsNothing") : t("discounts", { count: reach })}
      </p>

      {refused.length > 0 ? (
        <ul role="alert" className="rounded-xl bg-destructive/10 px-4 py-3 text-sm text-destructive">
          {refused.map((problem) => (
            <li key={problem}>{problem}</li>
          ))}
        </ul>
      ) : null}

      <section className="flex flex-col gap-2">
        <h2 className="text-sm font-semibold">{t("categories")}</h2>
        {/* The inheritance, said out loud. It is the reason to use a category target at all and it
            cannot be seen in a list of names. */}
        <p className="text-xs text-muted-foreground">{t("categoriesHint")}</p>

        {rows.length === 0 ? (
          <p className="text-sm text-muted-foreground">{t("noCategories")}</p>
        ) : (
          <ul className="flex flex-col divide-y divide-border rounded-xl border border-border">
            {rows.map((row) => (
              <li key={row.id} className="px-4 py-2 text-sm">
                <label className="flex items-center gap-2">
                  <input
                    type="checkbox"
                    className="size-4"
                    disabled={!canWrite}
                    checked={chosenCategories.has(row.id)}
                    onChange={(event) =>
                      setChosenCategories((current) => {
                        const next = new Set(current);
                        if (event.target.checked) next.add(row.id);
                        else next.delete(row.id);
                        return next;
                      })
                    }
                    aria-label={t("categoryNamed", { path: row.path })}
                  />
                  {row.path}
                </label>
              </li>
            ))}
          </ul>
        )}
      </section>

      <section className="flex flex-col gap-2">
        <h2 className="text-sm font-semibold">{t("products")}</h2>
        <p className="text-xs text-muted-foreground">{t("productsHint")}</p>

        {chosenProducts.length > 0 ? (
          <ul className="flex flex-wrap gap-2">
            {chosenProducts.map((productId) => {
              const product = byId.get(productId);

              return (
                <li
                  key={productId}
                  className="flex items-center gap-2 rounded-full border border-border px-3 py-1 text-xs"
                >
                  {/* A product the catalogue read did not contain is still targeted server-side.
                      Showing it by id keeps it in the next PUT rather than unnaming and dropping
                      it — the same reason an unloadable outlet stays on a price list's scope. */}
                  <span className="font-mono text-muted-foreground">
                    {product ? product.sku : productId}
                  </span>
                  <span>{product ? product.name : t("unknownProduct")}</span>

                  {canWrite ? (
                    <button
                      type="button"
                      aria-label={t("removeNamed", {
                        name: product ? product.name : t("unknownProduct"),
                        sku: product ? product.sku : productId,
                      })}
                      onClick={() =>
                        setChosenProducts((current) =>
                          current.filter((chosen) => chosen !== productId),
                        )
                      }
                      className="text-muted-foreground hover:text-foreground"
                    >
                      <X className="size-3.5" />
                    </button>
                  ) : null}
                </li>
              );
            })}
          </ul>
        ) : (
          <p className="text-sm text-muted-foreground">{t("noProducts")}</p>
        )}

        {canWrite ? (
          <div className="flex flex-col gap-2">
            <input
              type="search"
              value={search}
              onChange={(event) => setSearch(event.target.value)}
              placeholder={t("searchPlaceholder")}
              aria-label={t("searchProducts")}
              className={`${CONTROL} max-w-sm`}
            />

            {search.trim() !== "" ? (
              found.length === 0 ? (
                <p className="text-sm text-muted-foreground">{t("noMatches", { search })}</p>
              ) : (
                <ul className="flex max-w-sm flex-col divide-y divide-border rounded-xl border border-border">
                  {found.map((product) => {
                    const already = chosenProducts.includes(product.id);

                    return (
                      <li key={product.id} className="flex items-center gap-2 px-3 py-1.5 text-sm">
                        <span className="font-mono text-xs text-muted-foreground">
                          {product.sku}
                        </span>
                        <span className="truncate">{product.name}</span>

                        <Button
                          type="button"
                          size="sm"
                          variant="outline"
                          className="ml-auto"
                          // Already chosen, so adding it again would silently do nothing. Disabled
                          // rather than hidden, so a search result stays a stable list.
                          disabled={already}
                          onClick={() =>
                            setChosenProducts((current) => [...current, product.id])
                          }
                          aria-label={t("addNamed", { name: product.name, sku: product.sku })}
                        >
                          {already ? t("added") : t("add")}
                        </Button>
                      </li>
                    );
                  })}
                </ul>
              )
            ) : null}
          </div>
        ) : null}
      </section>

      {canWrite ? (
        <div className="flex items-center gap-3">
          <Button
            type="button"
            size="sm"
            disabled={!dirty || save.isPending}
            onClick={() => save.mutate()}
          >
            {save.isPending ? t("saving") : t("save")}
          </Button>

          {dirty ? <span className="text-xs text-muted-foreground">{t("unsaved")}</span> : null}
        </div>
      ) : null}
    </div>
  );
}
