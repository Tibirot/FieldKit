"use client";

import { useTranslations } from "next-intl";
import { useState } from "react";

import { CheckOut } from "@/components/field/check-out";
import { useSync } from "@/components/sync/sync-provider";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import type { LocalVisit, LocalVisitStep } from "@/lib/sync/db";
import { useLive } from "@/lib/sync/live";
import { outlet as heldOutlet } from "@/lib/sync/reference";
import { completeStep, visit as heldVisit, type VisitRefusal } from "@/lib/visits/local-visit";
import { minutesOnSite } from "@/lib/visits/summary";

/**
 * The visit a rep is working (`VIS-03`, `VIS-06`) — W9 slice 7.
 *
 * <b>The sequence is rendered from the visit, not from Configuration.</b> The steps were copied onto
 * the visit at check-in and this screen reads that copy — which is `BR-VIS-6`'s snapshot rule doing
 * its job: an admin editing the channel workflow at eleven must not change what a rep who checked in
 * at ten is required to do. It is also what makes the screen work with no signal, since there is no
 * second conversation to have about what is outstanding.
 *
 * <b>A step whose control does not exist yet is still workable.</b> Audit, Order, Survey, Photo and
 * Signature open sub-flows that arrive in W10, W11 and Phase 3. Until then they render as what they
 * already are — a labelled item on a checklist the rep works in the shop — and can be marked done.
 * The alternative is a mandatory step nobody can complete, which by `BR-VIS-3` is a rep who cannot
 * check out: the visit would be broken by a feature not being finished yet.
 *
 * What that costs is worth naming: a ticked `Audit` step records that the rep did an audit and
 * carries none of its numbers. `CapturedStep` sends the type and the label, so the back office can
 * see exactly which kind of step was ticked rather than inferring it.
 */
export function Visit({ visitId }: { visitId: string }) {
  const t = useTranslations("Field.visit");
  const { db } = useSync();

  // `undefined` still reading, `null` no such visit — the same three-state shape the check-in screen
  // needs, and for the same reason: the two are one value otherwise, and this screen would sit on
  // "Opening…" forever for an id that does not exist.
  const visit = useLive(async () => (await heldVisit(db, visitId)) ?? null, undefined, [db, visitId]);

  const shop = useLive(
    async () => (visit ? ((await heldOutlet(db, visit.outletId)) ?? null) : null),
    null,
    [db, visit?.outletId],
  );

  if (visit === undefined) return <Waiting message={t("opening")} />;

  if (visit === null) {
    return <Explained title={t("unknown.title")} body={t("unknown.body")} />;
  }

  const done = visit.steps.filter((step) => step.completedAtUtc !== null).length;

  return (
    <div className="flex flex-col gap-4">
      <header className="flex flex-col gap-1">
        <h1 className="text-lg font-medium">{shop?.name ?? t("unknownOutlet")}</h1>
        {shop ? <p className="font-mono text-xs text-muted-foreground">{shop.code}</p> : null}
      </header>

      {visit.status === "checkedOut" ? (
        /*
         * A sealed visit is a record, not a screen with the buttons disabled. `checkOut` already
         * refuses a step on it, so this is about not offering the action rather than about safety —
         * a rep who has finished should be reading what they did, not looking at greyed-out controls
         * wondering which one they missed.
         */
        <Sealed visit={visit} />
      ) : null}

      {visit.steps.length === 0 ? (
        // A channel nobody configured has no steps, and that is a legitimate workflow rather than a
        // missing one (`IVisitWorkflow` returns exactly this). The rep checks in and checks out.
        <p className="text-sm text-muted-foreground" role="status">
          {t("noSteps")}
        </p>
      ) : (
        <section className="flex flex-col gap-2">
          <p className="text-xs text-muted-foreground">
            {t("progress", { done, total: visit.steps.length })}
          </p>

          {/*
            Named, because there are now three lists on this screen and they say different things:
            the steps, the recap's optional-and-not-done, and check-out's mandatory-and-blocking
            (W9 slices 8 and 10). "List" three times tells a screen-reader user nothing about which
            one they have landed in — and it was ambiguous for the tests first, which is how the
            problem announced itself.
          */}
          <ol aria-label={t("stepsLabel")} className="flex flex-col gap-2">
            {visit.steps.map((step) => (
              <StepRow key={step.stepId} visit={visit} step={step} />
            ))}
          </ol>
        </section>
      )}

      {/* Check-out lives below the steps because that is the order the rep works in, and because
          what it refuses is about them (`BR-VIS-3`, W9 slice 8). */}
      {visit.status === "inProgress" ? <CheckOut visit={visit} /> : null}
    </div>
  );
}

/**
 * What a finished visit says about itself (`VIS-05`) — W9 slice 8.
 *
 * <b>Not the summary screen.</b> `VIS-09`'s recap is the thing a rep reads *before* checking out, and
 * it is slice 10. This is the three facts check-out itself produced, shown where the visit is.
 *
 * <b>Time on site is derived here, exactly as it is server-side.</b> Check-out minus check-in, never
 * stored — a stored copy is a second answer that can disagree with the first (`BR-VIS-5`).
 */
function Sealed({ visit }: { visit: LocalVisit }) {
  const t = useTranslations("Field.visit");

  return (
    <div className="flex flex-col gap-1 rounded-xl border border-border p-3" role="status">
      <p className="text-sm">{t("sealed")}</p>

      <dl className="flex flex-col gap-0.5 text-sm text-muted-foreground">
        {visit.outcome ? (
          <div className="flex gap-2">
            <dt>{t("outcomeLabel")}</dt>
            <dd>{t(`outcomes.${visit.outcome === "Productive" ? "Productive" : "NonProductive"}`)}</dd>
          </div>
        ) : null}

        {visit.outcomeReason ? (
          <div className="flex gap-2">
            <dt>{t("reasonLabel")}</dt>
            <dd>{visit.outcomeReason}</dd>
          </div>
        ) : null}

        {visit.checkedOutAtUtc ? (
          <div className="flex gap-2">
            <dt>{t("timeOnSiteLabel")}</dt>
            <dd>{t("minutes", { minutes: minutesOnSite(visit, new Date()) })}</dd>
          </div>
        ) : null}
      </dl>
    </div>
  );
}

function StepRow({ visit, step }: { visit: LocalVisit; step: LocalVisitStep }) {
  const t = useTranslations("Field.visit");
  const { db } = useSync();

  const [notes, setNotes] = useState("");
  const [working, setWorking] = useState(false);
  const [refused, setRefused] = useState<VisitRefusal | null>(null);

  const complete = async () => {
    setWorking(true);
    setRefused(null);

    const result = await completeStep(db, visit.id, step.stepId, { notes, now: new Date() });

    // Left set on success as well: the row is about to re-render from the live query as *done*, and
    // clearing it first would be a state nobody sees.
    setWorking(false);
    if (!result.ok) setRefused(result.refusal);
  };

  const finished = step.completedAtUtc !== null;
  const open = !finished && visit.status === "inProgress";

  return (
    <li className="flex flex-col gap-2 rounded-xl border border-border p-3">
      <div className="flex items-start justify-between gap-3">
        <div className="flex min-w-0 flex-col">
          {/*
            The admin's own words for this step, and the reason `label` travels at all. The type
            decides what the app *does*; the label decides what the rep reads, and a tenant that
            calls its audit "Planogram check" should see that on the phone.
          */}
          <span className="font-medium">{step.label}</span>
          <span className="text-xs text-muted-foreground">{t(`type.${typeKey(step.type)}`)}</span>
        </div>

        <div className="flex shrink-0 items-center gap-2">
          {/* Only *required* is worth a chip. Optional is the majority and marking it would give a
              rep a screen of badges to read past to find the two that matter (`BR-VIS-3`). */}
          {step.mandatory && !finished ? <Badge variant="outline">{t("required")}</Badge> : null}
          {finished ? <Badge variant="secondary">{t("done")}</Badge> : null}
        </div>
      </div>

      {/* What the rep wrote, kept visible after the step is done. A note step *is* its text, so a
          finished one showing only a tick would have swallowed the whole point of doing it. */}
      {finished && step.notes ? <p className="text-sm">{step.notes}</p> : null}

      {open && step.type === "Note" ? (
        <label className="flex flex-col gap-1 text-sm">
          <span className="text-muted-foreground">{t("noteLabel")}</span>
          <textarea
            className="min-h-20 rounded-xl border border-border bg-transparent p-3 text-sm"
            value={notes}
            onChange={(event) => setNotes(event.target.value)}
          />
        </label>
      ) : null}

      {refused ? (
        <p className="text-sm text-destructive" role="alert">
          {t(`refusal.${refused in REFUSAL_KEYS ? REFUSAL_KEYS[refused as ReachableRefusal] : "unexpected"}`)}
        </p>
      ) : null}

      {open ? (
        <Button
          variant="outline"
          size="sm"
          className="self-start"
          onClick={() => void complete()}
          disabled={working}
        >
          {step.type === "Note" ? t("saveNote") : t("markDone")}
        </Button>
      ) : null}
    </li>
  );
}

/**
 * ADR-0012's dotted codes, mapped to message keys — the same trick the check-in screen needs, and
 * for the same reason: next-intl reads a dot as a path into the catalogue, so a code used directly
 * as a key comes back as the raw code printed at the rep.
 *
 * Two of seven again, and a different two: `notInProgress` and `noteRequired` are what completing a
 * step can be refused for. The check-in pair cannot happen here and the check-out pair belongs to
 * slice 8.
 */
const REFUSAL_KEYS = {
  "visit.notInProgress": "notInProgress",
  "visit.step.notOpen": "notOpen",
  "visit.step.noteRequired": "noteRequired",
} as const satisfies Partial<Record<VisitRefusal, string>>;

type ReachableRefusal = keyof typeof REFUSAL_KEYS;

/**
 * The step type, as a message key.
 *
 * <b>A type this device does not recognise is named generically rather than dropped.</b> The type
 * arrives as a string from a server that may be newer than the app — a tenant configuring a step
 * type added after this device last updated is an ordinary consequence of offline-first — and a
 * screen that rendered nothing for it would leave a mandatory step with no label and a rep who
 * cannot check out.
 */
function typeKey(type: string): (typeof KNOWN_TYPES)[number] | "unknown" {
  return KNOWN_TYPES.find((known) => known === type) ?? "unknown";
}

/** `VisitStepType`, mirrored. The server sends the name rather than the number (ADR-0012 §1). */
const KNOWN_TYPES = ["Audit", "Order", "Survey", "Task", "Photo", "Note", "Signature"] as const;

function Waiting({ message }: { message: string }) {
  return (
    <p className="text-sm text-muted-foreground" role="status">
      {message}
    </p>
  );
}

function Explained({ title, body }: { title: string; body: string }) {
  return (
    <div className="flex flex-col gap-1" role="alert">
      <h1 className="text-lg font-medium">{title}</h1>
      <p className="text-sm text-muted-foreground">{body}</p>
    </div>
  );
}
