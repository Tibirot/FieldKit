"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslations } from "next-intl";
import { useMemo, useState } from "react";

import { useAuth } from "@/components/auth-provider";
import { Button } from "@/components/ui/button";
import {
  channelAssortmentKey,
  fetchChannelAssortment,
  setChannelAssortment,
  type AssortmentItem,
} from "@/lib/api/assortments";
import { channelsKey, fetchChannels } from "@/lib/api/channels";
import { ApiError } from "@/lib/api/client";
import { fetchProducts, productsKey, type Product } from "@/lib/api/products";
import { usePermissions } from "@/lib/auth/use-permissions";

const CONTROL =
  "h-9 w-full rounded-lg border border-input bg-background px-3 text-sm text-foreground"
  + " focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none";

/** What a row is set to. Absent from the map means the product is not in the assortment. */
type Selection = Map<string, boolean>;

/**
 * Which products belong in a channel, and which of those must be stocked (`PRD-02`, `B2`).
 *
 * **The whole catalogue, with checkboxes** — rather than a picker that adds products one at a time.
 * The API's PUT replaces the assortment wholesale, so the screen that matches it is a picture of the
 * decision: every product, in or out. A queue of add/remove operations would need the client to
 * track what it had not sent, and two people editing one channel would interleave silently.
 *
 * **Authored per channel, read per outlet.** Nothing here is per-outlet: an outlet's departures from
 * its channel are overrides, which are their own screen because they are a different decision made
 * by different people at a different time.
 */
export function AssortmentEditor() {
  const t = useTranslations("Assortments");
  const { user } = useAuth();

  const accessToken = user?.access_token;
  const subject = user?.profile.sub;
  const enabled = Boolean(accessToken && subject);

  const channels = useQuery({
    enabled,
    queryKey: channelsKey(subject ?? ""),
    queryFn: ({ signal }) => fetchChannels(accessToken!, signal),
  });

  const products = useQuery({
    enabled,
    queryKey: productsKey(subject ?? ""),
    queryFn: ({ signal }) => fetchProducts(accessToken!, signal),
  });

  const [chosen, setChosen] = useState("");

  // Derived rather than defaulted in an effect. A selector that starts blank makes the screen look
  // empty when it is merely unasked — but syncing that with `useEffect` is a cascading render, and
  // the value is a pure function of what has loaded and what the reader has picked since.
  const channelId = chosen || channels.data?.[0]?.id || "";

  const assortment = useQuery({
    enabled: enabled && channelId !== "",
    queryKey: channelAssortmentKey(subject ?? "", channelId),
    queryFn: ({ signal }) => fetchChannelAssortment(accessToken!, channelId, signal),
  });

  if (channels.isError || products.isError) {
    const error = channels.error ?? products.error;

    return (
      <p role="alert" className="text-sm text-destructive">
        {error instanceof ApiError && error.status === 403 ? t("forbidden") : t("failed")}
      </p>
    );
  }

  if (channels.isPending || products.isPending) {
    return <p className="text-sm text-muted-foreground">{t("loading")}</p>;
  }

  if (channels.data.length === 0) {
    // The dead end this screen would otherwise be. An assortment is authored per channel, so a
    // workspace with none cannot have one — and the fix is on a different screen entirely.
    return <p className="text-sm text-muted-foreground">{t("noChannels")}</p>;
  }

  if (products.data.length === 0) {
    return <p className="text-sm text-muted-foreground">{t("noProducts")}</p>;
  }

  return (
    <div className="flex flex-col gap-4">
      <div className="flex min-w-56 max-w-xs flex-col gap-1.5">
        <label htmlFor="channelId" className="text-sm font-medium">
          {t("channel")}
        </label>
        <select
          id="channelId"
          className={CONTROL}
          value={channelId}
          onChange={(event) => setChosen(event.target.value)}
        >
          {channels.data.map((channel) => (
            <option key={channel.id} value={channel.id}>
              {channel.name}
            </option>
          ))}
        </select>
      </div>

      {assortment.isPending ? (
        <p className="text-sm text-muted-foreground">{t("loading")}</p>
      ) : assortment.isError ? (
        <p role="alert" className="text-sm text-destructive">
          {t("failed")}
        </p>
      ) : (
        <ChannelAssortment
          // Remounted per channel, which is what reseeds the checkboxes from the server rather than
          // carrying one channel's edits into another. The alternative — an effect that copies the
          // query into state — is a cascading render, and the lint rule that refuses it is right:
          // this is state derived from a prop, and React's answer to that is a key.
          key={channelId}
          channelId={channelId}
          items={assortment.data}
          products={products.data}
        />
      )}
    </div>
  );
}

/**
 * The editable half, seeded once from what the server holds.
 *
 * Split out so the selection is `useState`'s initial value rather than something an effect keeps in
 * step. Everything with behaviour lives here and takes plain arrays, which is also what makes it
 * testable without stubbing a loading state.
 */
function ChannelAssortment({
  channelId,
  items,
  products,
}: {
  channelId: string;
  items: readonly AssortmentItem[];
  products: readonly Product[];
}) {
  const t = useTranslations("Assortments");
  const client = useQueryClient();
  const { user } = useAuth();
  const { has } = usePermissions();

  const accessToken = user?.access_token;

  const stored = useMemo(
    () => new Map(items.map((item) => [item.productId, item.mustStock])),
    [items],
  );

  const [selection, setSelection] = useState<Selection>(() => new Map(stored));
  const [search, setSearch] = useState("");
  const [refused, setRefused] = useState<readonly string[]>([]);

  const save = useMutation({
    mutationFn: () =>
      setChannelAssortment(
        accessToken!,
        channelId,
        [...selection.entries()].map(([productId, mustStock]) => ({ productId, mustStock })),
      ),

    onSuccess: async () => {
      setRefused([]);
      // Every outlet in this channel now sells something different, and the effective-assortment
      // read is derived from this. The prefix covers both without enumerating outlets.
      await client.invalidateQueries({ queryKey: ["assortment"] });
    },

    onError: (error) =>
      setRefused(
        error instanceof ApiError && error.problems.length > 0
          ? error.problems.map((problem) => problem.message)
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

  function toggleIn(product: Product, included: boolean) {
    setSelection((current) => {
      const next = new Map(current);

      // Removing clears the flag with it. A must-stock entry for a product the channel does not
      // carry is a state `B2` does not have — the MSL is a subset of the assortment, not a parallel
      // list — and leaving one would resurface it the next time the product was re-added.
      if (included) next.set(product.id, false);
      else next.delete(product.id);

      return next;
    });
  }

  function toggleMustStock(product: Product, mustStock: boolean) {
    setSelection((current) => {
      const next = new Map(current);
      if (next.has(product.id)) next.set(product.id, mustStock);
      return next;
    });
  }

  const canWrite = has("product:write");
  const included = selection.size;
  const mustStock = [...selection.values()].filter(Boolean).length;

  // Compared against what the server last returned, so Save is only offered when there is something
  // to save — and so leaving mid-edit is visibly a choice rather than an accident.
  const dirty = useMemo(() => {
    if (stored.size !== selection.size) return true;

    for (const [productId, flag] of selection) {
      if (!stored.has(productId) || stored.get(productId) !== flag) return true;
    }

    return false;
  }, [stored, selection]);

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
          {t("summary", { included, mustStock })}
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
            const carried = selection.has(product.id);

            return (
              <li
                key={product.id}
                className="flex flex-wrap items-center gap-x-3 gap-y-1 px-4 py-2.5 text-sm"
              >
                <label className="flex items-center gap-2">
                  <input
                    type="checkbox"
                    className="size-4"
                    disabled={!canWrite}
                    checked={carried}
                    onChange={(event) => toggleIn(product, event.target.checked)}
                    aria-label={t("includeNamed", { name: product.name })}
                  />
                  <span className="font-mono text-xs text-muted-foreground">{product.sku}</span>
                  <span className="font-medium">{product.name}</span>
                </label>

                {product.status === "Discontinued" ? (
                  // Shown rather than filtered out: a discontinued product already in an assortment
                  // is exactly what someone came here to remove, and hiding it would make that
                  // impossible from the screen that owns the decision.
                  <span className="rounded-full bg-muted px-2 py-0.5 text-[11px] text-muted-foreground">
                    {t("discontinued")}
                  </span>
                ) : null}

                <label className="ml-auto flex items-center gap-2 text-xs text-muted-foreground">
                  <input
                    type="checkbox"
                    className="size-4"
                    // Meaningless for a product the channel does not carry — the MSL is a subset of
                    // the assortment (`B2`), so this cannot be reached without the box on the left.
                    disabled={!canWrite || !carried}
                    checked={selection.get(product.id) ?? false}
                    onChange={(event) => toggleMustStock(product, event.target.checked)}
                    aria-label={t("mustStockNamed", { name: product.name })}
                  />
                  {t("mustStock")}
                </label>
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
