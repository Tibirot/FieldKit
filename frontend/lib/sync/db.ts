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
