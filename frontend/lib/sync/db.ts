import Dexie, { type EntityTable } from "dexie";

/**
 * One outlet as the device holds it (`OFF-02`, sync engine §2).
 *
 * The wire shape of `OutletSnapshot`, stored as it arrives. Not re-modelled into something the UI
 * would prefer: the server decides what a device's copy of an outlet contains, and a client-side
 * projection here would be a second place that has to change when it does.
 *
 * `rowVersion` travels with the row rather than only in the cursor, so a page that overlaps what
 * the device already holds can be answered per row rather than by trusting arrival order.
 */
export type ReferenceOutlet = {
  id: string;
  name: string;
  channelId: string;
  segment: string | null;
  status: string;
  latitude: number | null;
  longitude: number | null;
  rowVersion: number;
};

/**
 * One call on the rep's round, as the device holds it (`JRN-05`, W8 slice 8a).
 *
 * `date` is an ISO `yyyy-mm-dd` string rather than a `Date`, and that is load-bearing: a planned
 * call is a *date*, in no timezone (the plan says so), and storing it as a `Date` would make
 * "today's round" depend on where the phone thinks it is. It is also what makes `date` a usable
 * IndexedDB index — strings sort lexicographically, which for ISO dates is chronological order.
 */
export type ReferencePlannedVisit = {
  id: string;
  outletId: string;
  date: string;
  status: string;
  source: string;
  notVisitedReason: string | null;
  rowVersion: number;
};

/** Where a mutation has got to. The device's own state, never the server's. */
export type OutboxStatus =
  /** Captured and durable. Waiting for a connection. */
  | "pending"
  /** Sent, and no answer yet. The dangerous state — see `reclaimInflight`. */
  | "inflight"
  /** The server refused it on its merits. It needs a person, not a retry (`OFF-09`). */
  | "failed";

/**
 * One thing the rep did that the server has not yet answered for (`OFF-04`).
 *
 * <b>There is no `acked`.</b> The spec's status list has one, and it is not here on purpose: a row
 * whose only content is "this is finished" is a table that grows for the life of the install with
 * nothing reading it. An accepted mutation is *deleted*, and the record of the work is the visit
 * itself — which the device already holds and the server now agrees about.
 *
 * What survives is the two states somebody still has a question about: `pending`, which the sync
 * manager retries, and `failed`, which a rep has to look at.
 */
export type OutboxEntry = {
  /**
   * Minted here, on the device, before anything is sent. It is the ledger's key server-side and the
   * whole basis of a free retry — see `enqueue`.
   */
  mutationId: string;
  /** The server's discriminator, e.g. `CapturedVisit`. Matched exactly; not a display string. */
  type: string;
  /** The entity id inside the payload, so a screen can ask "is *this* visit still pending?". */
  subjectId: string;
  payload: unknown;
  status: OutboxStatus;
  /** Epoch millis. Ordering is by capture, so the server sees a rep's day in the order it happened. */
  createdAt: number;
  attempts: number;
  /** An `ADR-0012` code when the server refused, so the UI can translate rather than print prose. */
  errorCode?: string;
  errorDetail?: string;
};

/** The keys `meta` is addressed by. A union rather than `string`, so a typo fails to compile. */
export type MetaKey =
  /** The device this browser is bound to, from `POST /api/sync/devices`. */
  | "deviceId"
  /** The `entity#cursor` string the last pull was taken at (sync engine §3). */
  | "snapshotVersion"
  /** Epoch millis of the last completed sync run, for the "last synced" line. */
  | "lastSyncAt";

export type MetaEntry = { key: MetaKey; value: string };

/**
 * A watermark: how far this device has been told about one entity (sync engine §3).
 *
 * Its own store rather than a `meta` row per entity, because it is written in the *same
 * transaction* as the rows it describes and that is easier to reason about when the thing being
 * written is a row with a schema instead of a stringly-typed blob.
 */
export type Watermark = { entity: string; cursor: number };

/**
 * The device's local database (`OFF-02`).
 *
 * <b>One database per signed-in user, not one per app.</b> The name carries tenant and subject, so
 * a rep signing in on a colleague's tablet gets an empty store rather than the colleague's
 * territory. Server-side, tenant isolation is a query filter nobody can bypass; on the device the
 * equivalent is that the data was never in the same database to begin with. Sharing one database
 * and filtering by a column would put the guarantee in application code that a bug can skip.
 *
 * It also makes sign-out cheap and total: delete the database.
 */
export class FieldKitDatabase extends Dexie {
  outlets!: EntityTable<ReferenceOutlet, "id">;
  plannedVisits!: EntityTable<ReferencePlannedVisit, "id">;
  outbox!: EntityTable<OutboxEntry, "mutationId">;
  meta!: EntityTable<MetaEntry, "key">;
  watermarks!: EntityTable<Watermark, "entity">;

  constructor(name: string) {
    super(name);

    /*
     * Version 1.
     *
     * Dexie's schema strings list *indexes*, not columns — the object is stored whole, and only
     * what is named here can be queried. Adding a field to a type above needs no migration; adding
     * a way to *look it up* does.
     *
     * `OFF-13` (a schema change must not strand a pending outbox) is slice 11, and lands as a
     * `version(2).upgrade(...)` beside this. The version is declared now, rather than left implicit,
     * so that slice has something to move from.
     */
    this.version(1).stores({
      // `name` is indexed because that is what a rep types when looking for a shop; `channelId`
      // because a visit's workflow is chosen by channel.
      ref_outlets: "id, name, channelId",

      // `date` is the index the whole field app turns on — *Today's Journey* is one range query on
      // it. `outletId` answers the other direction: "is this shop on my round?", asked from an
      // outlet screen.
      ref_planned_visits: "id, date, outletId",

      // Indexed by status (the sync manager asks for pending), by createdAt (it sends them in the
      // order the rep worked), and by subjectId (a screen asks about one visit).
      outbox: "mutationId, status, createdAt, subjectId",

      meta: "key",
      watermarks: "entity",
    });

    this.outlets = this.table("ref_outlets");
    this.plannedVisits = this.table("ref_planned_visits");
    this.outbox = this.table("outbox");
    this.meta = this.table("meta");
    this.watermarks = this.table("watermarks");
  }
}

/**
 * The database name for one signed-in user.
 *
 * Both parts are opaque to us — a Keycloak subject is a UUID and a tenant is a slug — so they are
 * used as given. The prefix exists so a developer with several apps on `localhost` can tell whose
 * storage is whose.
 */
export function databaseName(tenant: string, subject: string): string {
  return `fieldkit:${tenant}:${subject}`;
}

let open: { name: string; db: FieldKitDatabase } | null = null;

/**
 * The open database for this user, opening it if needed.
 *
 * Cached by name so repeated calls in one session share a connection — Dexie holds an IndexedDB
 * handle, and opening a second one for the same name is wasted work rather than an error. A
 * *different* name closes the first: that is a different user signing in, and leaving the previous
 * rep's connection open would keep their data reachable from a stale reference.
 */
export function openDatabase(tenant: string, subject: string): FieldKitDatabase {
  const name = databaseName(tenant, subject);

  if (open?.name === name) return open.db;

  open?.db.close();
  open = { name, db: new FieldKitDatabase(name) };

  return open.db;
}

/** Forgets the cached handle. For sign-out, and for a test that wants a fresh database. */
export function closeDatabase(): void {
  open?.db.close();
  open = null;
}

/**
 * Asks the browser to keep this origin's storage (`OFF-02`, sync engine §2).
 *
 * <b>A request, not a guarantee, and the return value says which you got.</b> Browsers — iOS
 * Safari in particular — evict IndexedDB under storage pressure or inactivity policies, and an
 * installed PWA is treated more kindly than a tab. Chrome grants this silently on an engaged
 * origin; Firefox prompts; Safari decides on its own.
 *
 * Called on device bind, which is the moment the answer is worth having: a rep who is told "no"
 * before their first offline day can be asked to install the app, which is the thing that actually
 * changes the answer. Asking later, with a full outbox, is asking too late.
 */
export async function requestPersistentStorage(): Promise<boolean> {
  if (typeof navigator === "undefined" || !navigator.storage?.persist) return false;

  // Already granted, on a repeat call. `persist()` would return the same answer, but `persisted()`
  // does not risk a second prompt in the browsers that show one.
  if (await navigator.storage.persisted?.()) return true;

  return navigator.storage.persist();
}
