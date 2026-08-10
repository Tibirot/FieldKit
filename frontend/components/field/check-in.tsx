"use client";

import { useTranslations } from "next-intl";
import { useEffect, useState } from "react";

import { useSync } from "@/components/sync/sync-provider";
import { Button } from "@/components/ui/button";
import { useRouter } from "@/i18n/navigation";
import type { ReferenceOutlet } from "@/lib/sync/db";
import { useLive } from "@/lib/sync/live";
import { outlet as heldOutlet, workflowFor } from "@/lib/sync/reference";
import { assess, type GeofenceAssessment } from "@/lib/visits/geofencing";
import { checkIn, inProgress, type VisitRefusal } from "@/lib/visits/local-visit";
import { currentPosition, type PositionOutcome } from "@/lib/visits/position";

/**
 * Starting a visit, decided entirely on the device (`VIS-01`, `VIS-02`) — W9 slice 6.
 *
 * <b>The verdict shown is the verdict stored.</b> The fix is taken when the screen opens, and the
 * assessment the rep is reading is the one written to the visit — this screen does not re-measure at
 * the tap. Re-measuring sounds more accurate and is worse: a rep shown *inside* and recorded
 * *outside* has been given a reason box they never saw, and a supervisor an exception the rep would
 * deny. The refresh button is there so a rep who has walked can ask again, deliberately.
 *
 * <b>Nothing here blocks (`BR-VIS-2`).</b> Outside the fence, no fix at all, an unplaced shop — the
 * visit still starts. The strongest thing this screen does is require a sentence, and only when the
 * assessment actually asks for one.
 *
 * <b>The presence policy is read, not assumed.</b> A channel configured as remote-capable makes the
 * whole question moot, and that answer comes from the workflow this device pulled — which is also
 * why a channel with no workflow held means "presence expected" rather than "no opinion".
 */
export function CheckIn({
  outletId,
  plannedVisitId,
}: {
  outletId: string;
  plannedVisitId?: string;
}) {
  const t = useTranslations("Field.checkIn");
  const router = useRouter();
  const { db } = useSync();

  /*
   * `undefined` means *still reading*, `null` means *read, and there is none*.
   *
   * `useLive` has no loading signal of its own — it returns the initial value until the first query
   * resolves — so a query that answered `undefined` for "not held" would be saying the same thing as
   * one that has not answered yet. The screen waits on the first and has something to say about the
   * second, so collapsing them means a shop this device does not hold sits on "Opening this shop…"
   * forever. Normalising inside the query is what makes the three states three.
   */
  const outlet = useLive(async () => (await heldOutlet(db, outletId)) ?? null, undefined, [
    db,
    outletId,
  ]);

  const open = useLive(async () => (await inProgress(db)) ?? null, undefined, [db]);

  const workflow = useLive(
    async () => (outlet ? ((await workflowFor(db, outlet.channelId)) ?? null) : null),
    null,
    [db, outlet?.channelId],
  );

  /*
   * The same three states again, for the same reason: a screen that treated "locating" as "no fix"
   * would flash the override reason box at every rep for the second or two the GPS takes, and asking
   * someone to justify where they are before the phone has decided teaches them to type anything.
   */
  const [position, setPosition] = useState<PositionOutcome | undefined>();
  const [attempt, setAttempt] = useState(0);

  useEffect(() => {
    let cancelled = false;

    void (async () => {
      const outcome = await currentPosition();
      if (!cancelled) setPosition(outcome);
    })();

    return () => {
      cancelled = true;
    };
  }, [attempt]);

  const [reason, setReason] = useState("");
  const [starting, setStarting] = useState(false);
  const [refused, setRefused] = useState<VisitRefusal | null>(null);

  if (outlet === undefined || open === undefined) {
    return <Waiting message={t("opening")} />;
  }

  if (outlet === null) {
    // A call at a shop that has left this rep's territory reaches the journey as a stop with no
    // outlet, and tapping it lands here. There is no position, no radius and no channel to work
    // with, so the visit is not started rather than started against guesses.
    return <Explained title={t("unknownOutlet.title")} body={t("unknownOutlet.body")} />;
  }

  if (open !== null) {
    // `BR-VIS-1`, said before the button rather than after it. One rep, one shop, one visit — and
    // the useful answer distinguishes "you are already in this shop" from "you are in another one".
    const where = open.outletId === outletId ? "already" : "elsewhere";

    return <Explained title={t(`${where}.title`)} body={t(`${where}.body`)} />;
  }

  const assessment = position && assessmentOf(outlet, workflow?.presenceExpected ?? true, position);

  const start = async () => {
    setStarting(true);
    setRefused(null);

    const result = await checkIn(db, {
      outlet,
      workflow: workflow ?? undefined,
      at: position?.ok ? position.at : null,
      plannedVisitId: plannedVisitId ?? null,
      overrideReason: reason,
      now: new Date(),
    });

    if (!result.ok) {
      setStarting(false);
      setRefused(result.refusal);

      return;
    }

    /*
     * Back to the round, where the stop now reads *In the shop*.
     *
     * The visit screen this will eventually open is slice 7, and routing to a stub would be the
     * worse lie: the journey genuinely reflects the check-in, live, because the stop reads the same
     * store this just wrote to.
     */
    router.replace("/field");
  };

  return (
    <div className="flex flex-col gap-4">
      <header className="flex flex-col gap-1">
        <h1 className="text-lg font-medium">{outlet.name}</h1>
        <p className="font-mono text-xs text-muted-foreground">{outlet.code}</p>
      </header>

      <Located
        assessment={assessment}
        position={position}
        radiusMetres={outlet.radiusMetres}
        onRefresh={() => {
          setPosition(undefined);
          setAttempt((previous) => previous + 1);
        }}
      />

      {assessment?.reasonRequired ? (
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

      <Button
        onClick={() => void start()}
        // Disabled only while a fix is outstanding or a check-in is already going — never because
        // the rep is outside the fence, which is the refusal `BR-VIS-2` explicitly does not make.
        disabled={starting || position === undefined}
      >
        {starting ? t("starting") : t("action")}
      </Button>
    </div>
  );
}

/**
 * ADR-0012's dotted codes, mapped to message keys.
 *
 * <b>A map rather than `t(\`refusal.${code}\`)`</b>, because next-intl reads a dot as a path into
 * the catalogue: a key spelled `visit.checkIn.alreadyInProgress` is looked up as three levels of
 * nesting and comes back as the raw key printed at the rep. The codes are the server's vocabulary
 * and stay that; only their spelling in the catalogue is this file's business.
 *
 * Two entries out of seven refusals, and that is the point — the other five belong to steps and
 * check-out, which are their own screens. `unexpected` catches a refusal that grows into this path
 * later, so the worst case is a vague sentence rather than a key.
 */
const REFUSAL_KEYS = {
  "visit.checkIn.alreadyInProgress": "alreadyInProgress",
  "visit.checkIn.overrideReasonRequired": "overrideReasonRequired",
} as const satisfies Partial<Record<VisitRefusal, string>>;

type ReachableRefusal = keyof typeof REFUSAL_KEYS;

/**
 * What the device makes of where it is, in one banner.
 *
 * Five sentences, because a rep can act differently on each: inside (nothing to do), outside (say
 * why — and how far tells them whether that is worth arguing with), no fix (say why, and the phone
 * is the thing to fix), an unplaced shop (nothing to explain; the gap is the back office's), and a
 * channel that does not expect presence at all.
 */
function Located({
  assessment,
  position,
  radiusMetres,
  onRefresh,
}: {
  assessment: GeofenceAssessment | undefined;
  position: PositionOutcome | undefined;
  radiusMetres: number;
  onRefresh: () => void;
}) {
  const t = useTranslations("Field.checkIn");

  if (assessment === undefined || position === undefined) {
    return (
      <p className="rounded-xl border border-border p-3 text-sm text-muted-foreground" role="status">
        {t("locating")}
      </p>
    );
  }

  return (
    <div className="flex flex-col gap-2 rounded-xl border border-border p-3" role="status">
      <p className="text-sm">{sentence(t, assessment, position, radiusMetres)}</p>

      {position.ok && position.accuracyMetres !== null ? (
        // The number that decides whether "twelve metres outside" is a rep in the car park or a
        // phone guessing. Shown, never stored — see `PositionOutcome`.
        <p className="text-xs text-muted-foreground">
          {t("accuracy", { metres: Math.round(position.accuracyMetres) })}
        </p>
      ) : null}

      <Button variant="outline" size="sm" className="self-start" onClick={onRefresh}>
        {t("refresh")}
      </Button>
    </div>
  );
}

function sentence(
  t: ReturnType<typeof useTranslations<"Field.checkIn">>,
  assessment: GeofenceAssessment,
  position: PositionOutcome,
  radiusMetres: number,
): string {
  if (!position.ok) return t(`problem.${position.problem}`);

  // A fix, and no distance: either the shop has no coordinates or the channel does not expect the
  // rep to be at one. `assess` returns the same shape for both because it refuses a reason for
  // both; the sentences differ because what the rep should think about them does.
  if (assessment.distanceMetres === null) {
    return assessment.reasonRequired ? t("unplaced") : t("remote");
  }

  const metres = Math.round(assessment.distanceMetres);

  return assessment.inside ? t("inside", { metres }) : t("outside", { metres, radius: radiusMetres });
}

/**
 * The geofence, assessed from exactly what `checkIn` will be given.
 *
 * The same function with the same arguments, which is what makes "the verdict shown is the verdict
 * stored" true rather than aspirational: `assess` is pure, so running it twice over one position
 * cannot disagree with itself.
 */
function assessmentOf(
  outlet: ReferenceOutlet,
  presenceExpected: boolean,
  position: PositionOutcome,
): GeofenceAssessment {
  const placed =
    outlet.latitude === null || outlet.longitude === null
      ? null
      : { latitude: outlet.latitude, longitude: outlet.longitude };

  return assess(position.ok ? position.at : null, placed, outlet.radiusMetres, presenceExpected);
}

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
