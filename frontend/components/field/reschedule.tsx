"use client";

import { useTranslations } from "next-intl";
import { useId, useState } from "react";

import { useSync } from "@/components/sync/sync-provider";
import { Button } from "@/components/ui/button";
import { useRouter } from "@/i18n/navigation";
import { useLive } from "@/lib/sync/live";
import {
  movable,
  queuedReschedule,
  reschedule,
  type RescheduleRefusal,
} from "@/lib/visits/reschedule";

/**
 * Moving a call to another day inside its cycle (`JRN-06`, `BR-JRN-4`) — W12 F2b.
 *
 * <b>Beside *Can't make this call?*, and that placement is the finding's own argument.</b> The
 * regression's cost statement for F2 is that a rep who cannot work a shop today has only
 * *not-visited* — which records a **miss** against coverage rather than a **move**. Those are the
 * two things that can be true at a closed door, so they belong side by side where the rep can see
 * that the second exists.
 *
 * It is deliberately not on the round. `NotVisited` argues that putting *skip* beside every call of
 * the day makes it a one-tap option and a different product; the same is true of *move it*, and more
 * so — a rep shuffling their week from an armchair is planning, which is a supervisor's screen.
 *
 * <b>Nothing here knows what a cycle is.</b> The days it offers are `movableFrom`/`movableTo` from
 * the round (F2a), computed server-side by the same function the refusal reads. This component puts
 * them on an `<input type="date">` as `min` and `max` and compares two strings.
 */
export function Reschedule({ plannedVisitId }: { plannedVisitId: string }) {
  const t = useTranslations("Field.reschedule");
  const router = useRouter();
  const { db } = useSync();

  const call = useLive(
    async () => (await db.plannedVisits.get(plannedVisitId)) ?? null,
    undefined,
    [db, plannedVisitId],
  );

  const queued = useLive(
    async () => (await queuedReschedule(db, plannedVisitId)) ?? null,
    undefined,
    [db, plannedVisitId],
  );

  /*
   * `useId` rather than a constant, because a shop screen is not guaranteed to hold one of these
   * forever and a duplicate `id` points one field's description at another's — silently, and only
   * for the rep using a screen reader.
   */
  const field = useId();
  const help = useId();

  const [open, setOpen] = useState(false);
  const [date, setDate] = useState("");
  const [saving, setSaving] = useState(false);
  const [refused, setRefused] = useState<RescheduleRefusal | null>(null);

  const move = async () => {
    if (!call) return;

    setSaving(true);
    setRefused(null);

    const result = await reschedule(db, call, date);

    if (!result.ok) {
      setSaving(false);
      setRefused(result.refusal);

      return;
    }

    // Back to the round, where the stop now reads *moved to …* with a badge saying the back office
    // has not heard yet. The call stays on today until the pull brings back a round that agrees —
    // the device never rewrites `ref_planned_visits`.
    router.replace("/field");
  };

  // Undefined is "still reading", null is "this device does not hold the call" — a real state, since
  // a rep can reach a shop screen for a call that arrived on another device.
  if (call === undefined || queued === undefined) return null;

  const window = call === null ? undefined : movable(call);

  /*
   * <b>No window, no offer.</b> An unplanned call belongs to no cycle and can never be moved, and a
   * call held from before local store version 21 has not been told its window yet. Both answer null,
   * and in both cases a button here would be a button the server refuses.
   */
  if (!window) return null;

  if (queued !== null) {
    /*
     * Already moved from this device and still on its way. Shown rather than hidden, because the day
     * the rep chose is the thing they will want to check — and because a screen that simply offered
     * the picker again would invite a second move from a day they have never seen the call on.
     */
    return (
      <section className="flex flex-col gap-1 rounded-xl border border-border p-3" role="status">
        <p className="text-sm font-medium">
          {queued.failed ? t("failed") : t("moved", { date: queued.date })}
        </p>
        <p className="text-sm text-muted-foreground">{t("stillHere")}</p>
      </section>
    );
  }

  if (!open) {
    return (
      <Button variant="outline" size="sm" className="self-start" onClick={() => setOpen(true)}>
        {t("open")}
      </Button>
    );
  }

  return (
    <section className="flex flex-col gap-3 rounded-xl border border-border p-3">
      <div className="flex flex-col gap-1">
        <h2 className="font-medium">{t("title")}</h2>
        <p className="text-sm text-muted-foreground">{t("intro")}</p>
      </div>

      <div className="flex flex-col gap-1 text-sm">
        <label className="font-medium" htmlFor={field}>
          {t("date.label")}
        </label>

        {/* Described-by rather than inside the label, so the accessible name stays *Which day?* — a
            label that swallowed this sentence would have a screen reader announce the whole rule
            every time focus entered the field. */}
        <span id={help} className="text-muted-foreground">
          {t("date.help", { from: window.from, to: window.to })}
        </span>

        {/* The range `BR-JRN-4` allows, straight from the round. `min`/`max` narrow the picker for a
            rep who taps; `reschedule` re-checks for one who types, which some browsers still let
            them do. */}
        <input
          id={field}
          type="date"
          aria-describedby={help}
          className="rounded-xl border border-border bg-transparent p-3 text-sm"
          value={date}
          min={window.from}
          max={window.to}
          onChange={(event) => setDate(event.target.value)}
        />
      </div>

      {refused ? (
        <p className="text-sm text-destructive" role="alert">
          {t(`refusal.${REFUSAL_KEYS[refused]}`)}
        </p>
      ) : null}

      <div className="flex flex-wrap gap-2">
        <Button
          onClick={() => void move()}
          /*
           * Disabled on the empty date and on the day the call is already on, rather than refused.
           *
           * The server treats a move to the current day as a no-op and answers success, so a
           * refusal code for it would be the device inventing vocabulary the protocol does not
           * have — and the rep would get a sync badge and a *moved* line for a call that never
           * moved. Nothing to say, so nothing to press.
           */
          disabled={saving || date === "" || date === call?.date}
        >
          {saving ? t("saving") : t("action")}
        </Button>
        <Button variant="outline" onClick={() => setOpen(false)} disabled={saving}>
          {t("cancel")}
        </Button>
      </div>
    </section>
  );
}

/** ADR-0012's dotted codes as message keys — next-intl reads a dot as a path into the catalogue. */
const REFUSAL_KEYS = {
  "journey.visit.outsideCycle": "outsideCycle",
  "journey.visit.alreadyReported": "alreadyReported",
} as const satisfies Record<RescheduleRefusal, string>;
