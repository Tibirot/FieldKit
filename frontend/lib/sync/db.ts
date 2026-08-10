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
 *
 * `code` is the tenant's own identifier for the shop, and it is not indexed. Adding a field needs
 * no migration; adding a *way to look it up* does, and nothing on the device searches by code yet —
 * the one screen that lists outlets by code is in the back office and asks the server. When a field
 * screen needs it, that is a version 3 and a deliberate one, not a line snuck into this type.
 */
export type ReferenceOutlet = {
  id: string;
  code: string;
  name: string;
  channelId: string;
  segment: string | null;
  status: string;
  latitude: number | null;
  longitude: number | null;
  /**
   * How close counts as at the shop (`OUT-08`), sent per outlet though constant server-side today.
   *
   * The device assesses the geofence itself — a rep in a shop with no signal still has to be told
   * whether they are inside it — and the server stores that verdict unmodified, so this number is
   * an *input to a record nothing will re-check*. Holding it as a TypeScript constant would agree
   * with the server exactly until `OUT-08` makes it per-outlet, and then disagree silently.
   */
  radiusMetres: number;
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

/**
 * One step of a visit workflow, as an admin configured it (`VIS-03`).
 *
 * `type` is the name, not the enum ordinal the server stores. Serialised, an ordinal would be
 * silently reinterpreted the day a value is inserted into the middle of that list — and every
 * device already holding a workflow would start opening the wrong sub-flow.
 */
export type ReferenceWorkflowStep = {
  order: number;
  type: string;
  mandatory: boolean;
  label: string;
};

/**
 * How a visit is worked in one channel (`VIS-03`, W8 slice 8b).
 *
 * The steps live *inside* the workflow rather than in a store of their own. A workflow is only ever
 * useful whole: a device holding four of five steps would run a visit that asks for less than the
 * tenant configured, and `BR-VIS-3` would gate check-out on a mandatory step it never received.
 */
export type ReferenceVisitWorkflow = {
  id: string;
  channelId: string;
  presenceExpected: boolean;
  steps: ReferenceWorkflowStep[];
  rowVersion: number;
};

/**
 * One product as the device holds it (`PRD-01`, W8 slice 8c).
 *
 * The whole tenant catalogue reaches every device — a rep standing in a shop has to be able to
 * *name* what they are looking at, including on an unplanned call or at a shop whose assortment
 * changed this morning. What they may **sell** is a different question, answered by the assortment.
 *
 * `status` travels rather than being filtered server-side: a device holding an order taken last week
 * still has to name a product the tenant has since discontinued.
 */
export type ReferenceProduct = {
  id: string;
  sku: string;
  name: string;
  brandId: string | null;
  categoryId: string | null;
  taxClassId: string | null;
  unitOfMeasure: string | null;
  packSize: number | null;
  status: string;
  rowVersion: number;
};

/**
 * One line of a channel's assortment (`PRD-02`, W8 slice 8d).
 *
 * Tenant-wide, like the catalogue: which products a channel carries is not something one rep may
 * know and another may not.
 */
export type ReferenceAssortmentLine = {
  id: string;
  channelId: string;
  productId: string;
  isMustStock: boolean;
  rowVersion: number;
};

/**
 * One outlet's departure from its channel's list (`PRD-02`, `B2`).
 *
 * Scoped to the outlets this device holds — the first entity that is. `kind` is a name rather than
 * an ordinal: an inserted enum value would turn every stored `Removed` into an `Added`, which is a
 * product appearing in an order screen that a buyer has explicitly refused.
 */
export type ReferenceAssortmentOverride = {
  id: string;
  outletId: string;
  productId: string;
  kind: string;
  isMustStock: boolean;
  rowVersion: number;
};

/**
 * A price list header (`PRD-03`).
 *
 * The effective window travels as ISO date strings, for the reason a planned visit's date does: a
 * price list is in effect on a *date*, in no timezone, and `BR-PRD-2` picks the list in effect on
 * the day of the order — which for a device working offline may not be the day it last synced.
 */
export type ReferencePriceList = {
  id: string;
  name: string;
  currency: string;
  effectiveFrom: string;
  effectiveTo: string | null;
  rowVersion: number;
};

/**
 * One product's price on one list.
 *
 * `amount` arrives as a number because that is what JSON has. Every calculation the device does with
 * it goes through `lib/pricing`'s Money, which is decimal.js — the parity suite exists because
 * float arithmetic on money is the one thing the two languages must never disagree about.
 */
export type ReferencePriceLine = {
  id: string;
  priceListId: string;
  productId: string;
  amount: number;
  rowVersion: number;
};

/** Which list applies where. Exactly one of the two ids is set (`BR-PRD-2`). */
export type ReferencePriceAssignment = {
  id: string;
  priceListId: string;
  channelId: string | null;
  outletId: string | null;
  rowVersion: number;
};

/** What a promotion applies to. Exactly one id is set; an empty list means "everything". */
export type ReferencePromotionTarget = { productId: string | null; categoryId: string | null };

/** One threshold of a volume promotion, ordered by `minQuantity`. */
export type ReferencePromotionTier = {
  minQuantity: number;
  percentOff: number | null;
  amountOff: number | null;
  currency: string | null;
};

/**
 * One promotion as the device holds it (`PRD-05`, W8 slice 8f).
 *
 * Targets and tiers live *inside* it, and the reason is sharper than the workflow's: a device
 * holding four of five tiers does not fail, it computes a **different discount** — and neither the
 * rep nor the shop has any way to notice.
 */
export type ReferencePromotion = {
  id: string;
  name: string;
  type: string;
  percentOff: number | null;
  amountOff: number | null;
  currency: string | null;
  buyQuantity: number | null;
  getQuantity: number | null;
  getPercentOff: number | null;
  getProductId: string | null;
  validFrom: string;
  validTo: string | null;
  priority: number;
  targets: ReferencePromotionTarget[];
  tiers: ReferencePromotionTier[];
  rowVersion: number;
};

/** Which promotion applies where. Exactly one of the two ids is set. */
export type ReferencePromotionAssignment = {
  id: string;
  promotionId: string;
  channelId: string | null;
  outletId: string | null;
  rowVersion: number;
};

/** Where a mutation has got to. The device's own state, never the server's. */
/**
 * One step of a visit as the rep works it (`VIS-03`).
 *
 * A copy of the workflow's step, not a reference to it. The workflow can be republished mid-visit —
 * `BR-VIS-6` — and a rep who checked in at ten must not be asked at four for a step that did not
 * exist when they walked in. The server's `VisitStep` copies for the same reason.
 */
export type LocalVisitStep = {
  stepId: string;
  order: number;
  type: string;
  mandatory: boolean;
  label: string;
  /** What the rep wrote. The whole content of a `Note` step, optional elsewhere. */
  notes: string | null;
  /** ISO-8601. Null until the rep does it. */
  completedAtUtc: string | null;
};

/**
 * A visit as the device holds it (`OFF-01`, `OFF-02`, `VIS-01`…`VIS-05`) — W9 slice 4.
 *
 * <b>The first thing in this database the device *authors*.</b> Every `ref_*` store is a copy of
 * something the server still has — lose it and the next pull rebuilds it — and the outbox holds
 * opaque payloads written once and thereafter only marked. This is neither: it is created on
 * check-in, mutated repeatedly as a rep works the steps, and only becomes an outbox mutation when
 * it is sealed. It is the first store where losing a row loses *work*, which is where `OFF-02`'s
 * "durable" stops being a word.
 *
 * <b>The shape is the server's `CapturedVisit`, not a client convenience.</b> Sealing it is then a
 * projection rather than a translation — and a field this type is missing is a field the push
 * cannot send, which is a compile error rather than a silently thinner record.
 *
 * <b>`id` is minted here.</b> It is the visit's identity on both sides: the ledger makes a replayed
 * *mutation* free, and this makes a replayed *visit* recognisable even if a mutation id were ever
 * lost — `VisitIngestService` answers `AlreadyExists` on it.
 */
export type LocalVisit = {
  id: string;
  outletId: string;
  /** The planned call this fulfils, when there was one (`JRN-04`). */
  plannedVisitId: string | null;
  status: LocalVisitStatus;
  checkedInAtUtc: string;
  checkInLatitude: number | null;
  checkInLongitude: number | null;
  /** What the *device* measured, and what the server stores unmodified (`VIS-01`). */
  checkInDistanceMetres: number | null;
  wasInsideGeofence: boolean;
  overrideReason: string | null;
  steps: LocalVisitStep[];
  checkedOutAtUtc: string | null;
  checkOutLatitude: number | null;
  checkOutLongitude: number | null;
  outcome: string | null;
  outcomeReason: string | null;
};

/**
 * Where a visit has got to on the device.
 *
 * Two values, like the server's `VisitStatus`. There is deliberately no `sent` — whether the back
 * office has it is the *outbox's* question, and a visit carrying its own copy of that answer would
 * be a second place to keep it in step. `SyncBadge` already reads it from the outbox by subject id.
 */
export type LocalVisitStatus = "inProgress" | "checkedOut";

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
  workflows!: EntityTable<ReferenceVisitWorkflow, "id">;
  products!: EntityTable<ReferenceProduct, "id">;
  assortment!: EntityTable<ReferenceAssortmentLine, "id">;
  assortmentOverrides!: EntityTable<ReferenceAssortmentOverride, "id">;
  priceLists!: EntityTable<ReferencePriceList, "id">;
  priceLines!: EntityTable<ReferencePriceLine, "id">;
  priceAssignments!: EntityTable<ReferencePriceAssignment, "id">;
  promotions!: EntityTable<ReferencePromotion, "id">;
  promotionAssignments!: EntityTable<ReferencePromotionAssignment, "id">;
  visits!: EntityTable<LocalVisit, "id">;
  outbox!: EntityTable<OutboxEntry, "mutationId">;
  meta!: EntityTable<MetaEntry, "key">;
  watermarks!: EntityTable<Watermark, "entity">;

  constructor(name: string) {
    super(name);

    /*
     * Version 1 — the schema as W8 slice 6 shipped it.
     *
     * Dexie's schema strings list *indexes*, not columns — the object is stored whole, and only
     * what is named here can be queried. Adding a field to a type above needs no migration; adding
     * a way to *look it up* does.
     *
     * <b>Every version stays declared, forever, even once nothing installs it fresh.</b> Dexie
     * replays them in order to bring an existing database forward, so deleting version 1 would not
     * simplify this file — it would strand every device that has not opened the app since.
     */
    this.version(1).stores({
      // `name` is indexed because that is what a rep types when looking for a shop; `channelId`
      // because a visit's workflow is chosen by channel.
      ref_outlets: "id, name, channelId",

      // `date` is the index the whole field app turns on — *Today's Journey* is one range query on
      // it. `outletId` answers the other direction: "is this shop on my round?", asked from an
      // outlet screen.
      ref_planned_visits: "id, date, outletId",

      // `channelId` is unique per tenant server-side and is the only way anything looks a workflow
      // up — a visit asks "how is this channel worked". Declared unique here too, so a bug that
      // stored two for one channel fails loudly rather than picking one arbitrarily.
      ref_visit_workflows: "id, &channelId",

      // `sku` is what a rep types or scans and `name` is what they read — both are how the
      // catalogue gets searched. `status` is indexed here where the outlet store's is not: this is
      // the one store big enough for "active only" to be worth an index rather than a scan.
      ref_products: "id, sku, name, status",

      // Indexed by `channelId`, because that is the only question anyone asks of it: "what does
      // this shop's channel carry". `productId` is not indexed — nothing asks the reverse.
      ref_assortment: "id, channelId",

      // `outletId` because the resolution reads one shop's exceptions, and because it is what the
      // cascade prune deletes by when an outlet leaves the rep's territory.
      ref_assortment_overrides: "id, outletId",

      // No index beyond the key: a tenant has a handful of price lists and resolution reads them
      // all to find the one in effect.
      ref_price_lists: "id",

      // Compound, because the one question asked of this store is "what does this list charge for
      // this product" — and a single-column index on either half would scan the rest.
      ref_price_lines: "id, [priceListId+productId]",

      // Both directions are asked: outlet assignments win over channel ones (`BR-PRD-2`), so
      // resolution looks for the outlet's first and falls back to its channel's.
      ref_price_assignments: "id, outletId, channelId",

      // No index beyond the key. Resolution reads every promotion assigned to the shop and sorts by
      // priority — a tenant runs a handful at a time, and `validFrom` is not an index because the
      // window is checked against the *order's* date rather than queried by range.
      ref_promotions: "id",
      ref_promotion_assignments: "id, outletId, channelId",

      // Indexed by status (the sync manager asks for pending), by createdAt (it sends them in the
      // order the rep worked), and by subjectId (a screen asks about one visit).
      outbox: "mutationId, status, createdAt, subjectId",

      meta: "key",
      watermarks: "entity",
    });

    /*
     * Version 2 — the outbox drain reads an index instead of sorting in memory (`OFF-13`).
     *
     * `pending()` asked for `where("status").equals("pending").sortBy("createdAt")`. Dexie's
     * `sortBy` is not an index read: it loads every matching row and sorts them in JavaScript. That
     * is the *hot path* — it runs at the top of every push, on the device with the least CPU and the
     * most reason to be quick — and it gets slower exactly as a rep's offline day gets longer.
     *
     * A compound `[status+createdAt]` index turns it into a range scan that arrives ordered.
     *
     * <b>No `upgrade()` callback, deliberately.</b> Dexie builds a new index by walking the existing
     * rows; nothing about them changes, so there is no data to transform, and an empty upgrade block
     * would be a hook somebody later fills in by accident. The callback is for *transforms* — a
     * renamed field, a split table — and this is not one.
     *
     * What this version is really for is `migration.test.ts`: `OFF-13` says an app update must not
     * strand a pending outbox, and until there were two versions that was a claim with nothing
     * behind it. Now a v1 database with unsent work gets opened by v2 code in a test, and the work
     * is still there.
     */
    this.version(2).stores({
      outbox: "mutationId, status, createdAt, subjectId, [status+createdAt]",
    });

    /*
     * Version 3 — `OutletSnapshot` gained `code`, so every outlet already on a device is stale.
     *
     * <b>This is the first version with an `upgrade()`, and the first one that had to have one.</b>
     * Version 2 added an index over rows that were already correct; this one does not change the
     * schema at all — no new index, no renamed field — and would look like nothing if the
     * `.stores({})` were the whole story. What changed is the *wire*, one layer up: a device that
     * synced under W8 holds outlet rows with no `code`, and nothing would ever fix them. The delta
     * only carries outlets whose row version moved, so an unedited shop keeps its codeless row
     * until somebody in the back office happens to touch it — which is to say, indefinitely, and
     * differently per device.
     *
     * Clearing the outlets watermark makes the next pull re-baseline them. Cursor 0 means "I have
     * nothing", the server answers with the rep's whole territory, and `applyOutletChanges` upserts
     * over the codeless rows in one transaction. It costs one full outlet page on the first sync
     * after the update — tens to hundreds of rows for one rep — and it is self-healing: if the app
     * is closed before that pull finishes, the watermark is still 0 and the next one does it again.
     *
     * <b>Only the outlets row is deleted</b>, not the table. Every other entity's watermark is
     * still accurate, and clearing them all would re-download the catalogue, the prices and the
     * promotions to fix a field on one entity — the exact cost the per-entity cursors in §3 exist
     * to avoid.
     *
     * The rows themselves are left alone rather than deleted. A device that goes offline between
     * the upgrade and the next successful pull keeps a territory it can still work — outlets with
     * a name and no code, which is what it had yesterday — where an emptied store would give a rep
     * an app with no shops in it and no way to get them back until they found signal.
     */
    this.version(3)
      .stores({})
      .upgrade((tx) => tx.table("watermarks").delete("outlets"));

    /*
     * Version 4 — `OutletSnapshot` gained `radiusMetres`, which is version 3 again for a new field.
     *
     * <b>Two versions doing the same thing is the point, not a smell.</b> A device that upgrades
     * straight from 2 to 4 runs both and clears the same watermark twice, which costs nothing; a
     * device already on 3 runs only this one and gets the re-baseline it needs. Folding them into
     * one version by editing 3 would be the mistake — Dexie skips a version a database has already
     * seen, so a device on 3 would never re-fetch, and the field it is missing is the one the
     * geofence check reads.
     *
     * <b>Why this field is worth a re-baseline at all</b>, when a stale outlet is usually harmless:
     * the device assesses the geofence, and `IVisitIngest` stores its verdict *unmodified*. An
     * outlet row with no radius makes that assessment on `undefined` — every check-in either inside
     * or outside depending on how the comparison coerces — and nothing downstream re-checks it. The
     * rule the sync engine takes from this: a re-baseline is cheap, and a wrong answer a rep cannot
     * see is not.
     */
    this.version(4)
      .stores({})
      .upgrade((tx) => tx.table("watermarks").delete("outlets"));

    /*
     * Version 5 — the visits store (`OFF-01`, W9 slice 4).
     *
     * <b>The first store added since v1, and the first that is not a copy of something the server
     * holds.</b> No `upgrade()`: there is nothing to transform, because there were no visits before
     * this version. Dexie creates the table and every existing device carries on with its outbox
     * and its reference data untouched — which is the whole of `OFF-13`'s promise for a device that
     * updates mid-day with work still queued.
     *
     * Two indexes, and only two:
     *
     * - `status`, because the app's most frequent question is "is a visit open on this device" —
     *   asked on every field screen to decide whether the rep is mid-visit.
     * - `outletId`, because a shop's row in Today's Journey shows whether it has been worked.
     *
     * `checkedInAtUtc` is *not* indexed. Sorting a rep's day is a sort of at most a couple of dozen
     * rows, and an index that exists to order a list that short is a write cost on every step
     * completion for a read nobody notices.
     */
    this.version(5).stores({
      visits: "id, status, outletId",
    });

    this.outlets = this.table("ref_outlets");
    this.plannedVisits = this.table("ref_planned_visits");
    this.workflows = this.table("ref_visit_workflows");
    this.products = this.table("ref_products");
    this.assortment = this.table("ref_assortment");
    this.assortmentOverrides = this.table("ref_assortment_overrides");
    this.priceLists = this.table("ref_price_lists");
    this.priceLines = this.table("ref_price_lines");
    this.priceAssignments = this.table("ref_price_assignments");
    this.promotions = this.table("ref_promotions");
    this.promotionAssignments = this.table("ref_promotion_assignments");
    this.visits = this.table("visits");
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
