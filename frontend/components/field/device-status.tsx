"use client";

import { useFormatter, useTranslations } from "next-intl";

import { useSync } from "@/components/sync/sync-provider";
import { useLive } from "@/lib/sync/live";
import { outlets } from "@/lib/sync/reference";

/**
 * What this device is holding, and whether the back office has it (`OFF-05`, `OFF-06`).
 *
 * <b>The landing screen until Today's Journey arrives (slice 5)</b>, and not a placeholder: it is
 * the wireframes' *Sync & reconcile* screen, and it is the one a rep opens when the answer they
 * want is "is my day in" rather than "what is my day". The chrome above already carries the
 * headline; this says what is behind it.
 *
 * Every number here is read from the local store rather than the API, which is the point: it
 * answers with no signal, and what it answers with is exactly what a rep would work from.
 */
export function DeviceStatus() {
  const t = useTranslations("Field");
  const format = useFormatter();
  const { db, pending } = useSync();

  const shops = useLive(() => outlets(db), [], [db]);
  const lastSync = useLive(() => db.meta.get("lastSyncAt"), undefined, [db]);

  return (
    <div className="flex flex-col gap-6">
      <section className="flex flex-col gap-1">
        <h1 className="text-lg font-medium">{t("status.title")}</h1>
        <p className="text-sm text-muted-foreground">{t("status.intro")}</p>
      </section>

      <dl className="grid grid-cols-2 gap-4 text-sm">
        <Fact label={t("status.outlets")} value={String(shops.length)} />
        <Fact label={t("status.pending")} value={String(pending)} />
        <Fact
          label={t("status.lastSync")}
          // A rep reads "9:15" and not an ISO string, and reads it in their own language — the
          // timestamp is stored as epoch millis precisely so the formatting is the locale's
          // decision rather than the store's (ADR-0010).
          value={
            lastSync
              ? format.dateTime(new Date(Number(lastSync.value)), {
                  dateStyle: "medium",
                  timeStyle: "short",
                })
              : t("status.never")
          }
        />
      </dl>
    </div>
  );
}

function Fact({ label, value }: { label: string; value: string }) {
  return (
    <div className="flex flex-col gap-0.5 rounded-xl border border-border p-3">
      <dt className="text-xs text-muted-foreground">{label}</dt>
      <dd className="font-mono text-base">{value}</dd>
    </div>
  );
}
