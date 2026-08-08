"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useFormatter, useTranslations } from "next-intl";
import { useState } from "react";

import { useAuth } from "@/components/auth-provider";
import { Button } from "@/components/ui/button";
import { ApiError } from "@/lib/api/client";
import {
  changeOutletStatus,
  fetchOutletStatusHistory,
  outletStatusHistoryKey,
  type OutletDetail,
  type OutletStatus,
  type OutletStatusChange,
} from "@/lib/api/outlets";
import { usePermissions } from "@/lib/auth/use-permissions";
import { cn } from "@/lib/utils";

const CONTROL =
  "h-9 w-full rounded-lg border border-input bg-background px-3 text-sm text-foreground"
  + " focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none";

/**
 * Where an outlet is in its life, and the trail of how it got there (`OUT-04`).
 *
 * **Below the edit form and outside it**, which is the whole point rather than a layout choice. The
 * API gave status its own endpoint so that "this store is shut" could not happen as a side effect of
 * fixing a spelling ([spec §F4](../../../docs/product/12-outlets-master-data.md)); putting the
 * control inside the form would hand that back — one Save, two unrelated decisions. So this has its
 * own submit, and the form beside it neither sends nor reads a status.
 *
 * Until now nothing called the endpoint at all. The outlet table has had a Status column since W5
 * that could only ever read `Active`, and the trail behind it was written by integration tests and
 * read by nobody.
 *
 * **`Closed` is terminal and the panel says so instead of offering a control that will be refused.**
 * A select still listing "Active" on a closed outlet is a door that does not open, which is the
 * pattern this codebase rejects everywhere else. What it offers instead is the guidance the spec
 * gives — a location that genuinely reopens is a new outlet with its own code, because its trading
 * history as a different business should not silently continue.
 */
export function OutletLifecycle({ outlet }: { outlet: OutletDetail }) {
  const t = useTranslations("OutletLifecycle");
  const format = useFormatter();
  const { user } = useAuth();
  const client = useQueryClient();
  const { has } = usePermissions();

  const accessToken = user?.access_token;
  const subject = user?.profile.sub;

  const history = useQuery({
    enabled: Boolean(accessToken && subject),
    queryKey: outletStatusHistoryKey(subject ?? "", outlet.id),
    queryFn: ({ signal }) => fetchOutletStatusHistory(accessToken!, outlet.id, signal),
  });

  const closed = outlet.status === "Closed";

  // The transitions that are actually available: never the one it already holds, and nothing at all
  // once it is closed. Derived rather than listed, so a fourth status could never be forgotten here.
  const available = (["Active", "Inactive", "Closed"] as const).filter(
    (status) => status !== outlet.status,
  );

  const [chosen, setChosen] = useState<OutletStatus | null>(null);

  /**
   * The option actually selected, reconciled against what is still on offer.
   *
   * A plain `useState(available[0])` breaks the moment a change succeeds: an Active outlet offers
   * Inactive and Closed, and once it *becomes* Inactive that option leaves the list while the state
   * still holds it — a required select bound to a value it no longer contains, which renders blank
   * and submits whatever the browser falls back to. The same shape lost an outlet's locale (#77) and
   * its time zone (#82); the fix there was to widen the options, and here it is to narrow the state,
   * because unlike a stored value a stale *intent* is worth discarding.
   */
  const target = chosen !== null && available.includes(chosen) ? chosen : available[0];

  const [reason, setReason] = useState("");
  const [refused, setRefused] = useState<readonly string[]>([]);
  const [reasonProblem, setReasonProblem] = useState<string | null>(null);

  const change = useMutation({
    mutationFn: () =>
      changeOutletStatus(accessToken!, outlet.id, {
        // Trimmed to null rather than sent as spaces: the server treats whitespace as absent when it
        // decides whether a close was explained, and a client that disagrees gets a puzzling refusal.
        status: target,
        reason: reason.trim() === "" ? null : reason.trim(),
      }),

    onSuccess: async () => {
      setReason("");
      setRefused([]);
      setReasonProblem(null);

      // The outlet itself, its trail, and the list behind it: the Status column is on that table and
      // a status filter may now exclude this row entirely.
      await client.invalidateQueries({ queryKey: ["outlet"] });
      await client.invalidateQueries({ queryKey: ["outlet-status-history"] });
      await client.invalidateQueries({ queryKey: ["outlets"] });
    },

    onError: (error) => {
      if (!(error instanceof ApiError)) {
        setRefused([t("failed")]);
        return;
      }

      const unattributed: string[] = [];
      let onReason: string | null = null;

      // "Closing an outlet permanently requires a reason." belongs under the reason box; "A closed
      // outlet cannot be reopened." is about the outlet and belongs at the top.
      for (const problem of error.problems) {
        if (problem.field === "reason") onReason = problem.message;
        else unattributed.push(problem.message);
      }

      setReasonProblem(onReason);
      // A refusal the API attached to nothing — a 403, a 404, a 500 with no body — still has to say
      // something. Without this the loop above runs zero times and the screen goes silent, which reads
      // as a Save button that does nothing rather than as a refusal.
      setRefused(error.problems.length > 0 ? unattributed : [t("failed")]);
    },
  });

  return (
    <section className="flex max-w-2xl flex-col gap-4 rounded-xl border border-border p-4">
      <div className="flex flex-wrap items-center gap-3">
        <h2 className="text-sm font-semibold">{t("title")}</h2>
        <StatusBadge status={outlet.status} label={t(`statuses.${outlet.status}`)} />
      </div>

      {refused.length > 0 ? (
        <ul role="alert" className="rounded-lg bg-destructive/10 px-3 py-2 text-sm text-destructive">
          {refused.map((problem) => (
            <li key={problem}>{problem}</li>
          ))}
        </ul>
      ) : null}

      {closed ? (
        <p className="text-sm text-muted-foreground">{t("terminal")}</p>
      ) : has("outlet:write") ? (
        <form
          onSubmit={(event) => {
            event.preventDefault();
            setRefused([]);
            setReasonProblem(null);
            change.mutate();
          }}
          noValidate
          className="flex flex-col gap-3"
        >
          <div className="flex flex-col gap-1.5">
            <label htmlFor="outletStatus" className="text-sm font-medium">
              {t("moveTo")}
            </label>
            <select
              id="outletStatus"
              value={target}
              onChange={(event) => setChosen(event.target.value as OutletStatus)}
              className={cn(CONTROL, "max-w-xs")}
            >
              {available.map((status) => (
                <option key={status} value={status}>
                  {t(`statuses.${status}`)}
                </option>
              ))}
            </select>
          </div>

          <div className="flex flex-col gap-1.5">
            <label htmlFor="outletStatusReason" className="text-sm font-medium">
              {t("reason")}
              {/*
                Decorative, and only while Closed is selected — `required` on the control is what a
                screen reader announces and what the browser enforces.
              */}
              {target === "Closed" ? (
                <span aria-hidden="true" className="ml-1 text-destructive">
                  *
                </span>
              ) : null}
            </label>
            <textarea
              id="outletStatusReason"
              rows={2}
              value={reason}
              onChange={(event) => setReason(event.target.value)}
              // Required only for the irreversible one. Demanding it for a routine Active↔Inactive
              // toggle buys a column full of "." — the server draws the line in the same place.
              required={target === "Closed"}
              aria-invalid={Boolean(reasonProblem)}
              aria-describedby={reasonProblem ? "outletStatusReason-error" : undefined}
              className={cn(CONTROL, "h-auto py-2", reasonProblem && "border-destructive")}
            />
            {reasonProblem ? (
              <p id="outletStatusReason-error" className="text-xs text-destructive">
                {reasonProblem}
              </p>
            ) : null}
            <p className="text-xs text-muted-foreground">
              {target === "Closed" ? t("reasonRequiredHint") : t("reasonHint")}
            </p>
          </div>

          {/*
            The warning sits beside the button rather than behind a confirmation step. Closing is
            irreversible, but unlike deleting a field definition it is a *deliberate* act with its
            own select and its own required sentence — someone who has typed why they are closing a
            shop has already confirmed it, and a second dialog asking "are you sure" would be
            ceremony rather than a safeguard.
          */}
          {target === "Closed" ? (
            <p className="text-xs text-destructive">{t("closingWarning")}</p>
          ) : null}

          <div>
            <Button
              type="submit"
              size="sm"
              variant={target === "Closed" ? "destructive" : "default"}
              disabled={change.isPending}
            >
              {change.isPending ? t("saving") : t("apply")}
            </Button>
          </div>
        </form>
      ) : null}

      <div className="flex flex-col gap-2">
        <h3 className="text-xs font-semibold text-muted-foreground uppercase">{t("history")}</h3>

        {history.isPending ? (
          <p className="text-sm text-muted-foreground">{t("historyLoading")}</p>
        ) : history.isError ? (
          <p role="alert" className="text-sm text-destructive">
            {t("historyFailed")}
          </p>
        ) : (
          <ol className="flex flex-col divide-y divide-border rounded-lg border border-border">
            {history.data.map((entry) => (
              <li
                key={`${entry.changedAtUtc}-${entry.to}`}
                className="flex flex-col gap-1 px-3 py-2 text-sm"
              >
                <div className="flex flex-wrap items-baseline gap-x-2 gap-y-1">
                  <span className="font-medium">
                    {/*
                      A null `from` is the outlet's creation rather than a transition, and reads as
                      one. Rendering it as "→ Active" would invent a previous state the shop never
                      had.
                    */}
                    {entry.from === null
                      ? t("created", { to: t(`statuses.${entry.to}`) })
                      : t("moved", {
                          from: t(`statuses.${entry.from}`),
                          to: t(`statuses.${entry.to}`),
                        })}
                  </span>
                  <span className="text-xs text-muted-foreground">
                    {format.dateTime(new Date(entry.changedAtUtc), {
                      dateStyle: "medium",
                      timeStyle: "short",
                    })}
                    {actor(entry) ? ` · ${actor(entry)}` : ""}
                  </span>
                </div>
                {entry.reason ? (
                  <p className="text-xs text-muted-foreground">{entry.reason}</p>
                ) : null}
              </li>
            ))}
          </ol>
        )}
      </div>
    </section>
  );
}

/**
 * Who to credit an entry to.
 *
 * The name when the API could resolve one, and **the subject when it could not** — rather than
 * nothing. A subject is a poor label, but it is the only handle on an entry whose author no longer
 * has a profile: a deleted account, an import principal, someone from before the user record. An
 * entry that says only "Active → Inactive, 6 Aug" has quietly lost the attribution the trail exists
 * to keep, and that loss would look identical to a transition nobody was ever recorded for.
 *
 * Not a fallback the API should have applied itself: it returns the two facts it has, and the
 * decision about what to show when one is missing belongs to the thing doing the showing.
 */
function actor(entry: OutletStatusChange): string | null {
  return entry.changedByName ?? entry.changedBy;
}

function StatusBadge({ status, label }: { status: OutletStatus; label: string }) {
  return (
    <span
      className={cn(
        "rounded-full px-2 py-0.5 text-xs font-medium",
        status === "Active" && "bg-primary/15 text-primary",
        status === "Inactive" && "bg-muted text-muted-foreground",
        status === "Closed" && "bg-destructive/15 text-destructive",
      )}
    >
      {label}
    </span>
  );
}
