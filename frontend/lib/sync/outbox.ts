import type { FieldKitDatabase, OutboxEntry } from "./db";

/** What a caller supplies to queue work. Everything else is bookkeeping this module owns. */
export type NewMutation = {
  type: string;
  subjectId: string;
  payload: unknown;
  /**
   * Supplied only by a caller that has already minted one — a retry of a *rejected* mutation
   * resubmits under a **new** id, and the caller is the one that knows it is doing that
   * (`BR-ORD-9`, sync engine §4). Left out otherwise, which is the normal case.
   */
  mutationId?: string;
};

/**
 * Puts captured work in the outbox, durably, before the UI says it is saved (`OFF-02`, `OFF-04`).
 *
 * <b>The mutation id is minted here and never again.</b> That is what makes a retry free: the
 * server's ledger is keyed by it, so a mutation re-sent because the response was lost is answered
 * with what happened the first time. Minting a fresh id per attempt would make every retry a new
 * piece of work, and a rep with a bad connection would end the day with five copies of one visit.
 *
 * It returns only after the write is committed. The caller is expected to await it before showing
 * the rep a confirmation — a promise that resolves early is exactly the "no lost work" claim this
 * store exists to keep.
 */
export async function enqueue(db: FieldKitDatabase, mutation: NewMutation): Promise<OutboxEntry> {
  const entry: OutboxEntry = {
    mutationId: mutation.mutationId ?? crypto.randomUUID(),
    type: mutation.type,
    subjectId: mutation.subjectId,
    payload: mutation.payload,
    status: "pending",
    createdAt: Date.now(),
    attempts: 0,
  };

  await db.outbox.add(entry);

  return entry;
}

/**
 * What is waiting to go, oldest first.
 *
 * Capture order, not arbitrary order: the server sees a rep's day the way it happened, and a
 * partial drain leaves the *tail* behind rather than a hole in the middle.
 *
 * `failed` is excluded. A mutation the server refused on its merits will be refused again by a
 * server that has not changed its mind, so re-sending it is a loop that burns a bad connection and
 * tells the rep nothing. It waits for a person (`OFF-09`).
 */
export async function pending(db: FieldKitDatabase, limit?: number): Promise<OutboxEntry[]> {
  const query = db.outbox.where("status").equals("pending");
  const entries = await query.sortBy("createdAt");

  return limit === undefined ? entries : entries.slice(0, limit);
}

/** How many pieces of work are unsent — the number the connectivity indicator shows (`OFF-05`). */
export function pendingCount(db: FieldKitDatabase): Promise<number> {
  return db.outbox.where("status").equals("pending").count();
}

/**
 * Marks a batch as sent and counts the attempt.
 *
 * The attempt is counted *now*, before the answer, because the failure this protects against is the
 * one where no answer ever comes. Counting on success would leave a mutation that times out forever
 * looking like it had never been tried.
 */
export async function markInflight(db: FieldKitDatabase, mutationIds: string[]): Promise<void> {
  await db.transaction("rw", db.outbox, async () => {
    for (const mutationId of mutationIds) {
      await db.outbox
        .where("mutationId")
        .equals(mutationId)
        .modify((entry) => {
          entry.status = "inflight";
          entry.attempts += 1;
        });
    }
  });
}

/**
 * The server accepted it, so the row goes.
 *
 * Deleted rather than marked, per `OutboxEntry` — the work is now the server's record, and keeping
 * a tombstone of a successful send is a store that only grows.
 */
export async function markAccepted(db: FieldKitDatabase, mutationIds: string[]): Promise<void> {
  await db.outbox.bulkDelete(mutationIds);
}

/**
 * The server refused it on its merits (`OFF-09`).
 *
 * Kept, with the reason, because a rejection is a result and somebody has to see it. The code is an
 * `ADR-0012` string the UI translates; the detail is the server's English, shown only where no
 * translation exists.
 */
export async function markRejected(
  db: FieldKitDatabase,
  mutationId: string,
  errorCode?: string,
  errorDetail?: string,
): Promise<void> {
  await db.outbox
    .where("mutationId")
    .equals(mutationId)
    .modify((entry) => {
      entry.status = "failed";
      entry.errorCode = errorCode;
      entry.errorDetail = errorDetail;
    });
}

/**
 * Returns everything stuck `inflight` to `pending`.
 *
 * <b>Called on startup, and this is the whole reason `inflight` is a durable state.</b> A device
 * that is killed mid-push — the browser tab closed, the phone out of battery, the OS reclaiming
 * memory — leaves rows claiming to be in flight on a connection that no longer exists. Nothing will
 * ever answer them, and without this they sit there forever while the rep is told their work is
 * syncing.
 *
 * Re-sending is safe precisely because the id survives the crash: whatever the server did with the
 * first attempt, the ledger will say so.
 */
export async function reclaimInflight(db: FieldKitDatabase): Promise<number> {
  return db.outbox
    .where("status")
    .equals("inflight")
    .modify((entry) => {
      entry.status = "pending";
    });
}

/** Whether this entity has work the server has not accepted — the per-item badge (`OFF-05`). */
export async function statusOf(
  db: FieldKitDatabase,
  subjectId: string,
): Promise<OutboxEntry["status"] | "synced"> {
  const entries = await db.outbox.where("subjectId").equals(subjectId).toArray();

  if (entries.length === 0) return "synced";

  // A rejection outranks a pending retry: if any attempt for this entity needs a person, that is
  // what the rep has to be told, whatever else is queued behind it.
  return entries.some((entry) => entry.status === "failed") ? "failed" : entries[0].status;
}
