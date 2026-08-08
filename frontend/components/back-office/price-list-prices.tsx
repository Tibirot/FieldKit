"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslations } from "next-intl";
import { useParams } from "next/navigation";
import { useMemo, useState } from "react";

import { useAuth } from "@/components/auth-provider";
import { Button } from "@/components/ui/button";
import { ApiError } from "@/lib/api/client";
import { refusalTexts } from "@/lib/api/refusals";
import {
  fetchPriceLists,
  fetchPrices,
  looksLikeAnAmount,
  priceListsKey,
  pricesKey,
  setPrices,
  type PriceLine,
  type PriceList,
} from "@/lib/api/price-lists";
import { fetchProducts, productsKey, type Product } from "@/lib/api/products";
import { usePermissions } from "@/lib/auth/use-permissions";

const CONTROL =
  "h-9 w-full rounded-lg border border-input bg-background px-3 text-sm text-foreground"
  + " focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none";

/**
 * What each product costs in one price list (`PRD-03`).
 *
 * **Amounts are strings from the input to the wire, and never become numbers.** `BR-PRD-8` is not a
 * serialization preference: `JSON.parse` makes an IEEE-754 double of a bare `12.50`, and the device
 * pricing engine would then be doing float arithmetic before `decimal.js` ever saw the value. So
 * nothing here calls `Number()`, `parseFloat` or a locale number formatter — what the author typed
 * is what is sent, and what the server returns is what is shown.
 *
 * **The currency belongs to the list**, shown once beside the heading rather than repeated per row.
 * A list has exactly one (`BR-PRD-1`), which is what makes its prices summable at all.
 *
 * **The whole catalogue, with an amount box each.** The API's PUT replaces the price set, so a
 * product left blank is unpriced in this list rather than left alone — the same shape as the
 * assortment editor, and for the same reason.
 */
export function PriceListPrices() {
  const t = useTranslations("PriceLists");
  const { user } = useAuth();
  const params = useParams<{ id: string }>();

  const accessToken = user?.access_token;
  const subject = user?.profile.sub;
  const id = params.id;
  const enabled = Boolean(accessToken && subject && id);

  const lists = useQuery({
    enabled,
    queryKey: priceListsKey(subject ?? ""),
    queryFn: ({ signal }) => fetchPriceLists(accessToken!, signal),
  });

  const products = useQuery({
    enabled,
    queryKey: productsKey(subject ?? ""),
    queryFn: ({ signal }) => fetchProducts(accessToken!, signal),
  });

  const prices = useQuery({
    enabled,
    queryKey: pricesKey(subject ?? "", id ?? ""),
    queryFn: ({ signal }) => fetchPrices(accessToken!, id, signal),
  });

  const failed = [lists, products, prices].find((query) => query.isError);

  if (failed) {
    const error = failed.error;

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

  if (!lists.data || !products.data || !prices.data) {
    return <p className="text-sm text-muted-foreground">{t("loading")}</p>;
  }

  const list = lists.data.find((candidate) => candidate.id === id);

  // The list index answered, and this id was not in it. That is a price list this tenant does not
  // have, which is also what another tenant's id looks like.
  if (!list) {
    return (
      <p role="alert" className="text-sm text-destructive">
        {t("notFound")}
      </p>
    );
  }

  return (
    <div className="flex max-w-4xl flex-col gap-4">
      <header>
        <p className="font-mono text-[11.5px] text-muted-foreground">{t("pricesCrumb")}</p>
        <h1 className="text-lg font-semibold tracking-tight">{list.name}</h1>
        <p className="mt-1 text-sm text-muted-foreground">
          {list.effectiveTo
            ? t("pricesIntro", {
                currency: list.currency,
                from: list.effectiveFrom,
                to: list.effectiveTo,
              })
            : t("pricesIntroOpen", { currency: list.currency, from: list.effectiveFrom })}
        </p>
      </header>

      <PriceEditor
        // Remounted per list, so the boxes reseed from what the server holds rather than carrying
        // one list's edits into another.
        key={list.id}
        list={list}
        products={products.data}
        prices={prices.data}
      />
    </div>
  );
}

/** The editable half, seeded once from what the server holds. */
function PriceEditor({
  list,
  products,
  prices,
}: {
  list: PriceList;
  products: readonly Product[];
  prices: readonly PriceLine[];
}) {
  const t = useTranslations("PriceLists");
  // Server refusals, in the reader's language (ADR-0012 stage 2).
  const refusals = useTranslations("Refusals");
  const client = useQueryClient();
  const { user } = useAuth();
  const { has } = usePermissions();

  const accessToken = user?.access_token;

  const stored = useMemo(
    () => new Map(prices.map((line) => [line.productId, line.price.amount])),
    [prices],
  );

  // The amount exactly as the server sent it — "12.50", not 12.5 — because that string is the value,
  // not a rendering of one.
  const [amounts, setAmounts] = useState<Map<string, string>>(() => new Map(stored));
  const [search, setSearch] = useState("");
  const [refused, setRefused] = useState<readonly string[]>([]);

  const save = useMutation({
    mutationFn: () =>
      setPrices(
        accessToken!,
        list.id,
        [...amounts.entries()]
          // Blank means unpriced in this list, which the replace semantics express by omission.
          .filter(([, amount]) => amount.trim() !== "")
          .map(([productId, amount]) => ({ productId, amount: amount.trim() })),
      ),

    onSuccess: async () => {
      setRefused([]);
      await client.invalidateQueries({ queryKey: ["price-lists"] });
    },

    onError: (error) =>
      setRefused(
        error instanceof ApiError && error.problems.length > 0
          ? refusalTexts(refusals, error.problems)
          : [t("saveFailed")],
      ),
  });

  const rows = useMemo(() => {
    const needle = search.trim().toLowerCase();

    if (needle === "") return products;

    return products.filter(
      (product) =>
        product.sku.toLowerCase().includes(needle) || product.name.toLowerCase().includes(needle),
    );
  }, [products, search]);

  // Checked here so a comma decimal is a message under the box rather than a refusal about a list.
  // "12,50" would parse to 1250 under invariant culture if separators were allowed — a hundredfold
  // error that reads as a plausible price. The server checks it again regardless.
  const malformed = useMemo(
    () =>
      new Set(
        [...amounts.entries()]
          .filter(([, amount]) => amount.trim() !== "" && !looksLikeAnAmount(amount))
          .map(([productId]) => productId),
      ),
    [amounts],
  );

  const dirty = useMemo(() => {
    const set = (map: Map<string, string>) =>
      [...map.entries()].filter(([, amount]) => amount.trim() !== "");

    const before = set(stored);
    const after = set(amounts);

    if (before.length !== after.length) return true;

    return after.some(([productId, amount]) => stored.get(productId) !== amount.trim());
  }, [stored, amounts]);

  const canWrite = has("product:write");
  const priced = [...amounts.values()].filter((amount) => amount.trim() !== "").length;

  return (
    <div className="flex flex-col gap-4">
      <div className="flex flex-wrap items-center gap-3">
        <input
          type="search"
          value={search}
          onChange={(event) => setSearch(event.target.value)}
          placeholder={t("searchPlaceholder")}
          aria-label={t("search")}
          className={`${CONTROL} max-w-xs`}
        />

        <p className="text-sm text-muted-foreground" role="status">
          {t("pricedSummary", { priced, total: products.length })}
        </p>
      </div>

      {refused.length > 0 ? (
        <ul role="alert" className="rounded-xl bg-destructive/10 px-4 py-3 text-sm text-destructive">
          {refused.map((problem) => (
            <li key={problem}>{problem}</li>
          ))}
        </ul>
      ) : null}

      {rows.length === 0 ? (
        <p className="text-sm text-muted-foreground">{t("noMatches", { search })}</p>
      ) : (
        <ul className="flex flex-col divide-y divide-border rounded-xl border border-border">
          {rows.map((product) => {
            const amount = amounts.get(product.id) ?? "";
            const bad = malformed.has(product.id);

            return (
              <li
                key={product.id}
                className="flex flex-wrap items-center gap-x-3 gap-y-1 px-4 py-2.5 text-sm"
              >
                <span className="font-mono text-xs text-muted-foreground">{product.sku}</span>
                <span className="font-medium">{product.name}</span>

                <div className="ml-auto flex items-center gap-2">
                  <input
                    // Deliberately `type="text"`, not `type="number"`. A number input hands back a
                    // value the browser has already interpreted — and on a comma-decimal locale it
                    // reports "12,50" as empty, so a price silently disappears on save.
                    type="text"
                    inputMode="decimal"
                    className={`h-8 w-28 rounded-lg border px-2 text-right text-sm ${
                      bad ? "border-destructive" : "border-input"
                    } bg-background`}
                    disabled={!canWrite}
                    value={amount}
                    onChange={(event) =>
                      setAmounts((current) => new Map(current).set(product.id, event.target.value))
                    }
                    aria-invalid={bad}
                    aria-label={t("priceNamed", { name: product.name })}
                  />
                  <span className="w-8 font-mono text-xs text-muted-foreground">{list.currency}</span>
                </div>

                {bad ? (
                  <p className="basis-full text-right text-xs text-destructive">{t("notAnAmount")}</p>
                ) : null}
              </li>
            );
          })}
        </ul>
      )}

      {canWrite ? (
        <div className="flex items-center gap-3">
          <Button
            type="button"
            size="sm"
            disabled={!dirty || malformed.size > 0 || save.isPending}
            onClick={() => save.mutate()}
          >
            {save.isPending ? t("saving") : t("savePrices")}
          </Button>

          {malformed.size > 0 ? (
            <span className="text-xs text-destructive">{t("fixAmounts")}</span>
          ) : dirty ? (
            <span className="text-xs text-muted-foreground">{t("unsaved")}</span>
          ) : null}
        </div>
      ) : null}
    </div>
  );
}
