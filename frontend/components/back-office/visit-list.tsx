"use client";

import { useQuery } from "@tanstack/react-query";
import { useTranslations } from "next-intl";
import { useState } from "react";

import { useAuth } from "@/components/auth-provider";
import { Link } from "@/i18n/navigation";
import { ApiError } from "@/lib/api/client";
import { fetchOutlets, outletsKey, type Outlet } from "@/lib/api/outlets";
import { fetchUsers, identifying, usersKey } from "@/lib/api/users";
import { fetchVisits, visitsKey } from "@/lib/api/visits";
import { useBusinessDay } from "@/lib/dates";

const CONTROL =
  "h-8 rounded-lg border border-input bg-background px-2 text-sm text-foreground"
  + " focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none";

/**
 * What the field recorded, as a supervisor reads it (`VIS-10`) — W12 slice 5a.
 *
 * **Read-only, and that is a rule rather than a scope cut.** A checked-out visit is sealed
 * (`BR-VIS-4`): every write path in the module refuses it, which is what makes a visit safe to push
 * through sync with no conflict story. A screen offering an edit would be offering a door the server
 * has bolted.
 *
 * **The two facts a supervisor is actually looking for are the ones a rep cannot hide.** A visit
 * worked outside the geofence carries the sentence the rep typed to explain it (`BR-VIS-2` records
 * rather than refuses), and a visit captured offline shows when it was *worked* beside when it
 * arrived. Both are printed here rather than tucked into the detail screen, because the reason to
 * open a visit is usually one of them.
 */
export function VisitList() {
  const t = useTranslations("Visits");
  const { user } = useAuth();
  const day = useBusinessDay();

  const accessToken = user?.access_token;
  const subject = user?.profile.sub;
  const enabled = Boolean(accessToken && subject);

  const [outletId, setOutletId] = useState("");
  const [userId, setUserId] = useState("");

  const visits = useQuery({
    enabled,
    queryKey: visitsKey(subject ?? "", outletId || undefined, userId || undefined),
    queryFn: ({ signal }) =>
      fetchVisits(accessToken!, { outletId: outletId || undefined, userId: userId || undefined }, signal),
  });

  // The two filters' vocabularies. Both are ordinary reads a supervisor already has, and neither is
  // required: an unfiltered list is the honest default for "what happened lately".
  const outlets = useQuery({
    enabled,
    queryKey: outletsKey(subject ?? "", {}),
    queryFn: ({ signal }) => fetchOutlets(accessToken!, {}, signal),
  });

  const users = useQuery({
    enabled,
    queryKey: usersKey(subject ?? ""),
    queryFn: ({ signal }) => fetchUsers(accessToken!, signal),
  });

  if (visits.isError) {
    const error = visits.error;

    return (
      <p role="alert" className="text-sm text-destructive">
        {error instanceof ApiError && error.status === 403 ? t("forbidden") : t("failed")}
      </p>
    );
  }

  if (!visits.data) {
    return <p className="text-sm text-muted-foreground">{t("loading")}</p>;
  }

  const shops = new Map((outlets.data?.items ?? []).map((outlet) => [outlet.id, outlet]));
  const reps = new Map((users.data ?? []).map((rep) => [rep.subjectId, rep]));

  return (
    <div className="flex flex-col gap-4">
      <div className="flex flex-wrap items-center gap-2">
        <label htmlFor="outlet" className="text-sm text-muted-foreground">{t("outletLabel")}</label>
        <select
          id="outlet"
          className={CONTROL}
          value={outletId}
          onChange={(event) => setOutletId(event.target.value)}
        >
          <option value="">{t("allOutlets")}</option>
          {(outlets.data?.items ?? []).map((outlet) => (
            <option key={outlet.id} value={outlet.id}>{naming(outlet)}</option>
          ))}
        </select>

        <label htmlFor="rep" className="text-sm text-muted-foreground">{t("repLabel")}</label>
        <select
          id="rep"
          className={CONTROL}
          value={userId}
          onChange={(event) => setUserId(event.target.value)}
        >
          <option value="">{t("allReps")}</option>
          {(users.data ?? []).map((rep) => (
            <option key={rep.subjectId} value={rep.subjectId}>{identifying(rep)}</option>
          ))}
        </select>
      </div>

      {visits.data.length === 0 ? (
        <p className="rounded-xl border border-dashed border-border px-4 py-6 text-center text-sm text-muted-foreground">
          {t("empty")}
        </p>
      ) : (
        <ul className="flex flex-col divide-y divide-border rounded-xl border border-border">
          {visits.data.map((visit) => (
            <li key={visit.id} className="flex flex-col gap-1 px-4 py-3">
              <div className="flex flex-wrap items-baseline gap-x-3 gap-y-1">
                {/*
                 * The shop's name is the link, because it is what a reader is looking at when they
                 * decide to open one — not a "view" button at the end of the row, which is a second
                 * thing to find and reads as an action rather than a destination.
                 */}
                <Link
                  href={`/visits/${visit.id}`}
                  className="min-w-48 flex-1 text-sm font-medium underline-offset-4 hover:underline focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
                >
                  {shops.get(visit.outletId) ? naming(shops.get(visit.outletId)!) : t("unknownOutlet")}
                </Link>
                <span className="text-sm text-muted-foreground">
                  {reps.get(visit.userId) ? identifying(reps.get(visit.userId)!) : visit.userId}
                </span>
                <span className="text-sm tabular-nums text-muted-foreground">
                  {day(visit.checkedInAtUtc.slice(0, 10))}
                </span>
                <span className="text-sm">{t(`outcome.${visit.outcome ?? "Open"}`)}</span>
              </div>

              {/*
               * The two lines a supervisor opens the screen for. Both are absent from an ordinary
               * visit, so a list of ordinary visits stays one line per row and the exceptions are
               * the ones that take up space.
               */}
              {!visit.wasInsideGeofence && (
                <p className="text-xs text-amber-600 dark:text-amber-500">
                  {t("elsewhere", { reason: visit.geofenceOverrideReason ?? t("noReason") })}
                </p>
              )}

              {visit.source === "Device" && visit.recordedAtUtc && (
                <p className="text-xs text-muted-foreground">
                  {t("offline", { recorded: day(visit.recordedAtUtc.slice(0, 10)) })}
                </p>
              )}
            </li>
          ))}
        </ul>
      )}

      {/*
       * The ceiling, said out loud when it might be biting. A list silently cut at two hundred is a
       * supervisor concluding their team stopped working in March.
       */}
      {visits.data.length >= 200 && (
        <p className="text-xs text-muted-foreground">{t("capped", { count: visits.data.length })}</p>
      )}
    </div>
  );
}

/** A shop, as a person refers to it: the name, with the code that disambiguates two of them. */
function naming(outlet: Outlet): string {
  return `${outlet.name} (${outlet.code})`;
}
