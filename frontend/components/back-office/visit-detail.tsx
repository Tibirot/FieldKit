"use client";

import { useQuery } from "@tanstack/react-query";
import { useTranslations } from "next-intl";

import { useAuth } from "@/components/auth-provider";
import { auditKey, fetchAudit, type Audit } from "@/lib/api/audits";
import { ApiError } from "@/lib/api/client";
import { fetchVisit, visitKey, type VisitStep } from "@/lib/api/visits";
import { useBusinessDay } from "@/lib/dates";

/**
 * One visit, as a supervisor reviews it (`VIS-10`, `AUD-09`) — W12 slice 5b.
 *
 * **Read-only, and the server agrees.** A checked-out visit is sealed (`BR-VIS-4`) and an audit is
 * append-only (`BR-AUD-6`); neither module has a write path a screen could offer. What is here is
 * the record, and the record is the point.
 *
 * **Two things it refuses to flatten.**
 *
 * A step that was never completed is shown as pending rather than dropped — a workflow of six steps
 * with two untouched is a different visit from one of four, and only the first tells a supervisor
 * what the rep skipped.
 *
 * A pillar that was **skipped** is labelled as such and never as 0%. `BR-AUD-2` renormalises it out
 * of the score rather than counting it against the shop; rendering a dash where a zero would go is
 * the same distinction the dashboard makes, at the one place a supervisor can act on it.
 */
export function VisitDetail({ visitId }: { visitId: string }) {
  const t = useTranslations("VisitDetail");
  const { user } = useAuth();
  const day = useBusinessDay();

  const accessToken = user?.access_token;
  const subject = user?.profile.sub;
  const enabled = Boolean(accessToken && subject);

  const detail = useQuery({
    enabled,
    queryKey: visitKey(subject ?? "", visitId),
    queryFn: ({ signal }) => fetchVisit(accessToken!, visitId, signal),
  });

  // Asked in parallel rather than after the visit: they are separate schemas behind separate
  // endpoints, and waiting would make the screen's latency the sum of two reads for no gain.
  const audit = useQuery({
    enabled,
    queryKey: auditKey(subject ?? "", visitId),
    queryFn: ({ signal }) => fetchAudit(accessToken!, visitId, signal),
  });

  if (detail.isError) {
    const error = detail.error;
    const missing = error instanceof ApiError && error.status === 404;

    return (
      <p role="alert" className="text-sm text-destructive">
        {missing ? t("missing") : error instanceof ApiError && error.status === 403 ? t("forbidden") : t("failed")}
      </p>
    );
  }

  if (!detail.data) {
    return <p className="text-sm text-muted-foreground">{t("loading")}</p>;
  }

  const { visit, steps } = detail.data;

  return (
    <div className="flex flex-col gap-6">
      <section className="flex flex-col gap-2">
        <h2 className="text-sm font-medium">{t("theVisit")}</h2>
        <dl className="grid gap-x-6 gap-y-2 rounded-xl border border-border p-4 sm:grid-cols-2">
          <Fact label={t("outcomeLabel")} value={t(`outcome.${visit.outcome ?? "Open"}`)} />
          <Fact label={t("checkedIn")} value={day(visit.checkedInAtUtc.slice(0, 10))} />
          <Fact
            label={t("timeOnSite")}
            value={visit.timeOnSiteSeconds === null ? "—" : t("minutes", { minutes: Math.round(visit.timeOnSiteSeconds / 60) })}
          />
          <Fact
            label={t("geofence")}
            value={visit.wasInsideGeofence ? t("atTheShop") : t("awayFromTheShop")}
          />
        </dl>

        {/*
         * The sentence the rep typed, given a line of its own rather than a cell. It is prose a
         * person wrote under time pressure, and it is the single most likely reason a supervisor
         * opened this screen — `BR-VIS-2` collects it precisely so somebody reads it.
         */}
        {!visit.wasInsideGeofence && (
          <p className="text-sm text-amber-600 dark:text-amber-500">
            {t("override", { reason: visit.geofenceOverrideReason ?? t("noReason") })}
          </p>
        )}

        {visit.outcome === "NonProductive" && visit.outcomeReason && (
          <p className="text-sm text-muted-foreground">
            {t("outcomeReason", { reason: visit.outcomeReason })}
          </p>
        )}
      </section>

      <Steps steps={steps} />

      {audit.data ? (
        <AuditPanel audit={audit.data} />
      ) : (
        // Null rather than an error: a visit with no audit is ordinary, and `fetchAudit` translates
        // the endpoint's 404 for exactly this sentence.
        <p className="text-sm text-muted-foreground">{t("noAudit")}</p>
      )}
    </div>
  );
}

/** The workflow as it was worked — including the steps that were not. */
function Steps({ steps }: { steps: VisitStep[] }) {
  const t = useTranslations("VisitDetail");

  if (steps.length === 0) {
    return <p className="text-sm text-muted-foreground">{t("noSteps")}</p>;
  }

  return (
    <section className="flex flex-col gap-2">
      <h2 className="text-sm font-medium">{t("steps")}</h2>
      <ul className="flex flex-col divide-y divide-border rounded-xl border border-border">
        {steps.map((step) => (
          <li key={step.id} className="flex flex-wrap items-baseline gap-x-3 gap-y-1 px-4 py-3">
            <span className="min-w-48 flex-1 text-sm">{step.label}</span>
            <span className="text-xs text-muted-foreground">{t(`stepType.${step.type}`)}</span>
            {step.mandatory && (
              <span className="text-xs text-muted-foreground">{t("mandatory")}</span>
            )}
            <span className="text-sm">{t(`stepStatus.${step.status}`)}</span>
            {step.notes && <p className="w-full text-xs text-muted-foreground">{step.notes}</p>}
          </li>
        ))}
      </ul>
    </section>
  );
}

/** The audit beneath the visit, and the score with its working shown. */
function AuditPanel({ audit }: { audit: Audit }) {
  const t = useTranslations("VisitDetail");

  return (
    <section className="flex flex-col gap-2">
      <h2 className="text-sm font-medium">{t("audit")}</h2>

      <div className="flex flex-col gap-1 rounded-xl border border-border p-4">
        <p className="text-sm text-muted-foreground">{t("score")}</p>
        <p className="text-2xl font-semibold tracking-tight tabular-nums">
          {audit.score === null ? "—" : `${audit.score.toFixed(2)}%`}
        </p>
        {/*
         * The weighting version, always. A score is only meaningful against the weights it was
         * computed with (`BR-AUD-8`), and two audits scored under different versions are not
         * comparable however similar the numbers look.
         */}
        <p className="text-xs text-muted-foreground">
          {t("weighting", { version: audit.weightSetVersion })}
        </p>
      </div>

      <ul className="flex flex-col divide-y divide-border rounded-xl border border-border">
        {audit.scoredPillars.map((pillar) => (
          <li key={pillar.pillar} className="flex flex-wrap items-baseline gap-x-3 gap-y-1 px-4 py-3">
            <span className="min-w-40 flex-1 text-sm">{t(`pillar.${pillar.pillar}`)}</span>
            <span className="text-sm font-medium tabular-nums">
              {/*
               * Skipped is not zero. `BR-AUD-2` renormalises an unmeasured pillar out of the score
               * rather than counting it against the shop, so a 0 here would both misstate the shelf
               * and disagree with the total beside it.
               */}
              {pillar.percentage === null ? t("notMeasured") : `${pillar.percentage.toFixed(2)}%`}
            </span>
            <span className="text-xs text-muted-foreground">
              {t("weight", { weight: pillar.weight })}
            </span>
          </li>
        ))}
      </ul>

      <p className="text-xs text-muted-foreground">
        {t("captured", {
          availability: audit.availability.length,
          facings: audit.facings.length,
          prices: audit.prices.length,
          answers: audit.answers.length,
          photos: audit.photos.length,
        })}
      </p>
    </section>
  );
}

function Fact({ label, value }: { label: string; value: string }) {
  return (
    <div className="flex flex-col">
      <dt className="text-xs text-muted-foreground">{label}</dt>
      <dd className="text-sm">{value}</dd>
    </div>
  );
}
