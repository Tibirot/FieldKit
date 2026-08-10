"use client";

import { useTranslations } from "next-intl";

import { useSync } from "@/components/sync/sync-provider";
import { Badge } from "@/components/ui/badge";
import { useLive } from "@/lib/sync/live";
import { statusOf } from "@/lib/sync/outbox";

/**
 * Whether one visit has reached the back office (`OFF-05`).
 *
 * <b>Renders nothing when the work is synced</b>, which is the opposite of the indicator's rule and
 * right for the same reason. The indicator answers a question a rep asks — *can I close the app* —
 * so it always shows an answer. A badge is an annotation on a row in a list: one that said "synced"
 * against every visit would be a column of noise a rep learns to stop reading, and the one that says
 * *failed* would be lost in it.
 *
 * `subjectId` is the entity's own id — the visit, not the mutation — because that is what a screen
 * has. Several mutations can concern one visit; `statusOf` decides which of them the rep is told
 * about, and a rejection outranks anything still queued behind it.
 */
export function SyncBadge({ subjectId }: { subjectId: string }) {
  const t = useTranslations("Sync");
  const { db } = useSync();

  const status = useLive(() => statusOf(db, subjectId), "synced" as const, [db, subjectId]);

  if (status === "synced") return null;

  return (
    <Badge variant={status === "failed" ? "destructive" : "secondary"}>
      {t(status === "failed" ? "itemFailed" : "itemPending")}
    </Badge>
  );
}
