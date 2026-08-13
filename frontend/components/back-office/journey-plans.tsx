"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { CalendarRange } from "lucide-react";
import { useTranslations } from "next-intl";
import { useMemo, useState } from "react";

import { useAuth } from "@/components/auth-provider";
import { Button } from "@/components/ui/button";
import { ApiError } from "@/lib/api/client";
import {
  fetchPlan,
  fetchPlans,
  generatePlan,
  planKey,
  plansKey,
  publishPlan,
  windowProblem,
  type Exclusion,
  type GeneratedPlan,
  type JourneyPlan,
  type PlannedVisit,
} from "@/lib/api/journeys";
import { fetchOutlets, outletsKey, type Outlet } from "@/lib/api/outlets";
import { refusalTexts } from "@/lib/api/refusals";
import { fetchUsers, identifying, usersKey } from "@/lib/api/users";
import { usePermissions } from "@/lib/auth/use-permissions";
import { useBusinessDay } from "@/lib/dates";

const CONTROL =
  "h-8 rounded-lg border border-input bg-background px-2 text-sm text-foreground"
  + " focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none";

/**
 * Generating a rep's round, reviewing it, and giving it to them (`JRN-03`, `JRN-04`).
 *
 * **A plan is an experiment until it is published.** That is the whole shape of this screen:
 * generation writes a draft, the draft is reviewed against what it could not do, and publishing is a
 * separate act that turns it into the rep's work. A published plan does not change — regenerating
 * makes a new one, which is why the list keeps them all.
 *
 * **What it could not do is as important as what it did.** A plan with 25 stops and four shops it
 * never reached is a different thing from a plan with 25 stops, and the difference is a supervisor's
 * to act on: a shortfall means there was not enough room, an exclusion means a shop is shut or has
 * no frequency. Only the second is fixable from here, which is why generation reports it and a
 * re-read does not.
 */
export function JourneyPlans() {
  const t = useTranslations("Plans");
  const { user } = useAuth();

  const accessToken = user?.access_token;
  const subject = user?.profile.sub;
  const enabled = Boolean(accessToken && subject);

  const [selected, setSelected] = useState<string | null>(null);
  const [generated, setGenerated] = useState<GeneratedPlan | null>(null);

  const plans = useQuery({
    enabled,
    queryKey: plansKey(subject ?? ""),
    queryFn: ({ signal }) => fetchPlans(accessToken!, undefined, signal),
  });

  if (plans.isError) {
    const error = plans.error;

    return (
      <p role="alert" className="text-sm text-destructive">
        {error instanceof ApiError && error.status === 403 ? t("forbidden") : t("failed")}
      </p>
    );
  }

  if (!plans.data) return <p className="text-sm text-muted-foreground">{t("loading")}</p>;

  const open = selected ?? plans.data[0]?.id ?? null;

  return (
    <div className="flex flex-col gap-8">
      <Generator
        onGenerated={(plan) => {
          setGenerated(plan);
          setSelected(plan.plan.id);
        }}
      />

      <PlanList plans={plans.data} open={open} onOpen={(id) => {
        setSelected(id);
        // The exclusions belong to one generation run, not to a plan — so opening a different plan
        // must not show the last run's shops as if they were this plan's.
        setGenerated((current) => (current?.plan.id === id ? current : null));
      }} />

      {open ? (
        <PlanDetail
          key={open}
          id={open}
          excluded={generated?.plan.id === open ? generated.excluded : []}
        />
      ) : null}
    </div>
  );
}

/** Rep, window, Generate. */
function Generator({ onGenerated }: { onGenerated: (plan: GeneratedPlan) => void }) {
  const t = useTranslations("Plans");
  const refusals = useTranslations("Refusals");
  const client = useQueryClient();
  const { user } = useAuth();
  const { has } = usePermissions();

  const accessToken = user?.access_token;
  const subject = user?.profile.sub;

  const [userId, setUserId] = useState("");
  const [from, setFrom] = useState("");
  const [to, setTo] = useState("");
  const [refused, setRefused] = useState<readonly string[]>([]);

  const users = useQuery({
    enabled: Boolean(accessToken && subject),
    retry: false,
    queryKey: usersKey(subject ?? ""),
    queryFn: ({ signal }) => fetchUsers(accessToken!, signal),
  });

  const generate = useMutation({
    mutationFn: () => generatePlan(accessToken!, userId, from, to),
    onSuccess: async (plan) => {
      setRefused([]);
      onGenerated(plan);
      await client.invalidateQueries({ queryKey: ["plans"] });
    },
    onError: (error) =>
      setRefused(
        error instanceof ApiError && error.problems.length > 0
          ? refusalTexts(refusals, error.problems)
          : [t("generateFailed")],
      ),
  });

  if (!has("journey:write")) return null;

  const problem = windowProblem(from, to);
  const reps = (users.data ?? []).filter((candidate) => candidate.isActive);

  return (
    <section className="flex flex-col gap-3 rounded-xl border border-border p-4">
      <div>
        <h2 className="text-sm font-semibold">{t("generateTitle")}</h2>
        <p className="text-sm text-muted-foreground">{t("generateIntro")}</p>
      </div>

      {refused.length > 0 ? (
        <ul role="alert" className="rounded-lg bg-destructive/10 px-3 py-2 text-sm text-destructive">
          {refused.map((message) => (
            <li key={message}>{message}</li>
          ))}
        </ul>
      ) : null}

      <div className="flex flex-wrap items-end gap-3">
        <label className="flex flex-col gap-1 text-sm">
          <span className="text-muted-foreground">{t("rep")}</span>
          <select
            className={CONTROL}
            value={userId}
            aria-label={t("rep")}
            onChange={(event) => setUserId(event.target.value)}
          >
            <option value="">{t("chooseRep")}</option>
            {reps.map((candidate) => (
              <option key={candidate.subjectId} value={candidate.subjectId}>
                {identifying(candidate)}
              </option>
            ))}
          </select>
        </label>

        <label className="flex flex-col gap-1 text-sm">
          <span className="text-muted-foreground">{t("from")}</span>
          <input
            type="date"
            className={CONTROL}
            value={from}
            aria-label={t("from")}
            onChange={(event) => setFrom(event.target.value)}
          />
        </label>

        <label className="flex flex-col gap-1 text-sm">
          <span className="text-muted-foreground">{t("to")}</span>
          <input
            type="date"
            className={`${CONTROL} ${problem ? "border-destructive" : ""}`}
            value={to}
            aria-invalid={problem !== null}
            aria-label={t("to")}
            onChange={(event) => setTo(event.target.value)}
          />
        </label>

        <Button
          type="button"
          size="sm"
          disabled={generate.isPending || userId === "" || from === "" || to === "" || problem !== null}
          onClick={() => generate.mutate()}
        >
          <CalendarRange className="size-4" />
          {generate.isPending ? t("generating") : t("generate")}
        </Button>

        {problem ? (
          <span className="pb-2 text-xs text-destructive">{t(`windowProblem.${problem}`)}</span>
        ) : null}
      </div>
    </section>
  );
}

/** Every run, newest window first — because a published plan is superseded rather than edited. */
function PlanList({
  plans,
  open,
  onOpen,
}: {
  plans: readonly JourneyPlan[];
  open: string | null;
  onOpen: (id: string) => void;
}) {
  const t = useTranslations("Plans");
  const day = useBusinessDay();

  if (plans.length === 0) {
    return <p className="text-sm text-muted-foreground">{t("noPlans")}</p>;
  }

  return (
    <section className="flex flex-col gap-2">
      <h2 className="text-sm font-semibold">{t("plansTitle")}</h2>

      <ul className="flex flex-col divide-y divide-border rounded-xl border border-border">
        {plans.map((plan) => (
          <li key={plan.id}>
            <button
              type="button"
              onClick={() => onOpen(plan.id)}
              aria-current={plan.id === open ? "true" : undefined}
              className={`flex w-full flex-wrap items-center gap-3 px-4 py-2.5 text-left text-sm ${
                plan.id === open ? "bg-muted/60" : ""
              }`}
            >
              <span className="min-w-40 font-medium">{plan.displayName ?? t("unknownRep")}</span>

              <span className="text-muted-foreground">
                {t("window", { from: day(plan.from), to: day(plan.to) })}
              </span>

              <span
                className={`rounded-full px-2 py-0.5 text-xs ${
                  plan.status === "Published"
                    ? "bg-primary/15 text-primary"
                    : "bg-muted text-muted-foreground"
                }`}
              >
                {t(`status.${plan.status}`)}
              </span>

              <span className="ml-auto flex gap-3 text-xs text-muted-foreground">
                <span>{t("visitCount", { count: plan.visitCount })}</span>
                {plan.shortfallCount > 0 ? (
                  <span className="text-destructive">
                    {t("shortfallCount", { count: plan.shortfallCount })}
                  </span>
                ) : null}
              </span>
            </button>
          </li>
        ))}
      </ul>
    </section>
  );
}

/** One plan: the week grid, what it fell short on, and the way to publish it. */
function PlanDetail({ id, excluded }: { id: string; excluded: readonly Exclusion[] }) {
  const t = useTranslations("Plans");
  const refusals = useTranslations("Refusals");
  const client = useQueryClient();
  const { user } = useAuth();
  const { has } = usePermissions();

  const accessToken = user?.access_token;
  const subject = user?.profile.sub;

  const [refused, setRefused] = useState<readonly string[]>([]);

  const detail = useQuery({
    enabled: Boolean(accessToken && subject),
    queryKey: planKey(subject ?? "", id),
    queryFn: ({ signal }) => fetchPlan(accessToken!, id, signal),
  });

  // Every shop the plan mentions, named in one request. `ids` exists for exactly this: a plan is
  // hundreds of visits over a few dozen shops, and one GET per shop is what the outlet picker's own
  // note says to replace with a bulk read when a screen like this arrives.
  const outletIds = useMemo(() => {
    const wanted = new Set<string>();

    for (const visit of detail.data?.visits ?? []) wanted.add(visit.outletId);
    for (const shortfall of detail.data?.shortfalls ?? []) wanted.add(shortfall.outletId);
    for (const exclusion of excluded) wanted.add(exclusion.outletId);

    return [...wanted].sort();
  }, [detail.data, excluded]);

  const outlets = useQuery({
    enabled: Boolean(accessToken && subject) && detail.data !== undefined && outletIds.length > 0,
    queryKey: outletsKey(subject ?? "", { ids: outletIds, pageSize: 200 }),
    queryFn: ({ signal }) =>
      fetchOutlets(accessToken!, { ids: outletIds, pageSize: 200 }, signal),
  });

  const publish = useMutation({
    mutationFn: () => publishPlan(accessToken!, id),
    onSuccess: async () => {
      setRefused([]);
      await client.invalidateQueries({ queryKey: ["plans"] });
      await client.invalidateQueries({ queryKey: ["plan"] });
    },
    onError: (error) =>
      setRefused(
        error instanceof ApiError && error.problems.length > 0
          ? refusalTexts(refusals, error.problems)
          : [t("publishFailed")],
      ),
  });

  if (detail.isError) {
    return (
      <p role="alert" className="text-sm text-destructive">
        {t("failed")}
      </p>
    );
  }

  if (!detail.data) return <p className="text-sm text-muted-foreground">{t("loading")}</p>;

  const named = new Map((outlets.data?.items ?? []).map((outlet: Outlet) => [outlet.id, outlet]));
  const name = (outletId: string) => named.get(outletId)?.name ?? t("unknownOutlet");

  const { plan, visits, shortfalls } = detail.data;

  return (
    <section className="flex flex-col gap-4">
      <header className="flex flex-wrap items-center justify-between gap-3">
        <h2 className="text-sm font-semibold">
          {t("planFor", { name: plan.displayName ?? t("unknownRep") })}
        </h2>

        {has("journey:write") && plan.status === "Draft" ? (
          <Button
            type="button"
            size="sm"
            disabled={publish.isPending}
            onClick={() => publish.mutate()}
          >
            {publish.isPending ? t("publishing") : t("publish")}
          </Button>
        ) : null}
      </header>

      {refused.length > 0 ? (
        <ul role="alert" className="rounded-lg bg-destructive/10 px-3 py-2 text-sm text-destructive">
          {refused.map((message) => (
            <li key={message}>{message}</li>
          ))}
        </ul>
      ) : null}

      {plan.status === "Published" ? (
        <p className="text-sm text-muted-foreground">{t("publishedNote")}</p>
      ) : null}

      <WeekGrid visits={visits} name={name} />

      {shortfalls.length > 0 ? (
        <section className="flex flex-col gap-2">
          <h3 className="text-sm font-semibold">{t("shortfallsTitle")}</h3>
          <p className="text-sm text-muted-foreground">{t("shortfallsIntro")}</p>

          <ul className="flex flex-col divide-y divide-border rounded-xl border border-border">
            {shortfalls.map((shortfall) => (
              <li
                key={shortfall.outletId}
                className="flex flex-wrap items-center gap-3 px-4 py-2 text-sm"
              >
                <span className="min-w-40 font-medium">{name(shortfall.outletId)}</span>
                <span className="text-muted-foreground">
                  {t("shortfall", { planned: shortfall.planned, required: shortfall.required })}
                </span>
              </li>
            ))}
          </ul>
        </section>
      ) : null}

      {excluded.length > 0 ? (
        <section className="flex flex-col gap-2">
          <h3 className="text-sm font-semibold">{t("excludedTitle")}</h3>
          {/* Only after a generation run. An exclusion is a fact about the inputs — a shut shop, or
              one nobody gave a frequency — so it is true until somebody fixes it and is not stored
              against the plan. This is the one moment it can be acted on. */}
          <p className="text-sm text-muted-foreground">{t("excludedIntro")}</p>

          <ul className="flex flex-col divide-y divide-border rounded-xl border border-border">
            {excluded.map((exclusion) => (
              <li
                key={exclusion.outletId}
                className="flex flex-wrap items-center gap-3 px-4 py-2 text-sm"
              >
                <span className="min-w-40 font-medium">{name(exclusion.outletId)}</span>
                <span className="text-muted-foreground">{t(`exclusion.${exclusion.reason}`)}</span>
              </li>
            ))}
          </ul>
        </section>
      ) : null}
    </section>
  );
}

/**
 * The plan as days, in the order they happen.
 *
 * Grouped by date rather than laid out as a fixed Mon–Fri week: a plan's window is whatever was
 * asked for, and a grid with seven columns would have to invent empty ones for a three-week window
 * or crop a Saturday somebody actually works. Days with nothing on them are simply absent, which is
 * also what makes the working calendar visible in the result.
 */
function WeekGrid({
  visits,
  name,
}: {
  visits: readonly PlannedVisit[];
  name: (outletId: string) => string;
}) {
  const t = useTranslations("Plans");
  const day = useBusinessDay();

  const days = useMemo(() => {
    const grouped = new Map<string, PlannedVisit[]>();

    for (const visit of visits) {
      grouped.set(visit.date, [...(grouped.get(visit.date) ?? []), visit]);
    }

    return [...grouped.entries()].sort(([a], [b]) => a.localeCompare(b));
  }, [visits]);

  if (days.length === 0) {
    // A plan with no calls is a real answer, not an error: nobody has a frequency, or the rep has no
    // working days in the window. The shortfalls and exclusions below say which.
    return <p className="text-sm text-muted-foreground">{t("noVisits")}</p>;
  }

  return (
    <div className="flex gap-3 overflow-x-auto pb-2">
      {days.map(([date, calls]) => (
        <section key={date} className="flex min-w-44 flex-col gap-2">
          <h3 className="flex items-baseline justify-between border-b border-border pb-1 text-xs">
            <span className="font-medium">{day(date)}</span>
            <span className="font-mono text-muted-foreground">{calls.length}</span>
          </h3>

          <ul className="flex flex-col gap-1.5">
            {calls.map((visit) => (
              <li
                key={visit.id}
                className={`rounded-lg border px-2 py-1.5 text-xs ${
                  visit.status === "NotVisited"
                    ? "border-dashed border-border text-muted-foreground"
                    : "border-border"
                }`}
              >
                <span className="block truncate">{name(visit.outletId)}</span>

                {visit.status === "NotVisited" ? (
                  // Still on the plan, and deliberately: BR-JRN-2 keeps a skipped call rather than
                  // deleting it, because a shop that was missed is a fact about the round.
                  <span className="mt-0.5 block truncate text-[11px] italic">
                    {visit.notVisitedReason ?? t("notVisited")}
                  </span>
                ) : null}

                {visit.source === "Unplanned" ? (
                  <span className="mt-0.5 block text-[11px] text-muted-foreground">
                    {t("unplanned")}
                  </span>
                ) : null}

                {visit.rescheduledFrom ? (
                  <span className="mt-0.5 block text-[11px] text-muted-foreground">
                    {t("movedFrom", { date: day(visit.rescheduledFrom) })}
                  </span>
                ) : null}
              </li>
            ))}
          </ul>
        </section>
      ))}
    </div>
  );
}
