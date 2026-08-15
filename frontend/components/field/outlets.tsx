"use client";

import { useTranslations } from "next-intl";
import { useState } from "react";

import { useSync } from "@/components/sync/sync-provider";
import { Link } from "@/i18n/navigation";
import { useLive } from "@/lib/sync/live";
import { matching, outlets as heldOutlets } from "@/lib/sync/reference";

/**
 * Every shop the rep covers, whether or not it is on today's round (W12½ slice 8a).
 *
 * **The gap the field app had.** A rep needing a shop that today's plan does not name had one way
 * in: *Add an unplanned call* on the journey screen, which lists only the shops **not** already
 * planned for today — that is the point of `callableOutlets`, and it makes the list useless for
 * looking something up. Everything else in this app is a linear flow, so a shop planned for
 * Thursday was unreachable on Tuesday.
 *
 * **Assembly rather than machinery.** `outlets(db)` has existed since W8 slice 6 and its own comment
 * says it is *"what the outlet list reads"* — a reader written for a screen nobody built, which is
 * this project's recurring shape one more time. `matching` moved here from `lib/visits/unplanned` in
 * the same change: it is a pure filter over `ReferenceOutlet[]`, and a browse screen importing it
 * from a module named for unplanned calls reads as a coincidence rather than a decision.
 *
 * **It reads the local store, so it works with no signal**, which is the whole reason the store
 * holds outlets at all (`A4`, `OFF-03`).
 */
export function FieldOutlets() {
  const t = useTranslations("Field.outlets");
  const { db } = useSync();

  const [search, setSearch] = useState("");

  /*
   * `undefined` while the store is being read, so an empty state cannot flash before the answer
   * arrives — the three-state rule the check-in and unplanned screens both follow. A rep who reads
   * "no shops" for one frame on a device that holds four hundred learns not to trust the screen.
   */
  const held = useLive(() => heldOutlets(db), undefined, [db]);
  const found = held && matching(held, search);

  return (
    <section className="flex flex-col gap-3">
      <div className="flex flex-col gap-1">
        <h1 className="text-lg font-semibold tracking-tight">{t("title")}</h1>
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
        <p role="status" className="text-sm text-muted-foreground">
          {t("loading")}
        </p>
      ) : found.length === 0 ? (
        /*
         * Two reasons and one sentence, as the unplanned picker does: a device holding nothing yet
         * and a search that matched nothing are different causes a rep cannot act differently on.
         */
        <p role="status" className="text-sm text-muted-foreground">
          {t("none")}
        </p>
      ) : (
        <>
          {/*
            The count is the answer to "is this everything?" — a rep who knows their territory has
            about four hundred shops can tell a filtered list from a half-synced one at a glance.
          */}
          <p className="text-xs text-muted-foreground" role="status">
            {t("count", { count: found.length })}
          </p>

          <ul className="flex flex-col gap-2">
            {found.map((outlet) => (
              <li key={outlet.id} className="rounded-xl border border-border">
                {/*
                  No `call` query parameter, exactly as the unplanned picker omits it: the shop
                  screen reads a missing planned call as an unplanned visit. Browsing to a shop and
                  starting a call there are the same journey, and this is its front door.
                */}
                <Link href={`/field/outlets/${outlet.id}`} className="flex flex-col gap-0.5 p-3">
                  <span className="truncate font-medium">{outlet.name}</span>
                  <span className="font-mono text-xs text-muted-foreground">{outlet.code}</span>
                </Link>
              </li>
            ))}
          </ul>
        </>
      )}
    </section>
  );
}
