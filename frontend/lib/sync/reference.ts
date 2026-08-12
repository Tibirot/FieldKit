import type { Table } from "dexie";

import { resolveOrderMinimum } from "@/lib/pricing/order-minimum";
import type {
  OrderMinimumCandidate,
  OrderMinimumScope,
  ResolvedOrderMinimum,
} from "@/lib/pricing/order-minimum";
import { resolveTaxRate } from "@/lib/pricing/tax";

import type {
  FieldKitDatabase,
  ReferenceAssortmentLine,
  ReferenceAssortmentOverride,
  ReferenceOrderMinimum,
  ReferenceOutlet,
  ReferencePriceAssignment,
  ReferencePriceLine,
  ReferencePriceList,
  ReferencePromotion,
  ReferencePromotionAssignment,
  ReferencePlannedVisit,
  ReferenceProduct,
  ReferenceScoreWeightSet,
  ReferenceSurveyForm,
  ReferenceTaxRate,
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
export const PRICE_LISTS = "priceLists";
export const PRICE_LINES = "priceLines";
export const PRICE_ASSIGNMENTS = "priceAssignments";
export const PROMOTIONS = "promotions";
export const PROMOTION_ASSIGNMENTS = "promotionAssignments";
export const SURVEYS = "surveys";
export const SCORE_WEIGHTS = "scoreWeights";

/** The watermark key for tax rates (`PRD-07`, W11 slice 7b). */
export const TAX_RATES = "taxRates";

/** The watermark key for order minimums (`ORD-06`, W11 slice 8b-ii). */
export const ORDER_MINIMUMS = "orderMinimums";

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

/** Price list headers. */
export function applyPriceListChanges(
  db: FieldKitDatabase,
  page: EntityChanges<ReferencePriceList>,
): Promise<void> {
  return apply(db, "ref_price_lists", PRICE_LISTS, page);
}

/** The prices themselves — the largest thing this protocol carries. */
export function applyPriceLineChanges(
  db: FieldKitDatabase,
  page: EntityChanges<ReferencePriceLine>,
): Promise<void> {
  return apply(db, "ref_price_lines", PRICE_LINES, page);
}

/** Which list applies where. */
export function applyPriceAssignmentChanges(
  db: FieldKitDatabase,
  page: EntityChanges<ReferencePriceAssignment>,
): Promise<void> {
  return apply(db, "ref_price_assignments", PRICE_ASSIGNMENTS, page);
}

/** Drops assignments for outlets this device no longer holds — the cascade the overrides get. */
export async function pruneOutletPriceAssignments(db: FieldKitDatabase): Promise<number> {
  const held = new Set((await db.outlets.toArray()).map((row) => row.id));
  const orphans = await db.priceAssignments
    .filter((assignment) => assignment.outletId !== null && !held.has(assignment.outletId))
    .primaryKeys();

  if (orphans.length === 0) return 0;

  await db.priceAssignments.bulkDelete(orphans);

  return orphans.length;
}

/*
 * `priceListFor` and `priceOf` were here until W11 slice 7d, and their removal is the point rather
 * than a tidy-up.
 *
 * `priceListFor` answered `BR-PRD-2` — outlet beats channel, then the effective window — by taking
 * the first assignment the index handed back, which was right by *ordering* because outlet
 * assignments were queried first. The shared `resolvePrice` answers the same rule by scope rank,
 * then the later `effectiveFrom`, then the id. The two agree until two lists tie, at which point the
 * device picks whatever IndexedDB returned and the server picks deterministically — and neither
 * looks wrong.
 *
 * Neither had a caller: they shipped in W8 slice 8e as store queries ahead of the screen that would
 * use one, and the screen's pricing now goes through `lib/orders/pricing.ts`, which gathers
 * candidates and hands them to the resolver the vectors check. Keeping a second answer to a priced
 * question is what slice 4b refused server-side for the assortment, for the same reason: one path
 * calling a line 8.00 while the other calls it 10.00 is not a thing to leave lying around.
 *
 * Their two real cases — outlet over channel, and the day the order is for — moved to
 * `lib/orders/pricing.test.ts` rather than being deleted with them.
 */

/** The tenant's promotions, each whole. */
export function applyPromotionChanges(
  db: FieldKitDatabase,
  page: EntityChanges<ReferencePromotion>,
): Promise<void> {
  return apply(db, "ref_promotions", PROMOTIONS, page);
}

/** Which promotion applies where. */
export function applyPromotionAssignmentChanges(
  db: FieldKitDatabase,
  page: EntityChanges<ReferencePromotionAssignment>,
): Promise<void> {
  return apply(db, "ref_promotion_assignments", PROMOTION_ASSIGNMENTS, page);
}

/** Drops promotion assignments for outlets this device no longer holds. */
export async function pruneOutletPromotionAssignments(db: FieldKitDatabase): Promise<number> {
  const held = new Set((await db.outlets.toArray()).map((row) => row.id));
  const orphans = await db.promotionAssignments
    .filter((assignment) => assignment.outletId !== null && !held.has(assignment.outletId))
    .primaryKeys();

  if (orphans.length === 0) return 0;

  await db.promotionAssignments.bulkDelete(orphans);

  return orphans.length;
}

/**
 * The promotions running at one outlet on one date (`PRD-06`, `BR-PRD-4`).
 *
 * <b>Highest priority first</b>, which is the order the resolver applies them in. Both the outlet's
 * own assignments and its channel's count — unlike prices, where the outlet's assignment *replaces*
 * the channel's; a promotion is an offer, and offers accumulate until the resolver decides which
 * ones stack.
 *
 * `on` is the **order's** date, not today's: a device pricing an order dated last Tuesday needs the
 * promotion that was running last Tuesday, which is why expired ones are held rather than filtered
 * out of the pull.
 */
export async function promotionsFor(
  db: FieldKitDatabase,
  outletId: string,
  channelId: string,
  on: string,
): Promise<ReferencePromotion[]> {
  const assignments = [
    ...(await db.promotionAssignments.where("outletId").equals(outletId).toArray()),
    ...(await db.promotionAssignments.where("channelId").equals(channelId).toArray()),
  ];

  const ids = [...new Set(assignments.map((assignment) => assignment.promotionId))];
  const promotions = await db.promotions.bulkGet(ids);

  return promotions
    .filter((promotion): promotion is ReferencePromotion => promotion !== undefined)
    .filter((promotion) => promotion.validFrom <= on && (promotion.validTo === null || on <= promotion.validTo))
    .sort((left, right) => right.priority - left.priority);
}

/**
 * The tenant's survey forms (`AUD-04`, W10 slice 7).
 *
 * Its own transaction, like every other entity: a device that got one page of a pull keeps it
 * whatever happened to the rest.
 */
export function applySurveyChanges(
  db: FieldKitDatabase,
  page: EntityChanges<ReferenceSurveyForm>,
): Promise<void> {
  return apply(db, "ref_surveys", SURVEYS, page);
}

/** One questionnaire, by the id an audit names. */
export function surveyForm(
  db: FieldKitDatabase,
  id: string,
): Promise<ReferenceSurveyForm | undefined> {
  return db.surveys.get(id);
}

/** Every questionnaire this tenant has, by name — what an audit screen offers. */
export function surveyForms(db: FieldKitDatabase): Promise<ReferenceSurveyForm[]> {
  return db.surveys.orderBy("name").toArray();
}

/** The published perfect-store weightings (`AUD-06`, `BR-AUD-8`). */
export function applyScoreWeightChanges(
  db: FieldKitDatabase,
  page: EntityChanges<ReferenceScoreWeightSet>,
): Promise<void> {
  return apply(db, "ref_score_weights", SCORE_WEIGHTS, page);
}

/** One page of tax rates (`PRD-07`, W11 slice 7b). */
export function applyTaxRateChanges(
  db: FieldKitDatabase,
  page: EntityChanges<ReferenceTaxRate>,
): Promise<void> {
  return apply(db, "ref_tax_rates", TAX_RATES, page);
}

/** One page of order minimums (`ORD-06`, W11 slice 8b-ii). */
export function applyOrderMinimumChanges(
  db: FieldKitDatabase,
  page: EntityChanges<ReferenceOrderMinimum>,
): Promise<void> {
  return apply(db, "ref_order_minimums", ORDER_MINIMUMS, page);
}

/**
 * The minimum this outlet's order must meet, or **null for none** (`ORD-06`, `BR-ORD-5`) — W11
 * slice 8b-ii.
 *
 * <b>Two queries, not one.</b> A minimum is scoped to the outlet *or* to its channel, and the two are
 * indexed separately because exactly one of them is set on any row — a compound index would key a
 * pair that is never whole. Both are read and `resolveOrderMinimum` ranks them, which keeps the
 * precedence rule in the one place both languages check it.
 *
 * <b>Null is the ordinary answer.</b> `BR-ORD-5` applies a minimum *if configured*, most tenants
 * configure none, and a device whose first pull has not landed holds none either — all three mean
 * every order passes, which is the behaviour every order has had until this slice.
 *
 * <b>An outlet the device has never heard of also answers null</b>, rather than throwing. A rep
 * cannot be standing in a shop that is not on their device; if they are, the order is going to be
 * refused on push for reasons a minimum has nothing to say about.
 */
export async function orderMinimumFor(
  db: FieldKitDatabase,
  outletId: string,
): Promise<ResolvedOrderMinimum | null> {
  const shop = await db.outlets.get(outletId);
  if (!shop) return null;

  const [own, channelWide] = await Promise.all([
    db.orderMinimums.where("outletId").equals(outletId).toArray(),
    shop.channelId
      ? db.orderMinimums.where("channelId").equals(shop.channelId).toArray()
      : Promise.resolve([]),
  ]);

  return resolveOrderMinimum([
    ...own.map((row) => minimumCandidate(row, "Outlet")),
    ...channelWide.map((row) => minimumCandidate(row, "Channel")),
  ]);
}

function minimumCandidate(
  row: ReferenceOrderMinimum,
  scope: OrderMinimumScope,
): OrderMinimumCandidate {
  return {
    orderMinimumId: row.id,
    scope,
    currencyCode: row.currencyCode,
    amount: row.amount,
  };
}

/**
 * Every rate this country charges this class, for `resolveTaxRate` to pick from (`PRD-07`).
 *
 * <b>Every rate, not the one in force today.</b> `BR-PRD-6` resolves against the *order's* date, and
 * a device may be pricing one captured before a VAT change — so the window cannot be filtered here
 * and cannot be an index. The compound `[countryCode+taxClassId]` narrows it to the handful that
 * could possibly apply, and the resolver decides which of them does.
 *
 * <b>An empty answer is "unknown", not "zero".</b> `priceLine` treats a null rate as unknown and
 * charges nothing, which is the same total a genuine 0% rate produces — and the caller keeps the
 * distinction, because a tenant that has authored no rate for a country is misconfigured while one
 * that has authored 0% is describing zero-rated goods.
 */
export function taxRatesFor(
  db: FieldKitDatabase,
  countryCode: string,
  taxClassId: string,
): Promise<ReferenceTaxRate[]> {
  return db.taxRates
    .where("[countryCode+taxClassId]")
    .equals([countryCode.toUpperCase(), taxClassId])
    .toArray();
}

/**
 * What tax this product carries at this shop on this day, or **null for unknown** (`PRD-07`,
 * `BR-PRD-5/6`) — W11 slice 7c.
 *
 * <b>The join slice 7b could not make.</b> Rates reached the device a slice ago and sat unusable:
 * a rate is a fact about a country and a class, and nothing on the device could say which country
 * the shop belonged to. `OutletSnapshot.countryCode` is the missing half, and this is the only
 * function that reads it.
 *
 * <b>Three ways to answer null, and they are deliberately the same answer.</b> The shop has no
 * country (its address was never completed), the product has no tax class (nobody said what kind of
 * thing it is), or nobody has authored a rate for that pair. All three mean *the device does not
 * know what this is taxed at* — and `priceLine` charges nothing for a null, which is the same total
 * a genuine `"0.00"` rate produces. That collapse is safe **only** because the caller keeps the
 * distinction: a 0% rate is a tenant describing zero-rated goods, a null is a tenant who has not
 * finished setting up, and the server draws the same line (`TaxEndpoints`).
 *
 * The mirror of the server's `/api/products/outlets/{id}/tax`, and it has to stay one: `BR-ORD-2`
 * has the rep's total and the server's recomputation agree to the cent, and tax is the last
 * multiplication on a line.
 *
 * @param on The **order's** date as `YYYY-MM-DD`, not today's. A device syncs a week of work.
 */
export async function taxPercentageFor(
  db: FieldKitDatabase,
  outletId: string,
  productId: string,
  on: string,
): Promise<string | null> {
  const shop = await db.outlets.get(outletId);
  if (!shop?.countryCode) return null;

  const item = await db.products.get(productId);
  if (!item?.taxClassId) return null;

  const rates = await taxRatesFor(db, shop.countryCode, item.taxClassId);

  // The window is decided here rather than in the query, by the shared resolver both languages run.
  const resolved = resolveTaxRate(
    rates.map((rate) => ({
      taxRateId: rate.id,
      percentage: rate.percentage,
      effectiveFrom: rate.effectiveFrom,
      effectiveTo: rate.effectiveTo,
    })),
    on,
  );

  return resolved?.percentage ?? null;
}

/**
 * The weighting an audit names (`BR-AUD-8`).
 *
 * <b>By version, never "the newest".</b> An audit records the version it was scored against, and a
 * device showing a queued audit's breakdown has to use *that* one — otherwise a re-weighting that
 * synced overnight would silently restate what the rep saw yesterday, and the number on the screen
 * would stop matching the number the server will store.
 */
export function scoreWeightSet(
  db: FieldKitDatabase,
  version: number,
): Promise<ReferenceScoreWeightSet | undefined> {
  return db.scoreWeights.where("version").equals(version).first();
}

/**
 * The newest published weighting — what a *new* audit is scored against.
 *
 * The one place "the latest" is the right question, and it is asked at capture time only: the
 * version it returns is then written onto the audit, and every later read goes through
 * `scoreWeightSet` instead. Undefined when the tenant has published none, which is a real state — a
 * device can hold a rep's whole round before an administrator has ever opened the weights screen —
 * and the caller decides what an audit means without one.
 */
export function currentScoreWeightSet(
  db: FieldKitDatabase,
): Promise<ReferenceScoreWeightSet | undefined> {
  return db.scoreWeights.orderBy("version").last();
}
