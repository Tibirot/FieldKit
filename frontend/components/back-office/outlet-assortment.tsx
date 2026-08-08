"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslations } from "next-intl";
import { useParams } from "next/navigation";
import { useMemo, useState } from "react";

import { useAuth } from "@/components/auth-provider";
import { Button } from "@/components/ui/button";
import {
  channelAssortmentKey,
  fetchChannelAssortment,
  fetchOutletAssortment,
  fetchOutletOverrides,
  outletAssortmentKey,
  outletOverridesKey,
  setOutletOverrides,
  type AssortmentItem,
  type AssortmentOverride,
  type OverrideKind,
} from "@/lib/api/assortments";
import { ApiError } from "@/lib/api/client";
import { refusalTexts } from "@/lib/api/refusals";
import { fetchOutlet, outletKey, type OutletDetail } from "@/lib/api/outlets";
import { fetchProducts, productsKey, type Product } from "@/lib/api/products";
import { usePermissions } from "@/lib/auth/use-permissions";

const CONTROL =
  "h-9 w-full rounded-lg border border-input bg-background px-3 text-sm text-foreground"
  + " focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none";

/** What a row is set to. Absent means this outlet follows its channel for that product. */
type Overrides = Map<string, { kind: OverrideKind; mustStock: boolean }>;

/**
 * How one shop departs from its channel's assortment (`PRD-02`).
 *
 * **Per outlet, and therefore here rather than under Products.** The channel assortment is a
 * decision about a kind of shop, made once by whoever owns the category; an override is a decision
 * about *this* shop, usually made by whoever looks after it. Different people, different screens.
 *
 * **Three states per product**, which is what the API models: follow the channel, `Added`, or
 * `Removed`. `Added` covers two cases deliberately — a product the channel does not carry, and a
 * product it does carry whose must-stock flag this shop disagrees about — because both are "this
 * outlet says yes, with these terms".
 *
 * **The effective assortment is read from the server**, never recomputed here. Channel minus
 * removals plus additions is `PRD-02`'s rule and it lives in one place; a second implementation in
 * TypeScript would be a copy to keep in step, and the copy is what drifts.
 */
export function OutletAssortment() {
  const t = useTranslations("OutletAssortment");
  const { user } = useAuth();
  const params = useParams<{ id: string }>();

  const accessToken = user?.access_token;
  const subject = user?.profile.sub;
  const outletId = params.id;
  const enabled = Boolean(accessToken && subject && outletId);

  const outlet = useQuery({
    enabled,
    queryKey: outletKey(subject ?? "", outletId ?? ""),
    queryFn: ({ signal }) => fetchOutlet(accessToken!, outletId, signal),
  });

  const products = useQuery({
    enabled,
    queryKey: productsKey(subject ?? ""),
    queryFn: ({ signal }) => fetchProducts(accessToken!, signal),
  });

  const overrides = useQuery({
    enabled,
    queryKey: outletOverridesKey(subject ?? "", outletId ?? ""),
    queryFn: ({ signal }) => fetchOutletOverrides(accessToken!, outletId, signal),
  });

  const effective = useQuery({
    enabled,
    queryKey: outletAssortmentKey(subject ?? "", outletId ?? ""),
    queryFn: ({ signal }) => fetchOutletAssortment(accessToken!, outletId, signal),
  });

  // The baseline this screen is *about*. Only askable once the outlet has told us its channel, which
  // is the join Products cannot make alone — the same one the API makes server-side.
  const channelId = outlet.data?.channelId;

  const channel = useQuery({
    enabled: enabled && Boolean(channelId),
    queryKey: channelAssortmentKey(subject ?? "", channelId ?? ""),
    queryFn: ({ signal }) => fetchChannelAssortment(accessToken!, channelId!, signal),
  });

  const failed = [outlet, products, overrides, effective, channel].find((query) => query.isError);

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

  // Presence rather than `isPending`, so TypeScript narrows all five at once — the error branch
  // above already ruled out the failed case, but it does so through a `find` the compiler cannot
  // follow, and non-null assertions on five queries would be five places to be wrong later.
  if (
    !outlet.data
    || !products.data
    || !overrides.data
    || !effective.data
    || !channel.data
  ) {
    return <p className="text-sm text-muted-foreground">{t("loading")}</p>;
  }

  return (
    <div className="flex max-w-4xl flex-col gap-4">
      <header>
        <p className="font-mono text-[11.5px] text-muted-foreground">
          {t("crumb", { code: outlet.data.code })}
        </p>
        <h1 className="text-lg font-semibold tracking-tight">
          {t("title", { name: outlet.data.name })}
        </h1>
        <p className="mt-1 text-sm text-muted-foreground">
          {t("intro", { channel: outlet.data.channelName })}
        </p>
      </header>

      <OutletOverrides
        // Remounted per outlet, so the controls reseed from what the server holds rather than
        // carrying one shop's edits to the next — the same reason the channel editor is keyed.
        key={outletId}
        outlet={outlet.data}
        products={products.data}
        channelItems={channel.data}
        overrides={overrides.data}
        effective={effective.data}
      />
    </div>
  );
}

/** The editable half, seeded once from what the server holds. */
function OutletOverrides({
  outlet,
  products,
  channelItems,
  overrides: stored,
  effective,
}: {
  outlet: OutletDetail;
  products: readonly Product[];
  channelItems: readonly AssortmentItem[];
  overrides: readonly AssortmentOverride[];
  effective: readonly AssortmentItem[];
}) {
  const t = useTranslations("OutletAssortment");
  // Server refusals, in the reader's language (ADR-0012 stage 2).
  const refusals = useTranslations("Refusals");
  const client = useQueryClient();
  const { user } = useAuth();
  const { has } = usePermissions();

  const accessToken = user?.access_token;

  const inChannel = useMemo(
    () => new Map(channelItems.map((item) => [item.productId, item.mustStock])),
    [channelItems],
  );

  const initial = useMemo(
    () =>
      new Map(stored.map((over) => [over.productId, { kind: over.kind, mustStock: over.mustStock }])),
    [stored],
  );

  const [selection, setSelection] = useState<Overrides>(() => new Map(initial));
  const [search, setSearch] = useState("");
  const [refused, setRefused] = useState<readonly string[]>([]);

  const save = useMutation({
    mutationFn: () =>
      setOutletOverrides(
        accessToken!,
        outlet.id,
        [...selection.entries()].map(([productId, row]) => ({ productId, ...row })),
      ),

    onSuccess: async () => {
      setRefused([]);
      // Both this outlet's overrides and its effective assortment are now different, and the
      // effective one is the server's answer rather than ours. The prefix covers both.
      await client.invalidateQueries({ queryKey: ["assortment"] });
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

  function choose(product: Product, choice: "" | OverrideKind) {
    setSelection((current) => {
      const next = new Map(current);

      if (choice === "") next.delete(product.id);
      else if (choice === "Removed") next.set(product.id, { kind: "Removed", mustStock: false });
      else {
        // Seeded from what the channel says, so switching to Added on a product the channel already
        // carries starts from its current terms rather than silently clearing must-stock.
        next.set(product.id, {
          kind: "Added",
          mustStock: current.get(product.id)?.mustStock ?? inChannel.get(product.id) ?? false,
        });
      }

      return next;
    });
  }

  function toggleMustStock(product: Product, mustStock: boolean) {
    setSelection((current) => {
      const next = new Map(current);
      const row = next.get(product.id);

      if (row?.kind === "Added") next.set(product.id, { kind: "Added", mustStock });

      return next;
    });
  }

  const canWrite = has("product:write");

  const dirty = useMemo(() => {
    if (initial.size !== selection.size) return true;

    for (const [productId, row] of selection) {
      const was = initial.get(productId);
      if (!was || was.kind !== row.kind || was.mustStock !== row.mustStock) return true;
    }

    return false;
  }, [initial, selection]);

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

        {/* The server's answer, not ours — and so deliberately stale until a save. Saying "as
            saved" is more honest than a number that moves with unsaved edits and looks authoritative. */}
        <p className="text-sm text-muted-foreground" role="status">
          {t("summary", { count: effective.length, overrides: selection.size })}
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
            const carried = inChannel.has(product.id);
            const row = selection.get(product.id);

            return (
              <li
                key={product.id}
                className="flex flex-wrap items-center gap-x-3 gap-y-2 px-4 py-2.5 text-sm"
              >
                <span className="font-mono text-xs text-muted-foreground">{product.sku}</span>
                <span className="font-medium">{product.name}</span>

                <span className="text-xs text-muted-foreground">
                  {carried
                    ? inChannel.get(product.id)
                      ? t("channelCarriesMustStock")
                      : t("channelCarries")
                    : t("channelDoesNot")}
                </span>

                <div className="ml-auto flex items-center gap-3">
                  <select
                    className="h-8 rounded-lg border border-input bg-background px-2 text-xs"
                    disabled={!canWrite}
                    value={row?.kind ?? ""}
                    onChange={(event) => choose(product, event.target.value as "" | OverrideKind)}
                    aria-label={t("overrideNamed", { name: product.name })}
                  >
                    <option value="">{t("follow")}</option>
                    <option value="Added">{carried ? t("addChangeTerms") : t("add")}</option>
                    {/* Nothing to take away from a channel that does not carry it. The API would
                        store such a row and it would do nothing — a control that cannot act. */}
                    <option value="Removed" disabled={!carried}>
                      {t("remove")}
                    </option>
                  </select>

                  <label className="flex items-center gap-2 text-xs text-muted-foreground">
                    <input
                      type="checkbox"
                      className="size-4"
                      // Only an Added override carries terms. Following the channel means following
                      // its must-stock flag too, and a removal has nothing to qualify.
                      disabled={!canWrite || row?.kind !== "Added"}
                      checked={row?.kind === "Added" ? row.mustStock : false}
                      onChange={(event) => toggleMustStock(product, event.target.checked)}
                      aria-label={t("mustStockNamed", { name: product.name })}
                    />
                    {t("mustStock")}
                  </label>
                </div>
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
