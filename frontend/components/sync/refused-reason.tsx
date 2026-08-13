"use client";

import { useTranslations } from "next-intl";

import { useSync } from "@/components/sync/sync-provider";
import { storedRefusalText } from "@/lib/api/refusals";
import { useLive } from "@/lib/sync/live";
import { refusalOf } from "@/lib/sync/outbox";

/**
 * Why the back office would not take this work (`OFF-09`, [ADR-0012](../../../docs/architecture/adr/0012-server-message-localization.md)) — W11½ R5.
 *
 * <b>The half of the badge that was missing for three months.</b> `markRejected` has stored
 * `errorCode` and `errorDetail` since W8, under a comment saying the UI translates them, and nothing
 * read either — six references in the whole front end, all of them the declaration or the write
 * (regression F1). A rep whose work the server refused saw *Needs attention* and could not find out
 * why, on the one surface where the reason matters most: the call is over, the shop is behind them,
 * and only a person can unstick it.
 *
 * <b>Beside the badge rather than inside it.</b> A badge is an annotation on a row — three words at
 * most — and the reason is a sentence. Putting the sentence in the badge would either truncate it or
 * turn every row of the round into a paragraph; the badge stays the signal and this is the answer.
 *
 * <b>Renders nothing unless there is something to say</b>, which is most of the time. Not every
 * failure carries a reason: a transport failure marks no code at all, and a device holding a row
 * written before W11½ has neither field. The badge alone already says *failed*, and a box that
 * appeared saying "refused, reason unknown" would be a worse answer than the one it replaced.
 */
export function RefusedReason({ subjectId }: { subjectId: string }) {
  const refusals = useTranslations("Refusals");
  const { db } = useSync();

  const refusal = useLive(async () => (await refusalOf(db, subjectId)) ?? null, null, [
    db,
    subjectId,
  ]);

  const sentence = refusal && storedRefusalText(refusals, refusal);
  if (!sentence) return null;

  /*
   * `role="alert"` rather than `status`: this is the one thing on the screen that needs a person to
   * do something, and it appears without the rep having asked — a sync that ran in a car park is
   * what puts it there. The visible styling says the same thing in the other channel.
   */
  return (
    <p className="text-sm text-destructive" role="alert">
      {sentence}
    </p>
  );
}
