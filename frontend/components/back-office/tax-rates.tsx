"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Plus, X } from "lucide-react";
import { useTranslations } from "next-intl";
import { useParams } from "next/navigation";
import { useMemo, useState } from "react";

import { useAuth } from "@/components/auth-provider";
import { Breadcrumb } from "@/components/back-office/breadcrumb";
import { Button } from "@/components/ui/button";
import { ApiError } from "@/lib/api/client";
import { looksLikeAnAmount } from "@/lib/api/price-lists";
import {
  fetchTaxClasses,
  fetchTaxRates,
  setTaxRates,
  taxClassesKey,
  taxRatesKey,
  type TaxRate,
  type Vocabulary,
} from "@/lib/api/products";
import { refusalTexts } from "@/lib/api/refusals";
import { usePermissions } from "@/lib/auth/use-permissions";

const CONTROL =
  "h-8 rounded-lg border border-input bg-background px-2 text-sm text-foreground"
  + " focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none";

/** A row as the author is editing it — every field as typed, none parsed. */
type Row = {
  key: string;
  countryCode: string;
  percentage: string;
  effectiveFrom: string;
  effectiveTo: string;
};

/** What can be wrong with one row, named by the message that says so. */
type RateProblem = "countryInvalid" | "duplicate" | null;

/**
 * What a tax class is taxed at, per country and over time (`PRD-07`).
 *
 * **A class is not a rate.** The class says what *kind* of thing a product is — standard, reduced,
 * zero-rated — and lives in the classification vocabulary. The rate is what that kind costs in one
 * country at one time, and it is different in Romania from Germany and different this year from
 * last. Keeping them apart is what lets a product be filed once and taxed correctly everywhere.
 *
 * **No rate is not a rate of zero.** An empty set resolves to *unknown* — the engine returns no tax
 * rather than none — and a genuinely zero-rated class is a rate of `0` somebody authored. The
 * difference is between "we have not said" and "we have said none", and only the second is a
 * statement an auditor can check.
 */
export function TaxRates() {
  const t = useTranslations("TaxRates");
  const { user } = useAuth();
  const params = useParams<{ id: string }>();

  const accessToken = user?.access_token;
  const subject = user?.profile.sub;
  const id = params.id;
  const enabled = Boolean(accessToken && subject && id);

  const classes = useQuery({
    enabled,
    queryKey: taxClassesKey(subject ?? ""),
    queryFn: ({ signal }) => fetchTaxClasses(accessToken!, signal),
  });

  const rates = useQuery({
    enabled,
    queryKey: taxRatesKey(subject ?? "", id ?? ""),
    queryFn: ({ signal }) => fetchTaxRates(accessToken!, id, signal),
  });

  const failed = [classes, rates].find((query) => query.isError);

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

  if (!classes.data || !rates.data) {
    return <p className="text-sm text-muted-foreground">{t("loading")}</p>;
  }

  const taxClass = classes.data.find((candidate) => candidate.id === id);

  if (!taxClass) {
    return (
      <p role="alert" className="text-sm text-destructive">
        {t("notFound")}
      </p>
    );
  }

  return (
    <div className="flex max-w-3xl flex-col gap-4">
      <header>
        <Breadcrumb leaf={t("crumbLeaf")} />
        <h1 className="text-lg font-semibold tracking-tight">{taxClass.name}</h1>
        <p className="mt-1 text-sm text-muted-foreground">{t("intro")}</p>
      </header>

      <RateEditor
        // Remounted per class, so the rows reseed from what the server holds.
        key={taxClass.id}
        taxClass={taxClass}
        rates={rates.data}
      />
    </div>
  );
}

/** The editable half, seeded once from what the server holds. */
function RateEditor({ taxClass, rates }: { taxClass: Vocabulary; rates: readonly TaxRate[] }) {
  const t = useTranslations("TaxRates");
  // Server refusals, in the reader's language (ADR-0012 stage 2).
  const refusals = useTranslations("Refusals");
  const client = useQueryClient();
  const { user } = useAuth();
  const { has } = usePermissions();

  const accessToken = user?.access_token;

  const [rows, setRows] = useState<Row[]>(() =>
    rates.map((rate, index) => ({
      key: `stored-${index}`,
      countryCode: rate.countryCode,
      // The percentage exactly as the server sent it — "19.00", not 19 — because that string is the
      // value, not a rendering of one.
      percentage: rate.percentage,
      effectiveFrom: rate.effectiveFrom,
      effectiveTo: rate.effectiveTo ?? "",
    })),
  );
  const [refused, setRefused] = useState<readonly string[]>([]);

  const save = useMutation({
    mutationFn: () =>
      setTaxRates(
        accessToken!,
        taxClass.id,
        rows.map((row) => ({
          countryCode: row.countryCode.trim().toUpperCase(),
          percentage: row.percentage.trim(),
          effectiveFrom: row.effectiveFrom,
          effectiveTo: row.effectiveTo === "" ? null : row.effectiveTo,
        })),
      ),

    onSuccess: async () => {
      setRefused([]);
      await client.invalidateQueries({ queryKey: ["tax-classes"] });
    },

    onError: (error) =>
      setRefused(
        error instanceof ApiError && error.problems.length > 0
          ? refusalTexts(refusals, error.problems)
          : [t("saveFailed")],
      ),
  });

  /**
   * What the server would refuse, said beside the row instead.
   *
   * A rate's identity is its country *and* its start date together, so those two are what can
   * collide — the same country twice is ordinary and correct, as long as the windows differ.
   */
  const problems = useMemo(() => {
    const seen = new Map<string, number>();

    for (const row of rows) {
      const identity = `${row.countryCode.trim().toUpperCase()}@${row.effectiveFrom}`;
      seen.set(identity, (seen.get(identity) ?? 0) + 1);
    }

    return rows.map((row): { country: RateProblem; percentage: boolean; window: boolean } => ({
      country: !/^[A-Za-z]{2}$/.test(row.countryCode.trim())
        ? "countryInvalid"
        : (seen.get(`${row.countryCode.trim().toUpperCase()}@${row.effectiveFrom}`) ?? 0) > 1
          ? "duplicate"
          : null,
      // Checked here so a comma decimal is a message beside the row rather than a refusal about a
      // class. The server checks it again, and refuses anything outside 0–100.
      percentage: row.percentage.trim() === "" || !looksLikeAnAmount(row.percentage),
      // Half-open, so equal dates are an empty window — a rate that never applies.
      window: row.effectiveTo !== "" && row.effectiveTo <= row.effectiveFrom,
    }));
  }, [rows]);

  const broken =
    problems.some((p) => p.country !== null || p.percentage || p.window)
    || rows.some((row) => row.effectiveFrom === "");

  const dirty = useMemo(() => {
    if (rows.length !== rates.length) return true;

    return rows.some(
      (row, index) =>
        rates[index].countryCode !== row.countryCode.trim().toUpperCase()
        || rates[index].percentage !== row.percentage.trim()
        || rates[index].effectiveFrom !== row.effectiveFrom
        || (rates[index].effectiveTo ?? "") !== row.effectiveTo,
    );
  }, [rows, rates]);

  const canWrite = has("product:write");

  function update(index: number, patch: Partial<Row>) {
    setRows((current) => current.map((row, at) => (at === index ? { ...row, ...patch } : row)));
  }

  return (
    <div className="flex flex-col gap-4">
      <p className="text-sm text-muted-foreground" role="status">
        {rows.length === 0 ? t("noRatesYet") : t("rateCount", { count: rows.length })}
      </p>

      {refused.length > 0 ? (
        <ul role="alert" className="rounded-xl bg-destructive/10 px-4 py-3 text-sm text-destructive">
          {refused.map((problem) => (
            <li key={problem}>{problem}</li>
          ))}
        </ul>
      ) : null}

      {rows.length === 0 ? (
        <p className="text-sm text-muted-foreground">{t("noRates")}</p>
      ) : (
        <ul className="flex flex-col divide-y divide-border rounded-xl border border-border">
          {rows.map((row, index) => {
            const problem = problems[index];

            return (
              <li key={row.key} className="flex flex-wrap items-center gap-3 px-4 py-2.5 text-sm">
                <label className="flex items-center gap-2">
                  <span className="text-muted-foreground">{t("country")}</span>
                  <input
                    className={`${CONTROL} w-16 uppercase ${problem.country ? "border-destructive" : ""}`}
                    maxLength={2}
                    disabled={!canWrite}
                    value={row.countryCode}
                    aria-invalid={problem.country !== null}
                    aria-label={t("countryRow", { row: index + 1 })}
                    onChange={(event) => update(index, { countryCode: event.target.value })}
                  />
                </label>

                <label className="flex items-center gap-2">
                  <span className="text-muted-foreground">{t("rate")}</span>
                  <input
                    // Deliberately `type="text"`, not `type="number"`. A number input hands back a
                    // value the browser has already interpreted — and on a comma-decimal locale it
                    // reports "19,00" as empty, so a rate silently disappears on save.
                    type="text"
                    inputMode="decimal"
                    className={`${CONTROL} w-20 text-right ${problem.percentage ? "border-destructive" : ""}`}
                    disabled={!canWrite}
                    value={row.percentage}
                    aria-invalid={problem.percentage}
                    aria-label={t("rateRow", { row: index + 1 })}
                    onChange={(event) => update(index, { percentage: event.target.value })}
                  />
                  <span className="font-mono text-xs text-muted-foreground">%</span>
                </label>

                <label className="flex items-center gap-2">
                  <span className="text-muted-foreground">{t("from")}</span>
                  <input
                    type="date"
                    className={`${CONTROL} w-36`}
                    disabled={!canWrite}
                    value={row.effectiveFrom}
                    aria-label={t("fromRow", { row: index + 1 })}
                    onChange={(event) => update(index, { effectiveFrom: event.target.value })}
                  />
                </label>

                <label className="flex items-center gap-2">
                  <span className="text-muted-foreground">{t("to")}</span>
                  <input
                    type="date"
                    className={`${CONTROL} w-36 ${problem.window ? "border-destructive" : ""}`}
                    disabled={!canWrite}
                    value={row.effectiveTo}
                    aria-invalid={problem.window}
                    aria-label={t("toRow", { row: index + 1 })}
                    onChange={(event) => update(index, { effectiveTo: event.target.value })}
                  />
                </label>

                {canWrite ? (
                  <button
                    type="button"
                    className="ml-auto text-muted-foreground hover:text-foreground"
                    aria-label={t("removeRow", { row: index + 1 })}
                    onClick={() => setRows((current) => current.filter((_, at) => at !== index))}
                  >
                    <X className="size-4" />
                  </button>
                ) : null}

                {problem.country || problem.percentage || problem.window ? (
                  <p className="basis-full text-xs text-destructive">
                    {problem.country ? `${t(problem.country)} ` : null}
                    {problem.percentage ? `${t("notAnAmount")} ` : null}
                    {problem.window ? t("windowInverted") : null}
                  </p>
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
            variant="outline"
            onClick={() =>
              setRows((current) => [
                ...current,
                {
                  key: `added-${current.length}-${current.at(-1)?.key ?? "first"}`,
                  countryCode: "",
                  percentage: "",
                  effectiveFrom: "",
                  effectiveTo: "",
                },
              ])
            }
          >
            <Plus className="size-4" />
            {t("addRate")}
          </Button>

          <Button
            type="button"
            size="sm"
            disabled={!dirty || broken || save.isPending}
            onClick={() => save.mutate()}
          >
            {save.isPending ? t("saving") : t("save")}
          </Button>

          {broken ? (
            <span className="text-xs text-destructive">{t("fixFirst")}</span>
          ) : dirty ? (
            <span className="text-xs text-muted-foreground">{t("unsaved")}</span>
          ) : null}
        </div>
      ) : null}
    </div>
  );
}
