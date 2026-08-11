"use client";

import { useTranslations } from "next-intl";
import { useState } from "react";

import { useSync } from "@/components/sync/sync-provider";
import { Button } from "@/components/ui/button";
import { useRouter } from "@/i18n/navigation";
import { useLive } from "@/lib/sync/live";
import { markNotVisited, queuedNotVisited, type NotVisitedRefusal } from "@/lib/visits/not-visited";

/**
 * Reporting a call the rep could not make (`VIS-07`, `JRN-06`) — W9 slice 9.
 *
 * <b>It lives on the shop screen, beneath check-in, and the order is the argument.</b> A rep who has
 * driven to a shop tries to work it; reporting that they could not is what they do when that fails.
 * Putting it on the round instead would make "skip" a one-tap option beside every call of the day,
 * which is a different product.
 *
 * <b>Only for a planned call.</b> There is nothing to annotate on an unplanned visit — a call nobody
 * planned that the rep could not make is not a fact about anybody's round, and `BR-JRN-2` is about
 * coverage against a plan.
 *
 * <b>W7 built this server-side and W9 makes it reachable with no signal.</b> The annotation goes to
 * the outbox as a `NotVisitedCall` — the second mutation type the protocol has ever carried, and the
 * one that turns `PushedMutation.Type` into a discriminator on the server.
 */
export function NotVisited({ plannedVisitId }: { plannedVisitId: string }) {
  const t = useTranslations("Field.notVisited");
  const router = useRouter();
  const { db } = useSync();

  const queued = useLive(
    async () => (await queuedNotVisited(db, plannedVisitId)) ?? null,
    undefined,
    [db, plannedVisitId],
  );

  const [open, setOpen] = useState(false);
  const [reason, setReason] = useState("");
  const [saving, setSaving] = useState(false);
  const [refused, setRefused] = useState<NotVisitedRefusal | null>(null);

  const report = async () => {
    setSaving(true);
    setRefused(null);

    const result = await markNotVisited(db, plannedVisitId, reason);

    if (!result.ok) {
      setSaving(false);
      setRefused(result.refusal);

      return;
    }

    // Back to the round, where the stop now reads *Not visited* with the rep's own sentence under it
    // and a badge saying the back office has not heard yet.
    router.replace("/field");
  };

  if (queued === undefined) return null;

  if (queued !== null) {
    /*
     * Already reported from this device and still on its way. Shown rather than hidden, because the
     * rep's own words are the thing they will want to check — and because a screen that simply
     * offered the form again would invite a second report the server will never apply.
     */
    return (
      <section className="flex flex-col gap-1 rounded-xl border border-border p-3" role="status">
        <p className="text-sm font-medium">{queued.failed ? t("failed") : t("reported")}</p>
        <p className="text-sm text-muted-foreground">{queued.reason}</p>
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

      <label className="flex flex-col gap-1 text-sm">
        <span className="font-medium">{t("reason.label")}</span>
        <textarea
          className="min-h-20 rounded-xl border border-border bg-transparent p-3 text-sm"
          value={reason}
          onChange={(event) => setReason(event.target.value)}
        />
      </label>

      {refused ? (
        <p className="text-sm text-destructive" role="alert">
          {t(`refusal.${REFUSAL_KEYS[refused]}`)}
        </p>
      ) : null}

      <div className="flex flex-wrap gap-2">
        <Button onClick={() => void report()} disabled={saving}>
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
  "journey.visit.reasonRequired": "reasonRequired",
  "journey.visit.alreadyReported": "alreadyReported",
} as const satisfies Record<NotVisitedRefusal, string>;
