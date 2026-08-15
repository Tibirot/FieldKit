"use client";

import { useQuery } from "@tanstack/react-query";
import { useTranslations } from "next-intl";
import { useMemo, useState } from "react";

import { useAuth } from "@/components/auth-provider";
import { ApiError } from "@/lib/api/client";
import { fetchTerritories, territoriesKey } from "@/lib/api/org";
import { fetchSummary, summaryKey, type PerfectStore } from "@/lib/api/reporting";
import { usePermissions } from "@/lib/auth/use-permissions";

const CONTROL =
  "h-8 rounded-lg border border-input bg-background px-2 text-sm text-foreground"
  + " focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none";

/**
 * The supervisor's dashboard (`AUD-09`, `JRN-10`, `ORD-09`, `VIS-10`) — W12 slice 4.
 *
 * **The first screen that reads across every module**, which makes it the honest test of whether the
 * contracts built in slices 1–2c are usable. It is one request: the composition in
 * `/api/reporting/summary` does the fanning out, so this component has four modules' worth of
 * numbers and no knowledge of where any of them came from.
 *
 * **Two things it refuses to do, and both are the point.**
 *
 * A rate that does not exist is rendered as **"—", never as 0%**. Coverage with nothing planned and
 * a strike rate with nothing finished are not failures, and a fresh tenant is the state most readers
 * meet this screen in — a wall of zeroes would tell a supervisor their team failed everything on
 * their first morning.
 *
 * And every figure is shown **beside what it was measured over**: coverage beside the calls, the
 * strike rate beside the visits, the score beside the audits. A percentage with no denominator is
 * the most confidently wrong thing a dashboard can print.
 */
export function Dashboard() {
  const t = useTranslations("Dashboard");
  const { user } = useAuth();
  const { has } = usePermissions();

  const accessToken = user?.access_token;
  const subject = user?.profile.sub;
  const enabled = Boolean(accessToken && subject);

  const [territoryId, setTerritoryId] = useState<string>("");

  /*
   * No dates: the server answers for the month containing *its* today, in UTC, which is the clock
   * every aggregate dates by.
   *
   * The first draft computed the window here and it was wrong twice over — `useBusinessDay` is a
   * formatter rather than a clock, and a browser deciding "this month" has to do it in a timezone
   * the data is not stored in. The response echoes the window it used, so this screen can say which
   * period it is showing without ever having chosen one.
   */
  const window = useMemo(() => ({ territoryId: territoryId || undefined }), [territoryId]);

  const summary = useQuery({
    enabled,
    queryKey: summaryKey(subject ?? "", window),
    queryFn: ({ signal }) => fetchSummary(accessToken!, window, signal),
  });

  // The selector only, and only for a reader who may list territories. Its absence costs nothing:
  // the dashboard defaults to every territory, which is the answer a reader without `territory:read`
  // would be left with anyway.
  const territories = useQuery({
    enabled: enabled && has("territory:read"),
    queryKey: territoriesKey(subject ?? ""),
    queryFn: ({ signal }) => fetchTerritories(accessToken!, undefined, signal),
  });

  if (summary.isError) {
    const error = summary.error;

    return (
      <p role="alert" className="text-sm text-destructive">
        {error instanceof ApiError && error.status === 403 ? t("forbidden") : t("failed")}
      </p>
    );
  }

  if (!summary.data) {
    return <p className="text-sm text-muted-foreground">{t("loading")}</p>;
  }

  const figures = summary.data;

  return (
    <div className="flex flex-col gap-4">
      {(territories.data?.length ?? 0) > 0 && (
        <div className="flex flex-wrap items-center gap-2">
          <label htmlFor="territory" className="text-sm text-muted-foreground">
            {t("territoryLabel")}
          </label>
          <select
            id="territory"
            className={CONTROL}
            value={territoryId}
            onChange={(event) => setTerritoryId(event.target.value)}
          >
            <option value="">{t("allTerritories")}</option>
            {territories.data?.map((territory) => (
              <option key={territory.id} value={territory.id}>
                {territory.name}
              </option>
            ))}
          </select>
        </div>
      )}

      {/*
       * The scope, said before the numbers rather than after them. A reader who is looking at zero
       * shops needs to know that before they read a single figure — and it is the one sentence that
       * distinguishes "nothing happened" from "nothing is in scope", which is the failure this
       * screen is most likely to be blamed for.
       */}
      <p className="text-sm text-muted-foreground">
        {t("scope", { outlets: figures.outlets, from: figures.from, to: figures.to })}
      </p>

      {figures.outlets === 0 ? (
        <Empty message={t("noOutlets")} />
      ) : (
        <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
          <Kpi
            label={t("coverage")}
            value={percent(figures.coverage.percentage)}
            detail={t("coverageDetail", {
              made: figures.coverage.made,
              planned: figures.coverage.planned,
              notVisited: figures.coverage.notVisited,
            })}
          />
          <Kpi
            label={t("strikeRate")}
            value={percent(figures.visits.strikeRate)}
            detail={t("strikeRateDetail", {
              productive: figures.visits.productive,
              finished: figures.visits.productive + figures.visits.nonProductive,
              open: figures.visits.open,
            })}
          />
          <Kpi
            label={t("perfectStore")}
            value={percent(figures.perfectStore.averageScore)}
            detail={t("perfectStoreDetail", {
              scored: figures.perfectStore.scored,
              audits: figures.perfectStore.audits,
            })}
            warning={warningOf(figures.perfectStore, t)}
          />
          <Kpi
            label={t("orderValue")}
            value={
              figures.orders.value.length === 0
                ? "—"
                : figures.orders.value
                    .map((value) => `${value.net.toFixed(2)} ${value.currencyCode}`)
                    .join(" · ")
            }
            detail={t("orderValueDetail", {
              orders: figures.orders.orders,
              lines: figures.orders.lines,
              rejected: figures.orders.rejected,
            })}
            warning={
              figures.orders.priceDisagreements > 0
                ? t("disputed", { disputed: figures.orders.priceDisagreements })
                : undefined
            }
          />
        </div>
      )}

      {figures.outlets > 0 && <Pillars perfectStore={figures.perfectStore} />}
    </div>
  );
}

/**
 * One headline figure, its denominator, and anything that makes it unsafe to read.
 *
 * The warning is a `<p>` rather than a tooltip: a mixed weighting is a caveat about the number
 * beside it, and a caveat a keyboard cannot reach is one that only sighted mouse users get.
 */
function Kpi({
  label,
  value,
  detail,
  warning,
}: {
  label: string;
  value: string;
  detail: string;
  warning?: string;
}) {
  return (
    <div className="flex flex-col gap-1 rounded-xl border border-border p-4">
      <p className="text-sm text-muted-foreground">{label}</p>
      <p className="text-2xl font-semibold tracking-tight tabular-nums">{value}</p>
      <p className="text-xs text-muted-foreground">{detail}</p>
      {warning && <p className="text-xs text-amber-600 dark:text-amber-500">{warning}</p>}
    </div>
  );
}

/** The pillar breakdown, which is what a supervisor acts on once the score says something is wrong. */
function Pillars({ perfectStore }: { perfectStore: PerfectStore }) {
  const t = useTranslations("Dashboard");

  if (perfectStore.pillars.length === 0) {
    return <Empty message={t("noAudits")} />;
  }

  return (
    <div className="flex flex-col gap-2">
      <h2 className="text-sm font-medium">{t("pillars")}</h2>
      <ul className="flex flex-col divide-y divide-border rounded-xl border border-border">
        {perfectStore.pillars.map((pillar) => (
          <li key={pillar.pillar} className="flex flex-wrap items-baseline gap-x-3 gap-y-1 px-4 py-3">
            <span className="min-w-40 flex-1 text-sm">{t(`pillar.${pillar.pillar}`)}</span>
            <span className="text-sm font-medium tabular-nums">{percent(pillar.average)}</span>
            <span className="text-xs text-muted-foreground">
              {/*
               * Measured *and* skipped, always both. `BR-AUD-2` renormalises a skipped pillar away
               * rather than scoring it zero, so this average is over the audits that measured it —
               * and 96% from two audits out of forty is a pillar nobody could count.
               */}
              {t("pillarDetail", { measured: pillar.measured, skipped: pillar.skipped })}
            </span>
          </li>
        ))}
      </ul>
    </div>
  );
}

/** Nothing to show, said in a sentence rather than as a screen of zeroes. */
function Empty({ message }: { message: string }) {
  return (
    <p className="rounded-xl border border-dashed border-border px-4 py-6 text-center text-sm text-muted-foreground">
      {message}
    </p>
  );
}

/**
 * A rate, or an em dash when there is not one.
 *
 * **Null is not zero, and this function is the only place that decision is enforced on screen.**
 * The server is careful to send null for "nothing has finished" and "nothing was planned"; rendering
 * it as `0%` here would throw that away at the last step, which is where it would be least visible.
 */
function percent(value: number | null): string {
  return value === null ? "—" : `${value.toFixed(2)}%`;
}

/** The caveat that makes an average unsafe to compare with last month's. */
function warningOf(
  perfectStore: PerfectStore,
  t: (key: string, values?: Record<string, unknown>) => string,
): string | undefined {
  return perfectStore.comparable
    ? undefined
    : t("mixedWeights", { versions: perfectStore.weightSetVersions.join(", ") });
}
