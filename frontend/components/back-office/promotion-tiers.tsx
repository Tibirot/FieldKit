"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Plus, X } from "lucide-react";
import { useTranslations } from "next-intl";
import { useParams } from "next/navigation";
import { useMemo, useState } from "react";

import { useAuth } from "@/components/auth-provider";
import { Button } from "@/components/ui/button";
import { ApiError } from "@/lib/api/client";
import { looksLikeAnAmount } from "@/lib/api/price-lists";
import {
  fetchPromotions,
  fetchTiers,
  promotionsKey,
  setTiers,
  SMALLEST_TIER,
  tiersKey,
  type Promotion,
  type PromotionTier,
} from "@/lib/api/promotions";
import { usePermissions } from "@/lib/auth/use-permissions";

const CONTROL =
  "h-9 w-full rounded-lg border border-input bg-background px-3 text-sm text-foreground"
  + " focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none";

/** A row as the author is editing it — both fields as typed, neither parsed. */
type Row = { key: string; minQuantity: string; value: string };

/** What can be wrong with one row, named by the message that says so. */
type TierProblem = "tooSmall" | "duplicate" | "notAnAmount" | null;

/**
 * The thresholds a tiered promotion discounts by (`PRD-05`).
 *
 * **Only `VolumeTiered` has tiers.** A flat promotion with them would carry two discounts and no
 * rule saying which applies, which is why the API refuses the combination — and why the promotions
 * list offers this route on that type alone.
 *
 * **One kind for the whole set.** The API takes a currency per tier but refuses a set that mixes
 * percentages and amounts: "5% off at 10, three euros off at 24" is a rule nobody can sanity-check
 * at a glance and is far likelier to be a slip than an intention. Rather than let an author build
 * that and then be told, the choice is made once for the promotion and the rows inherit it — the
 * refusal is expressed as a shape instead of a message.
 *
 * **No tiers means it discounts nothing**, the same meaning an empty target set has.
 */
export function PromotionTiers() {
  const t = useTranslations("PromotionTiers");
  // Only for the type names, which the promotions list owns and this screen quotes.
  const types = useTranslations("Promotions");
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

  const tiers = useQuery({
    enabled,
    queryKey: tiersKey(subject ?? "", id ?? ""),
    queryFn: ({ signal }) => fetchTiers(accessToken!, id, signal),
  });

  const failed = [promotions, tiers].find((query) => query.isError);

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

  if (!promotions.data || !tiers.data) {
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

  // Reachable by typing the URL even though the list only links it for the right type. Saying which
  // type this promotion is beats an empty editor that would refuse everything saved into it.
  if (promotion.type !== "VolumeTiered") {
    return (
      <div className="flex max-w-3xl flex-col gap-4">
        <header>
          <p className="font-mono text-[11.5px] text-muted-foreground">{t("crumb")}</p>
          <h1 className="text-lg font-semibold tracking-tight">{promotion.name}</h1>
        </header>
        <p role="alert" className="text-sm text-destructive">
          {/* The type as the rest of the back office names it, not the enum on the wire. An author
              who has only ever seen "Percentage off" should not have to work out that PercentOff
              is the same thing. */}
          {t("wrongType", { type: types(`type${promotion.type}`) })}
        </p>
      </div>
    );
  }

  return (
    <div className="flex max-w-3xl flex-col gap-4">
      <header>
        <p className="font-mono text-[11.5px] text-muted-foreground">{t("crumb")}</p>
        <h1 className="text-lg font-semibold tracking-tight">{promotion.name}</h1>
        <p className="mt-1 text-sm text-muted-foreground">{t("intro")}</p>
      </header>

      <TierEditor
        // Remounted per promotion, so the rows reseed from what the server holds.
        key={promotion.id}
        promotion={promotion}
        tiers={tiers.data}
      />
    </div>
  );
}

/** The editable half, seeded once from what the server holds. */
function TierEditor({
  promotion,
  tiers,
}: {
  promotion: Promotion;
  tiers: readonly PromotionTier[];
}) {
  const t = useTranslations("PromotionTiers");
  const client = useQueryClient();
  const { user } = useAuth();
  const { has } = usePermissions();

  const accessToken = user?.access_token;

  const [rows, setRows] = useState<Row[]>(() =>
    tiers.map((tier, index) => ({
      key: `stored-${index}`,
      minQuantity: String(tier.minQuantity),
      // The amount exactly as the server sent it — "7.50", not 7.5 — because that string is the
      // value, not a rendering of one.
      value: tier.value,
    })),
  );

  // Read off the stored set rather than defaulted: a promotion already priced in euros should not
  // open as a percentage editor. An empty set has no kind yet, and a percentage is the common one.
  const [currency, setCurrency] = useState(tiers[0]?.currency ?? "");
  const [amounts, setAmounts] = useState(tiers.length > 0 && tiers[0].currency !== null);
  const [refused, setRefused] = useState<readonly string[]>([]);

  const save = useMutation({
    mutationFn: () =>
      setTiers(
        accessToken!,
        promotion.id,
        rows.map((row) => ({
          minQuantity: Number(row.minQuantity),
          value: row.value.trim(),
          // One kind for the set, so the currency comes from the promotion rather than the row.
          currency: amounts ? currency.trim().toUpperCase() : null,
        })),
      ),

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
   * Everything wrong with the set, in the API's terms, before it is sent.
   *
   * Checked here so a threshold of 1 or a repeated 12 is a message beside the row rather than a
   * refusal about a promotion. The server checks all of it again; this only decides what is worth
   * saying early.
   */
  const problems = useMemo(() => {
    const seen = new Map<string, number>();

    for (const row of rows) {
      seen.set(row.minQuantity, (seen.get(row.minQuantity) ?? 0) + 1);
    }

    // The message keys, not booleans: a row says what is wrong with it, and the words live in the
    // catalogue where both locales can see them.
    return rows.map((row): { quantity: TierProblem; value: TierProblem } => ({
      // A tier at 1 is "buy one or more", which is every line that matched — a flat discount wearing
      // a tier's clothes.
      quantity:
        !/^\d+$/.test(row.minQuantity.trim()) || Number(row.minQuantity) < SMALLEST_TIER
          ? "tooSmall"
          : (seen.get(row.minQuantity) ?? 0) > 1
            ? "duplicate"
            : null,
      value: row.value.trim() === "" || !looksLikeAnAmount(row.value) ? "notAnAmount" : null,
    }));
  }, [rows]);

  const broken = problems.some((problem) => problem.quantity !== null || problem.value !== null);
  const currencyMissing = amounts && !/^[A-Za-z]{3}$/.test(currency.trim());

  const dirty = useMemo(() => {
    if (rows.length !== tiers.length) return true;
    if ((tiers[0]?.currency ?? null) !== (amounts ? currency.trim().toUpperCase() : null)) {
      return true;
    }

    return rows.some(
      (row, index) =>
        String(tiers[index].minQuantity) !== row.minQuantity.trim()
        || tiers[index].value !== row.value.trim(),
    );
  }, [rows, tiers, amounts, currency]);

  const canWrite = has("product:write");

  return (
    <div className="flex flex-col gap-6">
      <p className="text-sm text-muted-foreground" role="status">
        {rows.length === 0 ? t("noTiersYet") : t("tierCount", { count: rows.length })}
      </p>

      {refused.length > 0 ? (
        <ul role="alert" className="rounded-xl bg-destructive/10 px-4 py-3 text-sm text-destructive">
          {refused.map((problem) => (
            <li key={problem}>{problem}</li>
          ))}
        </ul>
      ) : null}

      <fieldset className="flex flex-col gap-2">
        <legend className="text-sm font-semibold">{t("kind")}</legend>
        {/* One choice for the promotion, not one per row. The API refuses a mixed set, and a form
            that let an author build one would be teaching a rule by refusing it. */}
        <p className="text-xs text-muted-foreground">{t("kindHint")}</p>

        <div className="flex flex-wrap items-center gap-4">
          <label className="flex items-center gap-2 text-sm">
            <input
              type="radio"
              name="tierKind"
              className="size-4"
              disabled={!canWrite}
              checked={!amounts}
              onChange={() => setAmounts(false)}
            />
            {t("percentages")}
          </label>

          <label className="flex items-center gap-2 text-sm">
            <input
              type="radio"
              name="tierKind"
              className="size-4"
              disabled={!canWrite}
              checked={amounts}
              onChange={() => setAmounts(true)}
            />
            {t("amountsIn")}
          </label>

          {amounts ? (
            <input
              className={`${CONTROL} max-w-24 ${currencyMissing ? "border-destructive" : ""}`}
              maxLength={3}
              disabled={!canWrite}
              value={currency}
              aria-invalid={currencyMissing}
              aria-label={t("currency")}
              onChange={(event) => setCurrency(event.target.value)}
            />
          ) : null}
        </div>

        {currencyMissing ? (
          <p className="text-xs text-destructive">{t("currencyInvalid")}</p>
        ) : null}
      </fieldset>

      <section className="flex flex-col gap-2">
        <h2 className="text-sm font-semibold">{t("thresholds")}</h2>
        <p className="text-xs text-muted-foreground">{t("thresholdsHint")}</p>

        {rows.length === 0 ? (
          <p className="text-sm text-muted-foreground">{t("noThresholds")}</p>
        ) : (
          <ul className="flex flex-col divide-y divide-border rounded-xl border border-border">
            {rows.map((row, index) => {
              const problem = problems[index];

              return (
                <li key={row.key} className="flex flex-wrap items-center gap-3 px-4 py-2.5 text-sm">
                  <label className="flex items-center gap-2">
                    <span className="text-muted-foreground">{t("buyAtLeast")}</span>
                    <input
                      type="number"
                      min={SMALLEST_TIER}
                      step={1}
                      className={`h-8 w-20 rounded-lg border px-2 text-right text-sm ${
                        problem.quantity ? "border-destructive" : "border-input"
                      } bg-background`}
                      disabled={!canWrite}
                      value={row.minQuantity}
                      aria-invalid={problem.quantity !== null}
                      aria-label={t("minQuantityRow", { row: index + 1 })}
                      onChange={(event) =>
                        setRows((current) =>
                          current.map((candidate, at) =>
                            at === index
                              ? { ...candidate, minQuantity: event.target.value }
                              : candidate,
                          ),
                        )
                      }
                    />
                  </label>

                  <label className="flex items-center gap-2">
                    <span className="text-muted-foreground">{t("takeOff")}</span>
                    <input
                      // Deliberately `type="text"`, not `type="number"`. A number input hands back a
                      // value the browser has already interpreted — and on a comma-decimal locale it
                      // reports "12,50" as empty, so a discount silently disappears on save.
                      type="text"
                      inputMode="decimal"
                      className={`h-8 w-24 rounded-lg border px-2 text-right text-sm ${
                        problem.value ? "border-destructive" : "border-input"
                      } bg-background`}
                      disabled={!canWrite}
                      value={row.value}
                      aria-invalid={problem.value !== null}
                      aria-label={t("valueRow", { row: index + 1 })}
                      onChange={(event) =>
                        setRows((current) =>
                          current.map((candidate, at) =>
                            at === index ? { ...candidate, value: event.target.value } : candidate,
                          ),
                        )
                      }
                    />
                    <span className="w-10 font-mono text-xs text-muted-foreground">
                      {amounts ? currency.toUpperCase() : "%"}
                    </span>
                  </label>

                  {canWrite ? (
                    <button
                      type="button"
                      className="ml-auto text-muted-foreground hover:text-foreground"
                      aria-label={t("removeRow", { row: index + 1 })}
                      onClick={() =>
                        setRows((current) => current.filter((_, at) => at !== index))
                      }
                    >
                      <X className="size-4" />
                    </button>
                  ) : null}

                  {problem.quantity || problem.value ? (
                    <p className="basis-full text-xs text-destructive">
                      {problem.quantity ? t(problem.quantity) : null}
                      {problem.quantity && problem.value ? " " : null}
                      {problem.value ? t(problem.value) : null}
                    </p>
                  ) : null}
                </li>
              );
            })}
          </ul>
        )}

        {canWrite ? (
          <div>
            <Button
              type="button"
              size="sm"
              variant="outline"
              onClick={() =>
                setRows((current) => [
                  ...current,
                  // A key of its own rather than the array index. Nothing observable turns on it
                  // today — every box here is controlled and holds no DOM state, so index keys would
                  // render the same thing; I went looking for a test that told the two apart and
                  // could not write one. It stays because the moment a row grows anything React
                  // keeps outside the value (an uncontrolled field, a transition, a focus trap),
                  // index keys start reusing the wrong node and the bug is invisible.
                  {
                    key: `added-${current.length}-${current.at(-1)?.key ?? "first"}`,
                    minQuantity: "",
                    value: "",
                  },
                ])
              }
            >
              <Plus className="size-4" />
              {t("addTier")}
            </Button>
          </div>
        ) : null}
      </section>

      {canWrite ? (
        <div className="flex items-center gap-3">
          <Button
            type="button"
            size="sm"
            disabled={!dirty || broken || currencyMissing || save.isPending}
            onClick={() => save.mutate()}
          >
            {save.isPending ? t("saving") : t("save")}
          </Button>

          {broken || currencyMissing ? (
            <span className="text-xs text-destructive">{t("fixFirst")}</span>
          ) : dirty ? (
            <span className="text-xs text-muted-foreground">{t("unsaved")}</span>
          ) : null}
        </div>
      ) : null}
    </div>
  );
}
