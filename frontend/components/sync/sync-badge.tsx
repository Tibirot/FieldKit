"use client";

import { useTranslations } from "next-intl";

import { useSync } from "@/components/sync/sync-provider";
import { Badge } from "@/components/ui/badge";
import { evidenceComplete } from "@/lib/photos/upload";
import { useLive } from "@/lib/sync/live";
import { statusOf } from "@/lib/sync/outbox";

/**
 * Whether one visit has reached the back office (`OFF-05`, `OFF-08`).
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
 *
 * <b>Synced is not finished, which is what W11 slice 13b added.</b> A visit's JSON travels in the
 * outbox and its photographs travel on their own transport, so a visit can be fully accepted while
 * the evidence is still on the phone. That is ordinary rather than broken — but calling it *synced*
 * tells a rep they can stop caring about a photograph the back office cannot see, and this badge is
 * the one place they would have believed it.
 */
export function SyncBadge({ subjectId }: { subjectId: string }) {
  const t = useTranslations("Sync");
  const { db } = useSync();

  const status = useLive(() => statusOf(db, subjectId), "synced" as const, [db, subjectId]);

  /*
   * Asked only once the mutations are in, and that ordering is the point rather than an
   * optimisation: while a visit is still pending or refused, *that* is what a rep needs to know, and
   * a photograph outstanding behind it changes nothing they would do.
   */
  const evidence = useLive(
    () => photographsOutstanding(db, subjectId),
    false,
    [db, subjectId],
  );

  if (status === "synced" && !evidence) return null;

  if (status === "synced") {
    return <Badge variant="outline">{t("itemPhotosPending")}</Badge>;
  }

  return (
    <Badge variant={status === "failed" ? "destructive" : "secondary"}>
      {t(status === "failed" ? "itemFailed" : "itemPending")}
    </Badge>
  );
}

/**
 * Whether this visit's audit is still waiting on photographs the server has not acknowledged.
 *
 * <b>False when there is no audit</b>, rather than unknown: a visit with no audit has no photographs
 * to wait for, and a badge is not the place to explain the difference between "none" and "not yet".
 */
async function photographsOutstanding(
  db: ReturnType<typeof useSync>["db"],
  visitId: string,
): Promise<boolean> {
  const audit = await db.audits.where("visitId").equals(visitId).first();

  return audit ? !(await evidenceComplete(db, audit.id)) : false;
}
