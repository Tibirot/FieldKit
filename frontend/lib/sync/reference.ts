import type { Table } from "dexie";

import type { FieldKitDatabase, ReferenceOutlet, ReferencePlannedVisit } from "./db";

/** An id the device must drop, and the version at which it stopped applying. */
export type ReferenceTombstone = { id: string; rowVersion: number };

/** One entity's page of a pull: what to upsert, what to drop, and how far the device now is. */
export type EntityChanges<T> = {
  upserts: T[];
  tombstones: ReferenceTombstone[];
  cursor: number;
};

/**
 * The name a watermark is stored under. One per entity the pull carries.
 *
 * They advance independently, which is why they are separate rows and separate request fields: a
 * tenant that edits outlets hourly and publishes a plan monthly would, on a shared cursor, make
 * every outlet edit look like a journey change. It is also what lets a new entity type be added
 * without resetting the ones that already work.
 */
export const OUTLETS = "outlets";
export const JOURNEYS = "journeys";

/**
 * Applies one page of a pull (`OFF-02`, `OFF-03`, sync engine §3).
 *
 * <b>The rows and the watermark are written in one transaction, and that is the whole point of this
 * function.</b> Written separately, either order loses:
 *
 * - **cursor first** — a crash before the rows land advances the device past changes it never
 *   stored. The next pull asks for everything *after* them, so those rows are gone until an
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
async function apply<T extends { id: string }>(
  db: FieldKitDatabase,
  store: string,
  entity: string,
  page: EntityChanges<T>,
  snapshotVersion?: string,
): Promise<void> {
  // Reached by store name rather than through the typed accessors, because `bulkDelete` on an
  // `EntityTable<T, "id">` cannot prove a generic `T`'s key is a string. The typed accessors are
  // still what every *reader* below uses; this one place trades the key type for the generic.
  const table: Table<T> = db.table<T>(store);

  await db.transaction("rw", table, db.watermarks, db.meta, async () => {
    if (page.upserts.length > 0) await table.bulkPut(page.upserts);

    if (page.tombstones.length > 0) {
      await table.bulkDelete(page.tombstones.map((tombstone) => tombstone.id));
    }

    /*
     * Never backwards.
     *
     * A retried or re-ordered response could carry a cursor behind the one already stored, and
     * taking it at face value would re-send everything between the two on the next pull — an
     * expensive no-op at best, and at worst a device that oscillates instead of converging.
     */
    const current = await db.watermarks.get(entity);
    const cursor = Math.max(page.cursor, current?.cursor ?? 0);

    await db.watermarks.put({ entity, cursor });

    if (snapshotVersion !== undefined) {
      await db.meta.put({ key: "snapshotVersion", value: snapshotVersion });
    }
  });
}

/** The outlets the rep covers. */
export function applyOutletChanges(
  db: FieldKitDatabase,
  page: EntityChanges<ReferenceOutlet>,
  snapshotVersion?: string,
): Promise<void> {
  return apply(db, "ref_outlets", OUTLETS, page, snapshotVersion);
}

/**
 * The calls on the rep's round.
 *
 * A separate transaction from the outlets, deliberately. The two cursors are independent, so
 * failing to store the round must not undo outlets that already landed — a device that got half a
 * pull should keep the half it got.
 */
export function applyJourneyChanges(
  db: FieldKitDatabase,
  page: EntityChanges<ReferencePlannedVisit>,
): Promise<void> {
  return apply(db, "ref_planned_visits", JOURNEYS, page);
}

/**
 * How far this device has been told about an entity.
 *
 * `0` for an entity never pulled, which is the same thing the server reads as "I have nothing" —
 * so a fresh install and a device that has lost its store take the identical path. It is also what
 * makes a *new* entity type free: a device that has never heard of journeys asks from zero for
 * those and keeps its outlet watermark.
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

/**
 * The rep's round for one day — what *Today's Journey* draws (`JRN-05`).
 *
 * The date is passed in rather than read from a clock here, because "today" is a question about
 * where the rep is and this module has no opinion about that. One range query on the `date` index,
 * which is why the field is stored as an ISO string.
 */
export function plannedVisits(
  db: FieldKitDatabase,
  date: string,
): Promise<ReferencePlannedVisit[]> {
  return db.plannedVisits.where("date").equals(date).toArray();
}

/**
 * Drops calls older than `before`, so a device does not accumulate every round it has ever held.
 *
 * <b>The client's job, not the server's</b>, and that is the decision worth noting. A server-side
 * date window would make the passage of midnight a membership change with no row version behind it
 * — the same problem the outlet baseline exists to work around — for a rule a phone can evaluate
 * perfectly well against a date it already has. Nothing has to be told; time passes on the device
 * too.
 */
export async function pruneJourney(db: FieldKitDatabase, before: string): Promise<number> {
  return db.plannedVisits.where("date").below(before).delete();
}
