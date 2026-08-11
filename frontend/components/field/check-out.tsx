"use client";

import { useTranslations } from "next-intl";
import { useState } from "react";

import { useSync } from "@/components/sync/sync-provider";
import { Button } from "@/components/ui/button";
import { useRouter } from "@/i18n/navigation";
import type { LocalVisit } from "@/lib/sync/db";
import { checkOut, openMandatorySteps, type VisitRefusal } from "@/lib/visits/local-visit";
import { currentPosition } from "@/lib/visits/position";

/**
 * Ending a visit, decided on the device (`VIS-04`, `VIS-05`) — W9 slice 8.
 *
 * <b>The opposite temperament from check-in, and deliberately so.</b> `BR-VIS-2` refuses to keep a
 * rep *out* of a shop; `BR-VIS-3` refuses to file a visit as done while the work it was configured
 * for is not. Refusing costs nothing here — the rep is still standing in the shop — provided the
 * refusal *names the steps*, which is why they are listed rather than counted.
 *
 * <b>What is outstanding is on screen the whole time, not only when the rep tries to leave.</b> Being
 * told at the door is the version of `BR-VIS-3` that sends someone back into a shop they have walked
 * out of, and that is the failure this list exists to prevent. The button stays live anyway: a rep
 * who taps it gets the names, which is more use than a disabled control with no explanation.
 *
 * <b>The position is captured, never judged.</b> Two points are a cheap counter against a visit that
 * was never really worked. A geofence rule at this end would prompt a rep who has done the job and
 * walked to the car — the flag-on-ordinary-work failure `BR-VIS-2`'s assumption already warns about.
 */
export function CheckOut({ visit }: { visit: LocalVisit }) {
  const t = useTranslations("Field.checkOut");
  const router = useRouter();
  const { db } = useSync();

  const [outcome, setOutcome] = useState<"Productive" | "NonProductive">("Productive");
  const [reason, setReason] = useState("");
  const [sealing, setSealing] = useState(false);
  const [refused, setRefused] = useState<VisitRefusal | null>(null);

  const outstanding = openMandatorySteps(visit);

  const seal = async () => {
    setSealing(true);
    setRefused(null);

    /*
     * The fix is taken *now*, at the tap, which is the opposite of check-in — and the difference is
     * that nothing here is shown to the rep before it is stored. Check-in has to show a verdict and
     * then honour it; this only records where the phone was when the visit ended, so the most
     * truthful moment is the last one.
     *
     * A short timeout, and `null` when it expires. `BR-VIS-3` is the only thing allowed to keep a rep
     * in a shop; a satellite is not.
     */
    const fix = await currentPosition({ timeoutMs: 5_000 });

    const result = await checkOut(db, visit.id, {
      outcome,
      reason,
      at: fix.ok ? fix.at : null,
      now: new Date(),
    });

    if (!result.ok) {
      setSealing(false);
      setRefused(result.refusal);

      return;
    }

    /*
     * Back to the round rather than staying on the sealed visit. The stop the rep just worked reads
     * *Worked* with a *Not synced* badge beside it, which answers the two questions they have on
     * walking out — is it done, and does the back office have it — in the place they are going next.
     */
    router.replace("/field");
  };

  return (
    <section className="flex flex-col gap-3 rounded-xl border border-border p-3">
      <h2 className="font-medium">{t("title")}</h2>

      {outstanding.length > 0 ? (
        <div className="flex flex-col gap-1" role="status">
          <p className="text-sm">{t("outstanding", { count: outstanding.length })}</p>
          <ul className="list-inside list-disc text-sm text-muted-foreground">
            {outstanding.map((step) => (
              <li key={step.stepId}>{step.label}</li>
            ))}
          </ul>
        </div>
      ) : null}

      <fieldset className="flex flex-col gap-2">
        <legend className="text-sm font-medium">{t("outcome")}</legend>

        {/*
          Radios rather than two buttons, and *Productive* preselected. The outcome is a fact about
          the call that the rep is reporting, not an action they are taking, and the ordinary call is
          productive — a screen that made them choose every time would collect the first option by
          reflex anyway, with an extra tap.
        */}
        {(["Productive", "NonProductive"] as const).map((option) => (
          <label key={option} className="flex items-center gap-2 text-sm">
            <input
              type="radio"
              name="outcome"
              value={option}
              checked={outcome === option}
              onChange={() => setOutcome(option)}
            />
            <span>{t(`outcomes.${option}`)}</span>
          </label>
        ))}
      </fieldset>

      {/* Only for a call that produced nothing. "Why did nothing come of it" is the reporting fact
          `VIS-05` is after; a reason attached to a productive call is a box nobody reads. */}
      {outcome === "NonProductive" ? (
        <label className="flex flex-col gap-1 text-sm">
          <span className="font-medium">{t("reason.label")}</span>
          <span className="text-muted-foreground">{t("reason.help")}</span>
          <textarea
            className="min-h-20 rounded-xl border border-border bg-transparent p-3 text-sm"
            value={reason}
            onChange={(event) => setReason(event.target.value)}
          />
        </label>
      ) : null}

      {refused ? (
        <p className="text-sm text-destructive" role="alert">
          {t(`refusal.${refused in REFUSAL_KEYS ? REFUSAL_KEYS[refused as ReachableRefusal] : "unexpected"}`)}
        </p>
      ) : null}

      <Button onClick={() => void seal()} disabled={sealing}>
        {sealing ? t("sealing") : t("action")}
      </Button>
    </section>
  );
}

/**
 * ADR-0012's dotted codes as message keys — the third screen to need this, and the third distinct
 * pair. next-intl reads a dot as a path into the catalogue, so a code used directly comes back as
 * the raw code printed at the rep.
 *
 * `mandatoryStepsOpen` has no message of its own: the list above the button already names the steps,
 * and a refusal repeating "you have steps open" under a list of the steps that are open would be the
 * screen talking to itself. It maps to the same sentence as the generic case, which points at the
 * list rather than restating it.
 */
const REFUSAL_KEYS = {
  "visit.notInProgress": "notInProgress",
  "visit.checkOut.mandatoryStepsOpen": "mandatoryStepsOpen",
  "visit.checkOut.reasonRequired": "reasonRequired",
} as const satisfies Partial<Record<VisitRefusal, string>>;

type ReachableRefusal = keyof typeof REFUSAL_KEYS;
