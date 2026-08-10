import type { FieldKitDatabase, ReferenceOutlet } from "./db";

/** An id the device must drop, and the version at which it stopped applying. */
export type ReferenceTombstone = { id: string; rowVersion: number };

/** One entity's page of a pull: what to upsert, what to drop, and how far the device now is. */
export type EntityChanges<T> = {
  upserts: T[];
  tombstones: ReferenceTombstone[];
  cursor: number;
};

/** The body of `POST /api/sync/pull`, as the client reads it. */
export type PullResponse = {
  changes: { outlets: EntityChanges<ReferenceOutlet> };
  snapshotVersion: string;
};

/** The name a watermark is stored under. One per entity the pull carries. */
export const OUTLETS = "outlets";

/**
 * Applies one page of a pull (`OFF-02`, `OFF-03`, sync engine §3).
 *
 * <b>The rows and the watermark are written in one transaction, and that is the whole point of this
 * function.</b> Written separately, either order loses:
 *
 * - **cursor first** — a crash before the rows land advances the device past changes it never
 *   stored. The next pull asks for everything *after* them, so those outlets are gone until an
 *   unrelated edit bumps their row version. Silent, permanent, and invisible from the server.
 * - **rows first** — a crash before the cursor lands re-sends the page. Harmless, but it is only
 *   harmless because upserts are idempotent, and that is a property of today's payloads rather than
 *   of the protocol.
 *
 * IndexedDB gives real transactions across stores, so neither trade has to be made: the device is
 * either at the old watermark with the old rows, or the new watermark with the new rows.
 *
 * `snapshotVersion` is written here too, in the same transaction, because it names the moment the
 * device's copy was taken and a value describing a page that did not land is worse than none.
 */
export async function applyOutletChanges(
  db: FieldKitDatabase,
  page: EntityChanges<ReferenceOutlet>,
  snapshotVersion?: string,
): Promise<void> {
  await db.transaction("rw", db.outlets, db.watermarks, db.meta, async () => {
    if (page.upserts.length > 0) await db.outlets.bulkPut(page.upserts);

    if (page.tombstones.length > 0) {
      await db.outlets.bulkDelete(page.tombstones.map((tombstone) => tombstone.id));
    }

    /*
     * Never backwards.
     *
     * A retried or re-ordered response could carry a cursor behind the one already stored, and
     * taking it at face value would re-send everything between the two on the next pull — an
     * expensive no-op at best, and at worst a device that oscillates instead of converging.
     */
    const current = await db.watermarks.get(OUTLETS);
    const cursor = Math.max(page.cursor, current?.cursor ?? 0);

    await db.watermarks.put({ entity: OUTLETS, cursor });

    if (snapshotVersion !== undefined) {
      await db.meta.put({ key: "snapshotVersion", value: snapshotVersion });
    }
  });
}

/**
 * How far this device has been told about an entity.
 *
 * `0` for an entity never pulled, which is the same thing the server reads as "I have nothing" —
 * so a fresh install and a device that has lost its store take the identical path.
 */
export async function watermark(db: FieldKitDatabase, entity: string): Promise<number> {
  return (await db.watermarks.get(entity))?.cursor ?? 0;
}

/** Every outlet the rep covers, by name — what the outlet list reads. */
export function outlets(db: FieldKitDatabase): Promise<ReferenceOutlet[]> {
  return db.outlets.orderBy("name").toArray();
}

/** One outlet, or undefined if this device does not hold it. */
export function outlet(db: FieldKitDatabase, id: string): Promise<ReferenceOutlet | undefined> {
  return db.outlets.get(id);
}
