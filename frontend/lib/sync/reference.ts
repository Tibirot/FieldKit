import type { Table } from "dexie";

import type {
  FieldKitDatabase,
  ReferenceAssortmentLine,
  ReferenceAssortmentOverride,
  ReferenceOutlet,
  ReferencePlannedVisit,
  ReferenceProduct,
  ReferenceVisitWorkflow,
} from "./db";

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
export const CONFIGURATION = "configuration";
export const PRODUCTS = "products";
export const ASSORTMENT = "assortment";
export const OUTLET_ASSORTMENT = "outletAssortment";

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
 * The tenant's visit workflows.
 *
 * Its own transaction, like the round. Three entities, three cursors, three transactions: a device
 * that got one page of a pull keeps it whatever happened to the others.
 */
export function applyConfigurationChanges(
  db: FieldKitDatabase,
  page: EntityChanges<ReferenceVisitWorkflow>,
): Promise<void> {
  return apply(db, "ref_visit_workflows", CONFIGURATION, page);
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

/**
 * How visits are worked in one channel (`VIS-03`).
 *
 * <b>Undefined is an answer, not a failure.</b> The server returns a default for a channel nobody
 * configured — no steps, presence expected — and the device has to reach the same conclusion for a
 * channel whose workflow it has not been sent, or a rep would be stuck at a shop the back office
 * considers perfectly ordinary. The caller supplies the default; this only says what is held.
 */
export function workflowFor(
  db: FieldKitDatabase,
  channelId: string,
): Promise<ReferenceVisitWorkflow | undefined> {
  return db.workflows.where("channelId").equals(channelId).first();
}

/**
 * The tenant's product catalogue.
 *
 * Its own transaction, like the round and the workflows. Four entities, four cursors, four
 * transactions: a device that got one page of a pull keeps it whatever happened to the others.
 */
export function applyProductChanges(
  db: FieldKitDatabase,
  page: EntityChanges<ReferenceProduct>,
): Promise<void> {
  return apply(db, "ref_products", PRODUCTS, page);
}

/**
 * Products a rep can put on an order or count on a shelf, by name (`PRD-01`).
 *
 * <b>Active only, by default.</b> The device holds discontinued products so it can still *name* one
 * on an order taken last week — but offering them in a picker is how a rep orders something the
 * tenant stopped selling. Naming and offering are different jobs, and this is the offering one.
 */
export function products(db: FieldKitDatabase): Promise<ReferenceProduct[]> {
  return db.products.where("status").equals("Active").sortBy("name");
}

/** One product, however it is classified — including a discontinued one. */
export function product(
  db: FieldKitDatabase,
  id: string,
): Promise<ReferenceProduct | undefined> {
  return db.products.get(id);
}

/** The channel assortment — which products each channel carries. */
export function applyAssortmentChanges(
  db: FieldKitDatabase,
  page: EntityChanges<ReferenceAssortmentLine>,
): Promise<void> {
  return apply(db, "ref_assortment", ASSORTMENT, page);
}

/** The per-outlet exceptions to it. */
export function applyOutletAssortmentChanges(
  db: FieldKitDatabase,
  page: EntityChanges<ReferenceAssortmentOverride>,
): Promise<void> {
  return apply(db, "ref_assortment_overrides", OUTLET_ASSORTMENT, page);
}

/**
 * Drops overrides for outlets this device no longer holds.
 *
 * <b>The server sends no scope tombstones for these, deliberately, and this is why it does not have
 * to.</b> When an outlet leaves a rep's territory the device is already told — Sync mints an outlet
 * tombstone — and an override is meaningless without the outlet it qualifies. So the device works it
 * out from a fact it already has, rather than the server enumerating rows it is about to stop being
 * allowed to talk about.
 *
 * Run after every pull, because an outlet can leave scope on any of them.
 */
export async function pruneOutletAssortment(db: FieldKitDatabase): Promise<number> {
  const held = new Set((await db.outlets.toArray()).map((row) => row.id));
  const orphans = await db.assortmentOverrides
    .filter((over) => !held.has(over.outletId))
    .primaryKeys();

  if (orphans.length === 0) return 0;

  await db.assortmentOverrides.bulkDelete(orphans);

  return orphans.length;
}

/**
 * What a rep may sell at one shop (`PRD-02`, `B2`).
 *
 * <b>Resolved here, on the device, rather than sent resolved.</b> `PRD-02` stores overrides
 * precisely so there is no materialised per-outlet list to keep in step; sending one would rebuild
 * that materialisation on the wire, and a channel edit would then have to invalidate every outlet it
 * touches. The rule is small and the inputs are local, so the device computes it.
 *
 * Returns product ids with their must-stock flag: `Added` overrides join the channel's list,
 * `Removed` ones leave it, and an override's own flag wins where both apply.
 */
export async function assortmentFor(
  db: FieldKitDatabase,
  outletId: string,
  channelId: string,
): Promise<Map<string, boolean>> {
  const lines = await db.assortment.where("channelId").equals(channelId).toArray();
  const overrides = await db.assortmentOverrides.where("outletId").equals(outletId).toArray();

  const effective = new Map(lines.map((line) => [line.productId, line.isMustStock]));

  for (const over of overrides) {
    if (over.kind === "Removed") effective.delete(over.productId);
    else effective.set(over.productId, over.isMustStock);
  }

  return effective;
}
