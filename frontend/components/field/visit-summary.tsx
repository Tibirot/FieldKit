"use client";

import { useTranslations } from "next-intl";

import type { LocalVisit } from "@/lib/sync/db";
import { minutesOnSite, unfinished } from "@/lib/visits/summary";

/**
 * What the rep is about to file (`VIS-09`) — W9 slice 10.
 *
 * <b>Inline above the outcome, not an interstitial.</b> The requirement says "recap before
 * check-out", and the obvious reading is a confirm screen between the button and the seal. That
 * would tax every visit of every day with a tap, to catch a mistake on a few of them. The
 * information is what matters, not the ceremony — so it sits where the rep is already looking when
 * they choose an outcome.
 *
 * <b>It deliberately does not repeat the step list above it.</b> Three of the four things here are
 * not on the screen otherwise, and the fourth is the reason the other three are worth reading:
 *
 * - **Optional steps still open.** `BR-VIS-3` gates check-out on *mandatory* steps only, so the
 *   check-out panel names those and stops. An optional step nobody did is the one thing a rep can
 *   still act on and nothing currently tells them about.
 * - **Time in the shop**, which is otherwise only visible after the visit is sealed.
 * - **What the rep wrote**, gathered rather than scattered under the steps that hold it.
 * - **That check-out is final.** A visit seals and queues; nothing edits it afterwards, on the
 *   device or on the server. That is worth one sentence at the moment it becomes true.
 */
export function VisitSummary({ visit, now }: { visit: LocalVisit; now?: Date }) {
  const t = useTranslations("Field.summary");

  const open = unfinished(visit);
  const notes = visit.steps.filter((step) => step.completedAtUtc !== null && step.notes);

  /*
   * Computed at render, and it moves when anything re-renders this screen rather than ticking.
   *
   * A live clock counting up in a rep's face is pressure, and the number is not a target — it is a
   * fact `BR-VIS-5` derives afterwards. What it is *for* here is catching the visit somebody left
   * open in their pocket since the morning, and that reads the same whether it updates every second
   * or every tap.
   */
  const minutes = minutesOnSite(visit, now ?? new Date());

  return (
    <section className="flex flex-col gap-2 rounded-xl border border-border p-3">
      <h2 className="font-medium">{t("title")}</h2>

      <dl className="flex flex-col gap-1 text-sm">
        <Fact label={t("timeOnSite")} value={t("minutes", { minutes })} />
        <Fact
          label={t("steps")}
          value={t("stepsDone", {
            done: visit.steps.filter((step) => step.completedAtUtc !== null).length,
            total: visit.steps.length,
          })}
        />
      </dl>

      {open.length > 0 ? (
        <div className="flex flex-col gap-1">
          {/*
            Optional only — the mandatory ones are the check-out panel's business, and naming them
            twice would make the two lists look like a disagreement.
          */}
          <p className="text-sm">{t("optionalOpen", { count: open.length })}</p>
          <ul className="list-inside list-disc text-sm text-muted-foreground">
            {open.map((step) => (
              <li key={step.stepId}>{step.label}</li>
            ))}
          </ul>
        </div>
      ) : null}

      {notes.length > 0 ? (
        <div className="flex flex-col gap-1">
          <p className="text-sm">{t("notes")}</p>
          <ul className="flex flex-col gap-1 text-sm text-muted-foreground">
            {notes.map((step) => (
              <li key={step.stepId}>
                <span className="font-medium">{step.label}: </span>
                {step.notes}
              </li>
            ))}
          </ul>
        </div>
      ) : null}

      {/* Last, and phrased as a fact rather than a warning. A rep checking out has done the work;
          this is the one thing about it they cannot undo. */}
      <p className="text-xs text-muted-foreground">{t("final")}</p>
    </section>
  );
}

function Fact({ label, value }: { label: string; value: string }) {
  return (
    <div className="flex gap-2">
      <dt className="text-muted-foreground">{label}</dt>
      <dd>{value}</dd>
    </div>
  );
}
