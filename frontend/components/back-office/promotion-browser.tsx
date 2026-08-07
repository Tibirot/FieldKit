"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Crosshair, Layers, Plus } from "lucide-react";
import { useTranslations } from "next-intl";
import { useState } from "react";

import { useAuth } from "@/components/auth-provider";
import { Button } from "@/components/ui/button";
import { LinkButton } from "@/components/ui/link-button";
import { ApiError } from "@/lib/api/client";
import { looksLikeAnAmount } from "@/lib/api/price-lists";
import { fetchProducts, productsKey, type Product } from "@/lib/api/products";
import {
  carriesItsOwnValue,
  createPromotion,
  fetchPromotions,
  promotionsKey,
  updatePromotion,
  type Bundle,
  type Promotion,
  type PromotionType,
} from "@/lib/api/promotions";
import { usePermissions } from "@/lib/auth/use-permissions";

const CONTROL =
  "h-9 w-full rounded-lg border border-input bg-background px-3 text-sm text-foreground"
  + " focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none";

const TYPES: readonly PromotionType[] = [
  "PercentOff",
  "FixedAmountOff",
  "VolumeTiered",
  "BuyXGetY",
];

/**
 * The deals a tenant runs (`PRD-05`).
 *
 * **Four types, one form.** `B1`'s types differ in what they carry, not in what they are, so the
 * form's fields follow the chosen type rather than there being four screens: a percentage takes a
 * value, a fixed amount takes a value and a currency, a tiered promotion takes neither because its
 * discounts are its tiers, and a bundle takes quantities. Sending a value for a type that has none
 * is refused by the API rather than ignored — a caller that sent one has misunderstood the type.
 *
 * **The type is chosen once.** Re-typing would reinterpret the value: 15 meaning "15% off" becoming
 * 15 meaning "€15 off", and every order already priced against it explained by a rule that no longer
 * exists. Same reason a price list's currency is fixed after creation.
 *
 * **What a promotion discounts, and where, is not here.** Targets, tiers and scope are their own
 * aggregates and their own slices — until then a promotion is a rule that exists and discounts
 * nobody, which is the same honest intermediate state a price list passes through.
 */
export function PromotionBrowser() {
  const t = useTranslations("Promotions");
  const client = useQueryClient();
  const { user } = useAuth();
  const { has } = usePermissions();

  const accessToken = user?.access_token;
  const subject = user?.profile.sub;

  const promotions = useQuery({
    enabled: Boolean(accessToken && subject),
    queryKey: promotionsKey(subject ?? ""),
    queryFn: ({ signal }) => fetchPromotions(accessToken!, signal),
  });

  const [editing, setEditing] = useState<Promotion | "new" | null>(null);

  const canWrite = has("product:write");

  return (
    <div className="flex flex-col gap-4">
      {canWrite && !promotions.isError ? (
        <div>
          <Button type="button" size="sm" onClick={() => setEditing("new")}>
            <Plus className="size-4" />
            {t("newPromotion")}
          </Button>
        </div>
      ) : null}

      {editing !== null ? (
        <PromotionForm
          // Remounted per target: the form captures its defaults on first render.
          key={editing === "new" ? "new" : editing.id}
          promotion={editing === "new" ? undefined : editing}
          onDone={async () => {
            setEditing(null);
            await client.invalidateQueries({ queryKey: ["promotions"] });
          }}
          onCancel={() => setEditing(null)}
        />
      ) : null}

      {promotions.isPending ? (
        <p className="text-sm text-muted-foreground">{t("loading")}</p>
      ) : promotions.isError ? (
        <p role="alert" className="text-sm text-destructive">
          {promotions.error instanceof ApiError && promotions.error.status === 403
            ? t("forbidden")
            : t("failed")}
        </p>
      ) : promotions.data.length === 0 ? (
        <p className="text-sm text-muted-foreground">{t("empty")}</p>
      ) : (
        // Best priority first, then by the day it opens — the order the API sends and the order
        // resolution considers them, so the screen is not quietly telling a different story.
        <ul className="flex flex-col divide-y divide-border rounded-xl border border-border">
          {promotions.data.map((promotion) => (
            <li
              key={promotion.id}
              className="flex flex-wrap items-center gap-x-3 gap-y-1 px-4 py-2.5 text-sm"
            >
              <span className="font-medium">{promotion.name}</span>
              <span className="rounded-full bg-muted px-2 py-0.5 text-xs">
                {t(`type${promotion.type}`)}
              </span>
              <span className="text-xs text-muted-foreground">{discountOf(promotion, t)}</span>

              {/* The window as stored, not localised — these are business days that mean the same
                  date everywhere (BR-PRD-6 evaluates them in the outlet's zone), and a formatter
                  would shift them by one wherever the reader happens to be. */}
              <span className="text-xs text-muted-foreground">
                {promotion.validTo
                  ? t("window", { from: promotion.validFrom, to: promotion.validTo })
                  : t("windowOpen", { from: promotion.validFrom })}
              </span>

              <div className="ml-auto flex items-center gap-3">
                <span className="font-mono text-xs text-muted-foreground">
                  {t("priorityShort", { priority: promotion.priority })}
                </span>

                {/* Separate from the form, because what a deal *is* and what it *applies to* are
                    decided at different times — and one Save covering both would let a stray tick
                    change which products are discounted while correcting a percentage. */}
                <LinkButton
                  href={`/products/promotions/${promotion.id}/targets`}
                  size="sm"
                  variant="outline"
                  // Named, because every row's link would otherwise read "Targets" and a list of
                  // four deals would give a screen reader four identical links to choose between.
                  aria-label={t("targetsNamed", { name: promotion.name })}
                >
                  <Crosshair className="size-4" />
                  {t("targets")}
                </LinkButton>

                {/* Offered on the one type that has tiers. A flat promotion with them would carry
                    two discounts and no rule saying which applies, so the API refuses the pairing —
                    and a link to an editor that refuses everything is not a courtesy. */}
                {promotion.type === "VolumeTiered" ? (
                  <LinkButton
                    href={`/products/promotions/${promotion.id}/tiers`}
                    size="sm"
                    variant="outline"
                    aria-label={t("tiersNamed", { name: promotion.name })}
                  >
                    <Layers className="size-4" />
                    {t("tiers")}
                  </LinkButton>
                ) : null}

                {canWrite ? (
                  <Button
                    type="button"
                    size="sm"
                    variant="outline"
                    onClick={() => setEditing(promotion)}
                    aria-label={t("editNamed", { name: promotion.name })}
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

/**
 * What this promotion takes off, in one phrase.
 *
 * The two types that carry no value of their own get a phrase saying where their discount lives
 * instead of a blank — "0%" would be a lie, and an empty cell reads as a missing value rather than
 * as one kept somewhere else.
 */
function discountOf(
  promotion: Promotion,
  t: ReturnType<typeof useTranslations<"Promotions">>,
): string {
  switch (promotion.type) {
    case "PercentOff":
      return t("percentOfValue", { value: promotion.value ?? "" });
    case "FixedAmountOff":
      return t("amountOfValue", {
        value: promotion.value ?? "",
        currency: promotion.currency ?? "",
      });
    case "BuyXGetY":
      return promotion.bundle
        ? t("bundleSummary", {
            buy: promotion.bundle.buyQuantity,
            get: promotion.bundle.getQuantity,
            percent: promotion.bundle.getPercentOff,
          })
        : "";
    default:
      return t("tieredSummary");
  }
}

/** Create or edit a promotion. One component for both — see the type and currency fields. */
function PromotionForm({
  promotion,
  onDone,
  onCancel,
}: {
  promotion?: Promotion;
  onDone: () => void;
  onCancel: () => void;
}) {
  const t = useTranslations("Promotions");
  const { user } = useAuth();

  const accessToken = user?.access_token;
  const subject = user?.profile.sub;

  const [name, setName] = useState(promotion?.name ?? "");
  const [type, setType] = useState<PromotionType>(promotion?.type ?? "PercentOff");
  const [value, setValue] = useState(promotion?.value ?? "");
  const [currency, setCurrency] = useState(promotion?.currency ?? "");
  const [from, setFrom] = useState(promotion?.validFrom ?? "");
  const [to, setTo] = useState(promotion?.validTo ?? "");
  const [priority, setPriority] = useState(String(promotion?.priority ?? 0));
  const [buyQuantity, setBuyQuantity] = useState(String(promotion?.bundle?.buyQuantity ?? 1));
  const [getQuantity, setGetQuantity] = useState(String(promotion?.bundle?.getQuantity ?? 1));
  const [getPercentOff, setGetPercentOff] = useState(promotion?.bundle?.getPercentOff ?? "100.00");
  const [getProductId, setGetProductId] = useState(promotion?.bundle?.getProductId ?? "");
  const [refused, setRefused] = useState<readonly string[]>([]);

  // Only for the one type that can name a giveaway. Fetching the catalogue to draw a select nobody
  // will see is a round trip spent on a field that is not rendered.
  const products = useQuery({
    enabled: Boolean(accessToken && subject) && type === "BuyXGetY",
    queryKey: productsKey(subject ?? ""),
    queryFn: ({ signal }) => fetchProducts(accessToken!, signal),
  });

  const carriesValue = carriesItsOwnValue(type);

  const bundle: Bundle = {
    buyQuantity: Number(buyQuantity),
    getQuantity: Number(getQuantity),
    getPercentOff: getPercentOff.trim(),
    // Empty means the same product that was bought, which is what the API's null says.
    getProductId: getProductId === "" ? null : getProductId,
  };

  const save = useMutation({
    mutationFn: () =>
      promotion
        ? updatePromotion(accessToken!, promotion.id, {
            name,
            validFrom: from,
            validTo: to === "" ? null : to,
            priority: Number(priority),
            // Omitted, not sent empty: the API refuses a value on a type that has none, and
            // `undefined` disappears from the JSON body rather than arriving as null.
            ...(carriesValue ? { value: value.trim() } : {}),
            ...(type === "BuyXGetY" ? { bundle } : {}),
          })
        : createPromotion(accessToken!, {
            name,
            type,
            validFrom: from,
            validTo: to === "" ? null : to,
            priority: Number(priority),
            ...(carriesValue ? { value: value.trim() } : {}),
            ...(type === "FixedAmountOff" ? { currency: currency.toUpperCase() } : {}),
            ...(type === "BuyXGetY" ? { bundle } : {}),
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

  // Checked here so a comma decimal is a message under the box rather than a refusal about a
  // promotion. "12,50" would parse to 1250 under invariant culture if separators were allowed — a
  // hundredfold discount that reads as a plausible one. The server checks it again regardless.
  const badValue = carriesValue && value.trim() !== "" && !looksLikeAnAmount(value);
  const badPercentOff = type === "BuyXGetY" && !looksLikeAnAmount(getPercentOff);

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
          <label htmlFor="promotionName" className="text-sm font-medium">
            {t("name")}
          </label>
          <input
            id="promotionName"
            className={CONTROL}
            value={name}
            onChange={(event) => setName(event.target.value)}
          />
        </div>

        <div className="flex flex-col gap-1.5">
          <label htmlFor="promotionType" className="text-sm font-medium">
            {t("type")}
          </label>
          <select
            id="promotionType"
            className={CONTROL}
            value={type}
            // Fixed once set, and the API has no parameter for changing it: the value would be
            // reinterpreted rather than converted, and every order already priced against this
            // promotion would be explained by a rule that no longer exists.
            disabled={Boolean(promotion)}
            onChange={(event) => setType(event.target.value as PromotionType)}
          >
            {TYPES.map((candidate) => (
              <option key={candidate} value={candidate}>
                {t(`type${candidate}`)}
              </option>
            ))}
          </select>
          <p className="text-xs text-muted-foreground">
            {promotion ? t("typeFixed") : t(`typeHint${type}`)}
          </p>
        </div>

        {carriesValue ? (
          <div className="flex flex-col gap-1.5">
            <label htmlFor="promotionValue" className="text-sm font-medium">
              {type === "PercentOff" ? t("percentOff") : t("amountOff")}
            </label>
            <input
              id="promotionValue"
              // Deliberately `type="text"`, not `type="number"`. A number input hands back a value
              // the browser has already interpreted — and on a comma-decimal locale it reports
              // "12,50" as empty, so a discount silently disappears on save.
              type="text"
              inputMode="decimal"
              className={`${CONTROL} ${badValue ? "border-destructive" : ""}`}
              value={value}
              aria-invalid={badValue}
              onChange={(event) => setValue(event.target.value)}
            />
            {badValue ? (
              <p className="text-xs text-destructive">{t("notAnAmount")}</p>
            ) : (
              <p className="text-xs text-muted-foreground">
                {type === "PercentOff" ? t("percentOffHint") : t("amountOffHint")}
              </p>
            )}
          </div>
        ) : null}

        {type === "FixedAmountOff" ? (
          <div className="flex flex-col gap-1.5">
            <label htmlFor="promotionCurrency" className="text-sm font-medium">
              {t("currency")}
            </label>
            <input
              id="promotionCurrency"
              className={CONTROL}
              maxLength={3}
              value={currency}
              // Fixed with the type, and for the same reason: €5 off becoming 5 RON off is a
              // reinterpretation, not a conversion.
              disabled={Boolean(promotion)}
              onChange={(event) => setCurrency(event.target.value)}
            />
            <p className="text-xs text-muted-foreground">
              {promotion ? t("currencyFixed") : t("currencyHint")}
            </p>
          </div>
        ) : null}

        <div className="flex flex-col gap-1.5">
          <label htmlFor="validFrom" className="text-sm font-medium">
            {t("from")}
          </label>
          <input
            id="validFrom"
            type="date"
            className={CONTROL}
            value={from}
            onChange={(event) => setFrom(event.target.value)}
          />
        </div>

        <div className="flex flex-col gap-1.5">
          <label htmlFor="validTo" className="text-sm font-medium">
            {t("to")}
          </label>
          <input
            id="validTo"
            type="date"
            className={CONTROL}
            value={to}
            onChange={(event) => setTo(event.target.value)}
          />
          {/* Half-open, so equal dates are an empty window rather than a single day — a promotion
              that is never live, which is certainly not what anyone meant to author. */}
          <p className="text-xs text-muted-foreground">{t("toHint")}</p>
        </div>

        <div className="flex flex-col gap-1.5">
          <label htmlFor="priority" className="text-sm font-medium">
            {t("priority")}
          </label>
          <input
            id="priority"
            type="number"
            step={1}
            className={CONTROL}
            value={priority}
            onChange={(event) => setPriority(event.target.value)}
          />
          {/* Worth saying out loud, because the intuition is the other way round: when two
              promotions both apply, BR-PRD-3 picks the higher priority and never the larger
              discount. An author who expects "the best deal wins" will author one that never
              fires. */}
          <p className="text-xs text-muted-foreground">{t("priorityHint")}</p>
        </div>
      </div>

      {type === "BuyXGetY" ? (
        <fieldset className="flex flex-col gap-3 rounded-xl border border-border p-4">
          <legend className="px-1 text-sm font-medium">{t("bundle")}</legend>
          <p className="text-xs text-muted-foreground">{t("bundleHint")}</p>

          <div className="grid gap-4 sm:grid-cols-2">
            <div className="flex flex-col gap-1.5">
              <label htmlFor="buyQuantity" className="text-sm font-medium">
                {t("buyQuantity")}
              </label>
              <input
                id="buyQuantity"
                type="number"
                min={1}
                step={1}
                className={CONTROL}
                value={buyQuantity}
                onChange={(event) => setBuyQuantity(event.target.value)}
              />
            </div>

            <div className="flex flex-col gap-1.5">
              <label htmlFor="getQuantity" className="text-sm font-medium">
                {t("getQuantity")}
              </label>
              <input
                id="getQuantity"
                type="number"
                min={1}
                step={1}
                className={CONTROL}
                value={getQuantity}
                onChange={(event) => setGetQuantity(event.target.value)}
              />
            </div>

            <div className="flex flex-col gap-1.5">
              <label htmlFor="getPercentOff" className="text-sm font-medium">
                {t("getPercentOff")}
              </label>
              <input
                id="getPercentOff"
                type="text"
                inputMode="decimal"
                className={`${CONTROL} ${badPercentOff ? "border-destructive" : ""}`}
                value={getPercentOff}
                aria-invalid={badPercentOff}
                onChange={(event) => setGetPercentOff(event.target.value)}
              />
              {badPercentOff ? (
                <p className="text-xs text-destructive">{t("notAnAmount")}</p>
              ) : (
                // 100 is free and is the ordinary case; anything less is "buy two, get a third
                // half price", which is the same rule with a different number.
                <p className="text-xs text-muted-foreground">{t("getPercentOffHint")}</p>
              )}
            </div>

            <div className="flex flex-col gap-1.5">
              <label htmlFor="getProductId" className="text-sm font-medium">
                {t("getProduct")}
              </label>
              <select
                id="getProductId"
                className={CONTROL}
                value={getProductId}
                onChange={(event) => setGetProductId(event.target.value)}
              >
                {/* Not a disabled placeholder: "the same product" is the default and the common
                    case, so it is a real option that stands for the API's null. */}
                <option value="">{t("sameProduct")}</option>
                {(products.data ?? []).map((product: Product) => (
                  <option key={product.id} value={product.id}>
                    {product.sku} — {product.name}
                  </option>
                ))}
              </select>
              <p className="text-xs text-muted-foreground">{t("getProductHint")}</p>
            </div>
          </div>
        </fieldset>
      ) : null}

      {refused.length > 0 ? (
        <ul role="alert" className="rounded-xl bg-destructive/10 px-4 py-3 text-sm text-destructive">
          {refused.map((problem) => (
            <li key={problem}>{problem}</li>
          ))}
        </ul>
      ) : null}

      <div className="flex items-center gap-3">
        <Button type="submit" size="sm" disabled={save.isPending || badValue || badPercentOff}>
          {save.isPending ? t("saving") : t("save")}
        </Button>
        <Button type="button" size="sm" variant="outline" onClick={onCancel}>
          {t("cancel")}
        </Button>

        {badValue || badPercentOff ? (
          <span className="text-xs text-destructive">{t("fixAmounts")}</span>
        ) : null}
      </div>
    </form>
  );
}
