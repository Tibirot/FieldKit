"use client";

import { useFormatter, useTranslations } from "next-intl";

import { useSync } from "@/components/sync/sync-provider";
import { SyncBadge } from "@/components/sync/sync-badge";
import { Badge } from "@/components/ui/badge";
import { Link } from "@/i18n/navigation";
import { useLive } from "@/lib/sync/live";
import { today, todayOn, type Stop, type StopProgress } from "@/lib/visits/today";

/**
 * The day's stops, read from the device (`JRN-05`, `OFF-01`, `OFF-05`) — W9 slice 5.
 *
 * <b>The screen the field app opens on, and it answers with no signal.</b> Everything here comes
 * from the local store: the round from `ref_planned_visits`, the shops from `ref_outlets`, and what
 * the rep has already done from `visits`. A rep standing outside a shop with one bar of signal is
 * not waiting on a request to find out where they are supposed to be.
 *
 * <b>Live, not fetched once.</b> The list is a `liveQuery`, so checking out of a visit moves that
 * row to *worked* without a refresh, and a sync landing mid-morning brings in a plan the supervisor
 * published while the rep was driving.
 */
export function TodaysJourney({ now }: { now?: Date }) {
  const t = useTranslations("Field.journey");
  const format = useFormatter();
  const { db } = useSync();

  /*
   * The date is computed once per mount rather than read inside the query.
   *
   * A rep working past midnight is a real case — and one whose round silently emptied at 00:00
   * while they were mid-visit would be worse than one showing a stale date. The day rolls over when
   * the screen is opened again, which is when a rep is between shops rather than inside one.
   */
  const date = todayOn(now ?? new Date());

  const stops = useLive(() => today(db, date), [], [db, date]);

  return (
    <div className="flex flex-col gap-4">
      <header className="flex flex-col gap-1">
        <h1 className="text-lg font-medium">{t("title")}</h1>
        <p className="text-sm text-muted-foreground">
          {/*
            Midnight **UTC**, not local, and the `Z` is the whole of it.

            The app formats in a fixed zone (`i18n/request.ts`), so a `Date` built at *local*
            midnight is rendered as the previous day for every rep east of UTC — this heading said
            "March 16" for a round dated the 17th, on my own machine, and the test caught it. A
            business day is a date with no instant in it; pinning both ends to UTC is what makes the
            round trip through `Date` give back the day it started with.
          */}
          {format.dateTime(new Date(`${date}T00:00:00Z`), { dateStyle: "full" })}
        </p>
      </header>

      {stops.length === 0 ? (
        /*
         * One empty state, deliberately, where there are arguably two: "no plan on this device" and
         * "a plan with nothing today". A rep cannot act differently on them — both mean sync, or ask
         * a supervisor — and a screen that split them would be explaining its own data model.
         */
        <p className="text-sm text-muted-foreground" role="status">
          {t("empty")}
        </p>
      ) : (
        <ol className="flex flex-col gap-2">
          {stops.map((stop) => (
            <StopRow key={stop.plannedVisitId} stop={stop} />
          ))}
        </ol>
      )}
    </div>
  );
}

function StopRow({ stop }: { stop: Stop }) {
  const t = useTranslations("Field.journey");

  return (
    <li className="flex flex-col gap-1 rounded-xl border border-border p-3">
      <div className="flex items-start justify-between gap-3">
        <div className="flex min-w-0 flex-col">
          {/*
            The whole row is a link only when there is a shop to open (W9 slice 6). A stop whose
            outlet this device does not hold has nothing behind it — the check-in screen would only
            be able to say so a second time — so it stays a row and does not offer a tap that leads
            to a dead end.

            The planned call rides in the query so the visit names it. Without it a rep opening a
            stop from their round would capture an *unplanned* visit at the right shop, and the
            supervisor's coverage figure would count the call as still outstanding.
          */}
          <span className="truncate font-medium">
            {stop.outlet ? (
              <Link href={destinationOf(stop)}>{stop.outlet.name}</Link>
            ) : (
              /*
                A shop this device does not hold still gets a row — the call is real and a supervisor
                would ask about it. What it cannot have is a name, so it says so rather than
                rendering an id at a rep as if it were one.
              */
              t("unknownOutlet")
            )}
          </span>
          {stop.outlet ? (
            <span className="font-mono text-xs text-muted-foreground">{stop.outlet.code}</span>
          ) : null}
        </div>

        <div className="flex shrink-0 items-center gap-2">
          {/*
            The badge answers "has this reached the back office", and a stop nobody has dealt with
            has not asked the question yet. Two things can ask it, and both are keyed by the subject
            they were queued under: a visit by its own id, and a not-visited report by the id of the
            call it annotates (W9 slice 9).
          */}
          {stop.visit ? <SyncBadge subjectId={stop.visit.id} /> : null}
          {!stop.visit && stop.reportedHere ? <SyncBadge subjectId={stop.plannedVisitId} /> : null}
          <Badge variant={variantOf(stop.progress)}>{t(`progress.${stop.progress}`)}</Badge>
        </div>
      </div>

      {/* The rep's own sentence, kept next to the stop it explains. Without it "not visited" is a
          gap nobody can act on — which is why `JRN-06` refuses the annotation without one. */}
      {stop.progress === "notVisited" && stop.notVisitedReason ? (
        <p className="text-sm text-muted-foreground">{stop.notVisitedReason}</p>
      ) : null}
    </li>
  );
}

/**
 * Where tapping a stop goes.
 *
 * <b>A stop the rep is standing in goes to the visit, not back to check-in</b> (W9 slice 7). Without
 * this, a rep who navigated away from a visit — to look at the round, or because the phone locked —
 * could not get back into it: the check-in screen would correctly refuse to start a second one and
 * then have nothing to offer. That is a visit stranded on the device by a routing decision, which is
 * the opposite of what the local store is for.
 *
 * A *finished* visit still goes to check-in, and that is deliberate rather than an omission: the
 * sealed visit is a record, and what a rep at that shop wants next is an unplanned second call
 * (`JRN-06`), not the read-only page.
 */
function destinationOf(stop: Stop): string {
  if (stop.visit?.status === "inProgress") return `/field/visits/${stop.visit.id}`;

  return `/field/outlets/${stop.outletId}?call=${stop.plannedVisitId}`;
}

/**
 * <b>Only one state is coloured, and it is the one that is *not* an achievement.</b>
 *
 * A row per shop with four colours is a wall a rep learns to stop reading. What they are scanning
 * for is where to go next, so `working` is the one that stands out; `worked` and `notVisited` are
 * both "dealt with", and `todo` is the majority for most of the day and therefore the quietest
 * thing on the screen.
 */
function variantOf(progress: StopProgress): "default" | "secondary" | "outline" {
  if (progress === "working") return "default";
  if (progress === "todo") return "outline";

  return "secondary";
}
