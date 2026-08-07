"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Plus, Tag, Target } from "lucide-react";
import { useTranslations } from "next-intl";
import { useState } from "react";

import { useAuth } from "@/components/auth-provider";
import { Button } from "@/components/ui/button";
import { Link } from "@/i18n/navigation";
import { ApiError } from "@/lib/api/client";
import {
  createPriceList,
  fetchPriceLists,
  priceListsKey,
  updatePriceList,
  type PriceList,
} from "@/lib/api/price-lists";
import { usePermissions } from "@/lib/auth/use-permissions";

const CONTROL =
  "h-9 w-full rounded-lg border border-input bg-background px-3 text-sm text-foreground"
  + " focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none";

/**
 * The price lists a tenant maintains (`PRD-03`).
 *
 * **A list here, its prices behind it.** Authoring a list — what it is called, what currency it is
 * in, when it applies — is a different sitting from pricing several hundred products into it, so the
 * prices get their own route rather than an expanding section.
 *
 * **Where a list applies is not here either.** Assigning it to channels and outlets is its own
 * decision and its own slice, exactly as `PriceListAssignment` is its own aggregate. Until then a
 * list can be authored and priced and reaches nobody, which is what a draft is.
 */
export function PriceListBrowser() {
  const t = useTranslations("PriceLists");
  const client = useQueryClient();
  const { user } = useAuth();
  const { has } = usePermissions();

  const accessToken = user?.access_token;
  const subject = user?.profile.sub;

  const lists = useQuery({
    enabled: Boolean(accessToken && subject),
    queryKey: priceListsKey(subject ?? ""),
    queryFn: ({ signal }) => fetchPriceLists(accessToken!, signal),
  });

  const [editing, setEditing] = useState<PriceList | "new" | null>(null);

  const canWrite = has("product:write");

  return (
    <div className="flex flex-col gap-4">
      {canWrite && !lists.isError ? (
        <div>
          <Button type="button" size="sm" onClick={() => setEditing("new")}>
            <Plus className="size-4" />
            {t("newList")}
          </Button>
        </div>
      ) : null}

      {editing !== null ? (
        <PriceListForm
          // Remounted per target: the form captures its defaults on first render.
          key={editing === "new" ? "new" : editing.id}
          list={editing === "new" ? undefined : editing}
          onDone={async () => {
            setEditing(null);
            await client.invalidateQueries({ queryKey: ["price-lists"] });
          }}
          onCancel={() => setEditing(null)}
        />
      ) : null}

      {lists.isPending ? (
        <p className="text-sm text-muted-foreground">{t("loading")}</p>
      ) : lists.isError ? (
        <p role="alert" className="text-sm text-destructive">
          {lists.error instanceof ApiError && lists.error.status === 403
            ? t("forbidden")
            : t("failed")}
        </p>
      ) : lists.data.length === 0 ? (
        <p className="text-sm text-muted-foreground">{t("empty")}</p>
      ) : (
        <ul className="flex flex-col divide-y divide-border rounded-xl border border-border">
          {lists.data.map((list) => (
            <li key={list.id} className="flex flex-wrap items-center gap-x-3 gap-y-1 px-4 py-2.5 text-sm">
              <span className="font-medium">{list.name}</span>
              <span className="font-mono text-xs text-muted-foreground">{list.currency}</span>

              {/* The window as stored, not localised. These are business days that mean the same
                  date everywhere (BR-PRD-6 evaluates them in the outlet's zone), and a formatter
                  would shift them by one wherever the reader happens to be. */}
              <span className="text-xs text-muted-foreground">
                {list.effectiveTo
                  ? t("window", { from: list.effectiveFrom, to: list.effectiveTo })
                  : t("windowOpen", { from: list.effectiveFrom })}
              </span>

              <div className="ml-auto flex gap-2">
                <Button
                  render={<Link href={`/products/price-lists/${list.id}`} />}
                  nativeButton={false}
                  size="sm"
                  variant="outline"
                >
                  <Tag className="size-4" />
                  {t("prices")}
                </Button>

                {/* Separate from the prices, because pricing a catalogue and deciding which shops
                    pay those prices are different decisions made at different times. */}
                <Button
                  render={<Link href={`/products/price-lists/${list.id}/scope`} />}
                  nativeButton={false}
                  size="sm"
                  variant="outline"
                >
                  <Target className="size-4" />
                  {t("scope")}
                </Button>

                {canWrite ? (
                  <Button
                    type="button"
                    size="sm"
                    variant="outline"
                    onClick={() => setEditing(list)}
                    aria-label={t("editNamed", { name: list.name })}
                  >
                    {t("edit")}
                  </Button>
                ) : null}
              </div>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}

/** Create or rename a price list. One component for both — see the currency field. */
function PriceListForm({
  list,
  onDone,
  onCancel,
}: {
  list?: PriceList;
  onDone: () => void;
  onCancel: () => void;
}) {
  const t = useTranslations("PriceLists");
  const { user } = useAuth();

  const accessToken = user?.access_token;

  const [name, setName] = useState(list?.name ?? "");
  const [currency, setCurrency] = useState(list?.currency ?? "");
  const [from, setFrom] = useState(list?.effectiveFrom ?? "");
  const [to, setTo] = useState(list?.effectiveTo ?? "");
  const [refused, setRefused] = useState<readonly string[]>([]);

  const save = useMutation({
    mutationFn: () =>
      list
        ? updatePriceList(accessToken!, list.id, {
            name,
            effectiveFrom: from,
            effectiveTo: to === "" ? null : to,
          })
        : createPriceList(accessToken!, {
            name,
            currency: currency.toUpperCase(),
            effectiveFrom: from,
            effectiveTo: to === "" ? null : to,
          }),

    onSuccess: () => {
      setRefused([]);
      onDone();
    },

    onError: (error) =>
      setRefused(
        error instanceof ApiError && error.problems.length > 0
          ? error.problems.map((problem) => problem.message)
          : [t("saveFailed")],
      ),
  });

  return (
    <form
      noValidate
      onSubmit={(event) => {
        event.preventDefault();
        save.mutate();
      }}
      className="flex flex-col gap-4 rounded-xl border border-border p-4"
    >
      <div className="grid gap-4 sm:grid-cols-2">
        <div className="flex flex-col gap-1.5">
          <label htmlFor="listName" className="text-sm font-medium">
            {t("name")}
          </label>
          <input
            id="listName"
            className={CONTROL}
            value={name}
            onChange={(event) => setName(event.target.value)}
          />
        </div>

        <div className="flex flex-col gap-1.5">
          <label htmlFor="currency" className="text-sm font-medium">
            {t("currency")}
          </label>
          <input
            id="currency"
            className={CONTROL}
            maxLength={3}
            value={currency}
            // Fixed once set, and the API has no parameter for changing it: every amount in the list
            // would be reinterpreted rather than converted. A tenant needing the same prices in
            // another currency needs another list, priced for it.
            disabled={Boolean(list)}
            onChange={(event) => setCurrency(event.target.value)}
          />
          <p className="text-xs text-muted-foreground">
            {list ? t("currencyFixed") : t("currencyHint")}
          </p>
        </div>

        <div className="flex flex-col gap-1.5">
          <label htmlFor="effectiveFrom" className="text-sm font-medium">
            {t("from")}
          </label>
          <input
            id="effectiveFrom"
            type="date"
            className={CONTROL}
            value={from}
            onChange={(event) => setFrom(event.target.value)}
          />
        </div>

        <div className="flex flex-col gap-1.5">
          <label htmlFor="effectiveTo" className="text-sm font-medium">
            {t("to")}
          </label>
          <input
            id="effectiveTo"
            type="date"
            className={CONTROL}
            value={to}
            onChange={(event) => setTo(event.target.value)}
          />
          {/* Half-open: the end day is the first day the list no longer applies, so a successor
              starts on exactly the date its predecessor stops. Saying so is the difference between
              a gap and an overlap on the one day everybody checks. */}
          <p className="text-xs text-muted-foreground">{t("toHint")}</p>
        </div>
      </div>

      {refused.length > 0 ? (
        <ul role="alert" className="rounded-xl bg-destructive/10 px-4 py-3 text-sm text-destructive">
          {refused.map((problem) => (
            <li key={problem}>{problem}</li>
          ))}
        </ul>
      ) : null}

      <div className="flex gap-2">
        <Button type="submit" size="sm" disabled={save.isPending}>
          {save.isPending ? t("saving") : t("save")}
        </Button>
        <Button type="button" size="sm" variant="outline" onClick={onCancel}>
          {t("cancel")}
        </Button>
      </div>
    </form>
  );
}
