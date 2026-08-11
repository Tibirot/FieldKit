"use client";

import { useFormatter, useTranslations } from "next-intl";
import { useEffect, useState } from "react";

import { InstallPrompt } from "@/components/field/install-prompt";

import { useSync } from "@/components/sync/sync-provider";
import { useLive } from "@/lib/sync/live";
import { outlets } from "@/lib/sync/reference";
import { concernOf, storageStatus, type StorageStatus } from "@/lib/sync/storage";

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

  /*
   * Read once per mount rather than live (`OFF-11`, W9 slice 11).
   *
   * `storageStatus` reads the browser, not the database, so `useLive` has nothing to observe — and
   * a quota figure that moved while a rep watched it would be noise. This screen is opened to
   * answer a question, and the answer is true as of opening it.
   */
  const [storage, setStorage] = useState<StorageStatus | null>(null);

  useEffect(() => {
    let cancelled = false;

    void storageStatus().then((status) => {
      if (!cancelled) setStorage(status);
    });

    return () => {
      cancelled = true;
    };
  }, []);

  const concern = storage ? concernOf(storage, pending) : "none";

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

        {/* Read from the browser rather than the store, and shown as a fact whether or not it is a
            problem — a rep who has never seen the number cannot judge the day it changes. */}
        <Fact
          label={t("status.storage")}
          value={
            storage?.usedBytes !== null && storage?.usedBytes !== undefined
              ? t("status.storageUsed", {
                  used: megabytes(storage.usedBytes),
                  quota: storage.quotaBytes ? megabytes(storage.quotaBytes) : "—",
                })
              : t("status.storageUnknown")
          }
        />
      </dl>

      {/* The only two states a rep can act on, and neither is "the number is large" (`OFF-11`). */}
      {concern !== "none" ? (
        <p className="rounded-xl border border-border p-3 text-sm" role="alert">
          {t(`storagePressure.${concern}`)}
        </p>
      ) : null}

      {/* Below the numbers, because it is the answer to the problem above rather than a feature
          being advertised: installing is what makes a browser agree to keep this data. */}
      <InstallPrompt />
    </div>
  );
}

/** Whole megabytes. A rep comparing "18" with "2048" does not need either to three decimals. */
function megabytes(bytes: number): number {
  return Math.round(bytes / 1024 / 1024);
}

function Fact({ label, value }: { label: string; value: string }) {
  return (
    <div className="flex flex-col gap-0.5 rounded-xl border border-border p-3">
      <dt className="text-xs text-muted-foreground">{label}</dt>
      <dd className="font-mono text-base">{value}</dd>
    </div>
  );
}
