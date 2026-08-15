"use client";

import { useTranslations } from "next-intl";
import { useState } from "react";

import { RefusedReason } from "@/components/sync/refused-reason";
import { useSync } from "@/components/sync/sync-provider";
import { Button } from "@/components/ui/button";
import { Link } from "@/i18n/navigation";
import { useLive } from "@/lib/sync/live";
import { matching } from "@/lib/sync/reference";
import { callableOutlets } from "@/lib/visits/unplanned";

/**
 * Starting a call at a shop that is not on today's round (`JRN-06`, `BR-JRN-4`) — W11½ R4.
 *
 * <b>The entry point the field app never had.</b> `JRN-06` — *"not-visited with reason; add unplanned
 * visit"* — is a Phase-2 **Must**, and every layer of it was built except this one (regression F7).
 * The cost was not only the missing annotation: with no planned calls, this screen offered *Sync now*
 * and *This device* and nothing else, so a rep whose plan had not arrived could do nothing at all —
 * which is why the regression sweep could not reach check-in, the audit or order capture by hand.
 *
 * <b>Collapsed by default, and that is the product decision.</b> The round is what a rep should be
 * working; a list of every shop on the territory sitting open above it invites a day worked off the
 * plan. It opens on a tap, which is one more than the planned path and none at all for the rep who
 * never needs it.
 *
 * <b>Nothing is queued here.</b> The list navigates to check-in and the annotation is enqueued when
 * the visit actually starts — see [check-in](./check-in.tsx). Queuing on the tap would tell a
 * supervisor a call happened at every shop a rep opened and thought better of.
 */
export function UnplannedCall({ date }: { date: string }) {
  const t = useTranslations("Field.unplanned");
  const { db } = useSync();

  const [open, setOpen] = useState(false);
  const [search, setSearch] = useState("");

  /*
   * `undefined` while the store is being read, so the empty state cannot flash before the answer
   * arrives — the same three-state rule the check-in screen states at length. Read only once the
   * section is open: a rep who never taps it never pays for the query.
   */
  const outlets = useLive(
    async () => (open ? await callableOutlets(db, date) : undefined),
    undefined,
    [db, date, open],
  );

  if (!open) {
    return (
      <Button variant="outline" size="sm" className="self-start" onClick={() => setOpen(true)}>
        {t("open")}
      </Button>
    );
  }

  const found = outlets && matching(outlets, search);

  return (
    <section className="flex flex-col gap-3 rounded-xl border border-border p-3">
      <div className="flex flex-col gap-1">
        <h2 className="font-medium">{t("title")}</h2>
        <p className="text-sm text-muted-foreground">{t("intro")}</p>
      </div>

      <label className="flex flex-col gap-1 text-sm">
        <span className="font-medium">{t("search.label")}</span>
        <input
          type="search"
          className="rounded-xl border border-border bg-transparent p-3 text-sm"
          placeholder={t("search.placeholder")}
          value={search}
          onChange={(event) => setSearch(event.target.value)}
        />
      </label>

      {found === undefined ? (
        <p className="text-sm text-muted-foreground" role="status">
          {t("loading")}
        </p>
      ) : found.length === 0 ? (
        /*
         * Two reasons this list can be empty and one sentence for both, because a rep cannot act
         * differently on them: a territory whose every shop is already on today's round, and a
         * search that matched nothing. Splitting them would be the screen explaining its own filter.
         */
        <p className="text-sm text-muted-foreground" role="status">
          {t("none")}
        </p>
      ) : (
        <ul className="flex flex-col gap-2">
          {found.map((outlet) => (
            <li key={outlet.id} className="rounded-xl border border-border p-3">
              {/*
                No `call` query parameter, and its absence is the whole mechanism: the shop screen
                treats a missing planned call as an unplanned visit, which is what makes this a
                different kind of call rather than the same one reached another way.
              */}
              <Link href={`/field/outlets/${outlet.id}`} className="flex flex-col">
                <span className="truncate font-medium">{outlet.name}</span>
                <span className="font-mono text-xs text-muted-foreground">{outlet.code}</span>
              </Link>

              {/*
                Why the back office refused a call already made here (`OFF-09`) — W11½ R5.

                The **only** place this refusal can be seen: an unplanned call is queued under the
                shop's id, and the round has no row for a shop it never planned. Without it the most
                likely refusal in the whole app — `journey.plan.noneForDate`, when no published round
                covers today (regression F9) — reaches the rep as an unexplained pending count.
              */}
              <RefusedReason subjectId={outlet.id} />
            </li>
          ))}
        </ul>
      )}

      <Button variant="outline" size="sm" className="self-start" onClick={() => setOpen(false)}>
        {t("close")}
      </Button>
    </section>
  );
}
