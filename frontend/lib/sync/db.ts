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
  /**
   * Which jurisdiction taxes this shop (`PRD-07`, W11 slice 7c) — ISO-3166-1 alpha-2, upper-cased.
   *
   * The shop's half of a tax match. Slice 7b put the rates on the device and left them unusable
   * without it: a rate is a fact about a country and a class, and nothing here could say which
   * country. Not the rep's and not the tenant's — a tenant selling across a border has reps who
   * cross it.
   *
   * **Null is *unknown*, not untaxed.** An address is optional server-side, so a shop entered
   * without one has no country — and `priceLine` reads a null rate as unknown and charges nothing,
   * which is the same total a genuine 0% rate produces. The caller keeps the distinction.
   */
  countryCode: string | null;
  /**
   * The IANA zone this shop trades in (`BR-PRD-6`, regression F6) — W11½ R6.
   *
   * **Which day a price list runs on is a question about the shop, not the phone.** The device used
   * to date its pricing by the rep's own local day and the server re-priced by the UTC day — two
   * different rules, so a rep in Bucharest before 03:00 was reported as disagreeing with a server
   * that had asked a different question. Both sides now ask the shop.
   *
   * **Empty means "this row predates R6 and has not been re-pulled yet"**, and is the one value the
   * server can never send — `Outlet.TimeZoneId` is required and IANA-validated. A caller that cannot
   * resolve a zone declines to answer rather than guessing UTC, which would reintroduce the defect
   * for exactly the shops the migration had not reached.
   */
  timeZoneId: string;
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
 * One question on a survey form (`AUD-04`, W10 slice 7).
 *
 * `type` is a name for the reason a workflow step's is. `key` is what an answer is filed under and
 * survives the form being re-worded — see `SurveyQuestion.Key` on the server for why an id would not.
 */
export type ReferenceSurveyQuestion = {
  order: number;
  key: string;
  text: string;
  type: string;
  mandatory: boolean;
  options: string[];
};

/**
 * A tenant's questionnaire (`AUD-04`, `CFG-04`, W10 slice 7).
 *
 * The questions live *inside* the form, like a workflow's steps: a device holding four of five would
 * ask a rep less than the tenant configured, and `BR-AUD-7` would gate the audit step on a mandatory
 * question it never received.
 */
export type ReferenceSurveyForm = {
  id: string;
  name: string;
  questions: ReferenceSurveyQuestion[];
  rowVersion: number;
};

/**
 * One pillar's weight (`AUD-06`, `BR-AUD-4`).
 *
 * `percentage` is a **string**, not a number, and that is the single most load-bearing decision on
 * this type. `BR-AUD-5` has the device's score match the server's exactly; `decimal.js` reads a
 * string, and a number would already have been through IEEE-754 before the scorer saw it. The same
 * rule the parity vectors enforce, applied to the data the device stores.
 */
export type ReferenceScoreWeight = {
  pillar: string;
  percentage: string;
};

/**
 * One published perfect-store weighting (`AUD-06`, `BR-AUD-8`).
 *
 * **Every published version is held, not just the newest.** An audit records the version it was
 * scored against, so a device with a queued audit from last week still has to be able to show the
 * rep what it scored — and a published set is immutable, so each version arrives exactly once and
 * never changes.
 */
export type ReferenceScoreWeightSet = {
  id: string;
  version: number;
  publishedAtUtc: string;
  weights: ReferenceScoreWeight[];
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
 * `amount` is a decimal **string**, and this comment used to say the opposite.
 *
 * It read: "`amount` arrives as a number because that is what JSON has. Every calculation goes
 * through Money, which is decimal.js — the parity suite exists because float arithmetic on money is
 * the one thing the two languages must never disagree about." Every clause of that is true and the
 * conclusion was backwards: `JSON.parse` had already made it an IEEE-754 float *before* `Money` could
 * read it, so the engine was decimal-exact over a value that was not. The parity suite could not see
 * it because it feeds the engine strings from a file and never touches a pull feed.
 *
 * Fixed at the source in W11 slice 7a — `PriceLineSnapshot.Amount` is a string on the wire — and
 * version 8 re-baselines the price watermarks so a device already holding numbers re-pulls.
 */
export type ReferencePriceLine = {
  id: string;
  priceListId: string;
  productId: string;
  amount: string;
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

/**
 * What one country charges one tax class, as the device holds it (`PRD-07`) — W11 slice 7b.
 *
 * **The last input the device was missing.** Prices, promotions and the assortment all arrived in
 * W8; rates did not, and `TaxRate` was not even sync-tracked — so `priceLine` was handed a null rate,
 * which it reads as *unknown* and charges nothing for. A rep saw a correct-looking net total that the
 * server's recomputation would exceed by exactly the tax, on every order.
 *
 * `percentage` is a decimal **string**, per the rule slice 7a established for this whole protocol.
 * The window travels as ISO dates and is half-open `[effectiveFrom, effectiveTo)`, because a device
 * pricing an order dated before a VAT change needs the rate that was in force then.
 */
export type ReferenceTaxRate = {
  id: string;
  taxClassId: string;
  /** ISO-3166-1 alpha-2, upper-cased — matched against the outlet's own country. */
  countryCode: string;
  percentage: string;
  effectiveFrom: string;
  effectiveTo: string | null;
  rowVersion: number;
};

/**
 * The smallest order one channel or one shop may place (`ORD-06`, `BR-ORD-5`) — W11 slice 8b-ii.
 *
 * **Exactly one of the two scope ids is set**, and that is how the device ranks them: an outlet's own
 * minimum beats its channel's, the same precedence a price list has. A row with both would be a rule
 * with two scopes; the server's check constraint refuses one.
 *
 * `amount` is a decimal **string**, per the rule slice 7a established for this whole protocol — and
 * more sharply here than anywhere else on it, because a hundredth decides whether a rep may send
 * their order at all rather than what it costs.
 *
 * **`currencyCode` travels with it**, which no other reference row needs. An order's currency comes
 * from the list that priced it (`BR-ORD-7`), and nothing makes that agree with what somebody typed
 * into a minimum. Comparing 50 EUR against 200 RON by their numbers alone would refuse orders
 * comfortably over the threshold, so the device has to be able to see that they disagree.
 */
export type ReferenceOrderMinimum = {
  id: string;
  channelId: string | null;
  outletId: string | null;
  amount: string;
  currencyCode: string;
  rowVersion: number;
};

/**
 * What a promotion applies to. Exactly one id is set.
 *
 * **An empty list reaches nothing**, and this comment used to say the opposite. The server is
 * unambiguous — `PromotionEndpoints` calls an empty target set "a real state, not a refusal: the
 * promotion then discounts nothing", and it is how a deal is withdrawn without editing its window or
 * deleting a record other things point at.
 *
 * Worth recording rather than quietly correcting, because of *when* it was caught: W11 slice 7d wrote
 * the first device code that reads this field, and the comment would have had it apply every
 * withdrawn promotion to every line. A confident wrong sentence with no code under it yet is exactly
 * the shape of the price feed's `amount arrives as a number because that is what JSON has`
 * (slice 7a) — right up to the moment somebody believed it.
 */
export type ReferencePromotionTarget = { productId: string | null; categoryId: string | null };

/**
 * One threshold of a volume promotion, ordered by `minQuantity`.
 *
 * The two decimals are **strings** for the reason `ReferencePriceLine.amount` is. `minQuantity`
 * stays a number because it genuinely is one — a tier reads "buy 6 or more", and whole units is what
 * the rule means.
 */
export type ReferencePromotionTier = {
  minQuantity: number;
  percentOff: string | null;
  amountOff: string | null;
  currency: string | null;
};

/**
 * One promotion as the device holds it (`PRD-05`, W8 slice 8f).
 *
 * Targets and tiers live *inside* it, and the reason is sharper than the workflow's: a device
 * holding four of five tiers does not fail, it computes a **different discount** — and neither the
 * rep nor the shop has any way to notice.
 *
 * Every decimal is a **string** (W11 slice 7a) — see `ReferencePriceLine.amount`. A discount is the
 * worst place to lose exactness: the error compounds with quantity, and the rep and the shopkeeper
 * have already shaken hands on the number. The quantities stay numbers because they are counts.
 */
export type ReferencePromotion = {
  id: string;
  name: string;
  type: string;
  percentOff: string | null;
  amountOff: string | null;
  currency: string | null;
  buyQuantity: number | null;
  getQuantity: number | null;
  getPercentOff: string | null;
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

/**
 * One line of an order as the device holds it (`ORD-01`, `ORD-05`) — W11 slice 6.
 *
 * <b>Every number here is a decimal *string*, and that is the whole point.</b> A quantity can be a
 * weight and a price is money; `0.1 + 0.2` is exactly the arithmetic `Money` and `decimal.js` exist
 * to keep out of this app (`BR-PRD-8`). The screen computes with `Money`, stores the result as a
 * string, and only the projection onto the wire turns it into a JSON number — see `captured` in
 * `local-order.ts` for why that last step is where the conversion belongs.
 */
export type LocalOrderLine = {
  productId: string;
  /** How many, in `unitOfMeasure`. A decimal string — a quantity can be a weight. */
  quantity: string;
  /** The unit as it was when the rep picked it, copied and never reached for later. */
  unitOfMeasure: string;
  /** Units per pack at capture; null when sold loose. */
  packSize: number | null;
  /** What the device charged per unit. The record, not a suggestion (`BR-ORD-6`). */
  unitPrice: string;
  /** What the device made of the line after any promotion it applied. */
  lineTotal: string;
  /**
   * The tax on top of {@link lineTotal} (`ORD-02`, `PRD-07`) — W11 slice 14.
   *
   * <b>The field the captured shape was missing.</b> The screen priced tax from slice 7 and had
   * nowhere to put it, so every order reached the back office net of VAT — and the server's
   * recomputation, which includes tax, had nothing like-for-like to be compared against.
   */
  taxAmount: string;
};

/**
 * What the device had pulled when it priced an order (`ORD-08`) — W11 slice 14.
 *
 * <b>The device's own cursors, because a disagreement should explain itself.</b> `BR-ORD-6` asks an
 * order to record the pricing snapshot it was captured against; these are the six watermarks that
 * actually decided the numbers, so a server that gets a different total can say *which* input was
 * stale rather than only that one was.
 */
export type LocalPricingSnapshot = {
  priceLists: number;
  priceLines: number;
  priceAssignments: number;
  promotions: number;
  promotionAssignments: number;
  taxRates: number;
};

/**
 * An order as the device holds it (`ORD-01`, `ORD-05`, `OFF-01b`) — W11 slice 6.
 *
 * <b>The second thing in this database the device authors</b>, after `LocalVisit`, and it is the one
 * `B4` puts a whole lifecycle state on: `Draft` exists **here and nowhere else**. The server's first
 * status is `Submitted`, so a draft that is lost is work that never existed anywhere — which is what
 * makes this store, rather than the outbox, the thing `ORD-05` is really about.
 *
 * <b>The shape is the server's `CapturedOrder`</b>, so sealing it is a projection rather than a
 * translation, and a field this type is missing is a field the push cannot send. What it adds is the
 * device's own bookkeeping: the status, the outlet (which the wire omits because the server takes it
 * from the visit), and when it was last touched.
 */
export type LocalOrder = {
  /**
   * Minted here, and it is the order's identity on both sides.
   *
   * A rejected order re-opens under the *same* id and is resubmitted under a new mutation id
   * (`BR-ORD-9`), which is why the identity has to be the device's and stable across attempts.
   */
  id: string;
  visitId: string;
  /**
   * The shop, for this device's own screens only.
   *
   * <b>Not sent.</b> `CapturedOrder` has no outlet: the server takes it from the visit, because a
   * device that could name one could name a different shop from the one the rep stood in.
   */
  outletId: string;
  status: LocalOrderStatus;
  /** ISO-4217, from the price list the device resolved. Every line is in it (`BR-ORD-7`). */
  currencyCode: string;
  /** The order's total as a decimal string — the sum of the rounded lines, never re-derived. */
  total: string;
  /** The tax total, beside {@link total}'s net — W11 slice 14. */
  taxTotal: string;
  /**
   * What the device had pulled when the rep sealed this (`ORD-08`), or null while it is a draft.
   *
   * Recorded at the seal rather than at the first line, because a rep can add a line, sync, and add
   * another — the numbers that reach the server are the ones from the moment they stopped editing.
   */
  capturedAgainst: LocalPricingSnapshot | null;
  lines: LocalOrderLine[];
  /**
   * When the rep sealed it. Null while it is a draft.
   *
   * The server stores this unmodified and it is *not* when the push arrived: an order taken in a
   * basement on Tuesday and pushed from a car park on Thursday happened on Tuesday.
   */
  capturedAtUtc: string | null;
  /** Device-only, so a list can show the most recently touched draft first. */
  updatedAtUtc: string;
};

/**
 * Where an order has got to on the device.
 *
 * <b>`draft` is `B4`'s `Draft`, and `submitted` covers everything after.</b> There is deliberately no
 * `accepted` or `rejected` here yet: what the back office made of an order arrives on the *pull*
 * feed, which does not carry orders until the Order module opts back into sync tracking. A status
 * this store could not keep true would be worse than one it does not have.
 */
export type LocalOrderStatus = "draft" | "submitted";

/**
 * One MSL product as the rep found it (`AUD-01`, `BR-AUD-1`) — W11 slice 9a.
 *
 * The names are the server's `AvailabilityStatus`, not numbers: an ordinal inserted into the middle
 * of that enum would silently reinterpret every stored line, and this store outlives the app version
 * that wrote it (`OFF-13`).
 *
 * <b>Three states, not two, and the third is why.</b> *Absent* is a listing the shop never took;
 * *out of stock* is one it took and cannot keep filled. They look identical from the aisle and mean
 * opposite things to the business — collapsing them makes the availability pillar unable to tell a
 * distribution problem from a replenishment one, which is most of what the pillar is for.
 */
export type LocalAvailabilityStatus = "Present" | "Absent" | "OutOfStock";

export type LocalAvailabilityLine = { productId: string; status: LocalAvailabilityStatus };

/**
 * An audit as the device holds it (`AUD-01`, `OFF-01b`) — W11 slice 9a.
 *
 * <b>The third thing in this database the device authors</b>, after `LocalVisit` and `LocalOrder`,
 * and it takes the draft-then-sealed shape from the second: an audit is worked at a shelf with no
 * signal and only exists here until the rep seals it. `BR-AUD-6` seals it for good.
 *
 * <b>The shape is the server's `CapturedAudit`</b>, so sealing is a projection rather than a
 * translation — a field this type lacks is a field the push cannot send. What it adds is the
 * device's own bookkeeping: the status, the outlet (the wire omits it, because the server takes it
 * from the visit), and when it was last touched.
 *
 * <b>`weightSetVersion` is recorded at capture and never re-read.</b> `BR-AUD-8` has the server
 * recompute the score against *these* weights, so a re-weighting between the shelf and the push must
 * not move the answer. It is the one fact that cannot be recovered later, which is why it is stored
 * with the audit rather than looked up when the audit is sealed.
 *
 * <b>Facings, prices and answers are not here yet</b> — slices 9b and 9c. Named rather than left to
 * be discovered: `CapturedAudit` carries them, so an audit sealed today sends empty lists for all
 * three, and the server's `Empty` refusal is what stops one with nothing in it at all.
 */
export type LocalAudit = {
  /** Minted here, and it is the audit's identity on both sides — a replay maps to the same row. */
  id: string;
  visitId: string;
  /** The shop, for this device's own screens. **Not sent** — the server takes it from the visit. */
  outletId: string;
  status: LocalAuditStatus;
  /** The weighting this audit is scored against (`BR-AUD-8`), fixed when the draft is started. */
  weightSetVersion: number;
  availability: LocalAvailabilityLine[];
  facings: LocalFacingsLine[];
  /**
   * The **total** facings in the category, own SKUs and competitors' alike (`BR-AUD-2`) — W11 9b.
   *
   * The denominator, and it is captured rather than derived: summing the facings above would always
   * produce ~100% share-of-shelf, which is the arithmetic `BR-AUD-2` exists to forbid.
   *
   * **Null is a real answer and the default.** Without a total the share-of-shelf pillar is
   * *skipped*, not faked — the score renormalises over the pillars that were measured (W10 slice 0).
   * A rep who could not count the shelf has said something true by leaving it empty.
   */
  categoryFacings: number | null;
  prices: LocalPriceCheck[];
  /**
   * Which questionnaire the rep worked, or null when this audit has no survey (`AUD-04`) — W11 9c.
   *
   * <b>Null is the ordinary case</b>, not a gap: most audits are a shelf and no form, and the server
   * takes a null form with no answers as an audit that simply had no survey step.
   *
   * <b>Nothing in the model says which form applies here</b>, which is why the rep chooses. A visit
   * workflow's step carries a type and a label and no form id, and `ISurveyForms` is tenant-wide —
   * so with one form the screen uses it and with several it asks. A form-per-channel would be
   * Configuration's to own, and inventing it on the device would be a rule no administrator could
   * see.
   */
  surveyFormId: string | null;
  answers: LocalAnswer[];
  /**
   * What the rep photographed, as **references** (`AUD-05`, `B5`) — W11 slice 11.
   *
   * <b>The images are not here.</b> They live in the `blobs` table under these same keys, and travel
   * to object storage on their own schedule (`OFF-08`) — never through the JSON push, which regularly
   * wins the race and lands an audit whose pictures arrive minutes later.
   */
  photos: LocalPhoto[];
  /** When the rep sealed it. Null while it is a draft, and never when the push arrived. */
  capturedAtUtc: string | null;
  updatedAtUtc: string;
};

/**
 * Which part of the audit a photo is evidence for (`AUD-05`).
 *
 * The names `AuditSection` carries, because the enum is serialised by name — an ordinal here would be
 * a second vocabulary to keep in step, and a device holding a photo taken before a member was
 * inserted would file it under the wrong section.
 */
export type LocalAuditSection =
  | "Availability"
  | "ShareOfShelf"
  | "PriceCompliance"
  | "Survey"
  | "General";

/**
 * One photo's reference (`AUD-05`, `B5`) — W11 slice 11.
 *
 * <b>The key is minted on the device</b>, like the audit's own id: the reference and the upload have
 * to agree without a round trip, and the rep is usually offline when the picture is taken.
 */
export type LocalPhoto = { section: LocalAuditSection; objectKey: string };

/**
 * What {@link LocalPhotoBlob.uploadedAtUtc} holds while the image is still on the device.
 *
 * Empty rather than null so IndexedDB can index it — see the field's own note.
 */
export const WAITING = "";

/**
 * An image waiting to be uploaded (`OFF-08`, `B5`) — W11 slice 11.
 *
 * <b>The one table on this device holding something the server cannot re-send.</b> Every `ref_*`
 * store is a copy; the outbox and this are the originals. A blob dropped before its upload is a
 * photograph that existed nowhere else, which is why `objectKey` is the primary key rather than an
 * auto-increment: the audit already references it, and a re-keyed row would strand that reference.
 *
 * <b>`auditId` is indexed</b> so a sealed audit's images can be found without scanning — the upload
 * path (W11 slice 12) walks them, and clearing up after a rejected audit needs the same question.
 */
export type LocalPhotoBlob = {
  objectKey: string;
  auditId: string;
  section: LocalAuditSection;
  /** The downscaled JPEG itself (`B5`: ~1600px, quality ~0.7). */
  image: Blob;
  bytes: number;
  capturedAtUtc: string;
  /**
   * When this image reached object storage, or {@link WAITING} while it has not (`OFF-08`) — W11 12b.
   *
   * <b>An empty string rather than null, and the type says so.</b> IndexedDB will not index `null`,
   * and this field exists to be *queried* — the uploader asks "what is still waiting" on every sync
   * run, and without an index that is a scan of every image a rep has taken this week, because Dexie
   * hands back whole records to answer a question about one field. Typing it `string | null` while
   * storing `""` would be a lie in the one place a reader checks.
   *
   * <b>The bytes are kept after upload, deliberately.</b> A rep looking at a sealed audit should
   * still see what they photographed, and the device is the only copy they can reach — the upload
   * path is write-only by design. Pruning is `OFF-11`'s question (storage pressure), not this
   * slice's, and deleting on success would answer it by losing the picture.
   */
  uploadedAtUtc: string;
  /**
   * How many times an upload has been tried and failed.
   *
   * Kept per photograph rather than per run, because the retry schedule is this transport's own
   * (`B5`): a picture that has failed nine times must not keep a rep's whole round waiting on it,
   * and the count is what lets a later run skip it and still show a rep it is stuck.
   */
  attempts: number;
  /**
   * Why the last upload failed, or empty when none has (`OFF-08`, `OFF-09`) — W11 slice 12c.
   *
   * <b>Kept because swallowing it hid a bug for a whole slice.</b> The uploader caught every failure
   * and recorded only that there had been one, so a Content Security Policy refusing every `PUT`
   * looked exactly like a bad connection — and the retry made it look like a bad connection
   * *forever*. It took a browser console to find, and nothing on the device could have said.
   *
   * A short message, not a stack: it is for a rep's "why is this stuck" (slice 13) and for whoever
   * reads the store when a photograph will not go, and both want a sentence.
   */
  lastFailure: string;
  /**
   * The full key the server minted, tenant prefix included — empty until the upload succeeds
   * (`OFF-08`) — W11 slice 13b.
   *
   * <b>Kept because confirming needs it and the device cannot rebuild it.</b> {@link objectKey} is
   * the device's own, `audits/{auditId}/{photoId}.jpg`; the server prefixes the tenant, which this
   * device does not know and must not be told. Holding the answer to a presign is the only way to
   * name the same object twice.
   */
  storedKey: string;
  /**
   * When the server acknowledged the upload, or {@link WAITING} while it has not (`OFF-08`) —
   * W11 slice 13b.
   *
   * <b>A second state, because uploading and being *known* to have uploaded are different facts.</b>
   * The bytes reach storage on a presigned URL the server never sees used, so an upload that
   * succeeded and a confirmation that never got through look identical from the back office. This is
   * what the device retries on, and it is what stops a visit reading as finished while the server
   * still believes a photograph is on its way.
   *
   * Empty rather than null for the same reason {@link uploadedAtUtc} is: it is indexed, and the
   * confirm pass asks "what is still unacknowledged" on every run.
   */
  confirmedAtUtc: string;
};

/**
 * One survey answer, as the rep gave it (`AUD-04`) — W11 slice 9c.
 *
 * <b>It carries the question's text, not just its key</b>, which is the server's shape and its
 * argument: a key alone needs the form re-read to mean anything, and the form may have been reworded
 * — or the question dropped — between the rep answering and the push arriving.
 *
 * <b>`value` is a string whatever the question's type was.</b> A number question's answer is `"12"`,
 * a boolean's is `"true"`, a multi-choice's is its chosen options joined. That is a real loss of
 * typing and it is the server's: the alternative is five nullable columns of which four are always
 * null, and a sixth the day a type is added. The type lives on the question, where a reader can find
 * it.
 */
export type LocalAnswer = { questionKey: string; questionText: string; value: string };

/**
 * Facings counted for one product (`AUD-02`) — the numerator of share-of-shelf.
 *
 * A whole number, because a facing is one product's front on a shelf and there is no half of one.
 * Stored as a `number` rather than a decimal string for that reason — the rule `BR-PRD-8` protects
 * is about *money*, and a count that cannot be fractional cannot be wrong by a hundredth.
 */
export type LocalFacingsLine = { productId: string; facings: number };

/**
 * A shelf price the rep read, and the one the device says to expect (`AUD-03`, `BR-AUD-3`) — W11 9b.
 *
 * <b>Both amounts are decimal strings here and integer minor units on the wire.</b> That is the
 * server's shape (`CapturedPrice.ObservedMinorUnits`), and the conversion happens once, at the seal,
 * for the reason `local-order.ts` gives: the rep types a decimal, arithmetic stays in `decimal.js`,
 * and the value crosses `Number` exactly once at a magnitude where it is exact.
 *
 * <b>`expected` is what the device resolved, and null when it could resolve none.</b> An unpriced
 * product is not a compliance failure — scoring it as one would punish a rep for a gap in the price
 * list. It is stored rather than re-resolved on arrival, because the server asking Pricing what the
 * price is *today* would judge a completed audit against a list republished since.
 */
export type LocalPriceCheck = {
  productId: string;
  observed: string;
  expected: string | null;
  /** ISO-4217, from the list that priced it. One currency across the audit (`CurrencyMismatch`). */
  currencyCode: string;
};

/** Where an audit has got to on the device. `BR-AUD-6` makes `sealed` final. */
export type LocalAuditStatus = "draft" | "sealed";

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
  surveys!: EntityTable<ReferenceSurveyForm, "id">;
  scoreWeights!: EntityTable<ReferenceScoreWeightSet, "id">;
  taxRates!: EntityTable<ReferenceTaxRate, "id">;
  orderMinimums!: EntityTable<ReferenceOrderMinimum, "id">;
  visits!: EntityTable<LocalVisit, "id">;
  orders!: EntityTable<LocalOrder, "id">;
  audits!: EntityTable<LocalAudit, "id">;
  blobs!: EntityTable<LocalPhotoBlob, "objectKey">;
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

    /*
     * Version 6 — survey forms and the perfect-store weightings (`OFF-03`, W10 slice 7).
     *
     * Two reference stores, no `upgrade()`: nothing existed to transform, and every device carries
     * on with its outbox and its other reference data untouched. A device that opens the app after
     * this ships gets both tables empty and both watermarks at zero, which is exactly the state a
     * fresh install is in — so the next pull fills them by the ordinary path rather than a special
     * one.
     *
     * The indexes are the questions actually asked:
     *
     * - `name` on a form, because that is what an administrator picks one by and what a rep sees.
     * - `&version` on a weighting, unique, because an audit names a *version* and that is the only
     *   way anything will look one up. Declared unique so a bug that stored two for one version
     *   fails loudly rather than picking one arbitrarily — the same call `ref_visit_workflows`
     *   makes about `channelId`.
     *
     * A form's `questions` and a set's `weights` are not indexed and never will be: they are read
     * with the row that carries them, which is the whole reason they travel inside it.
     */
    this.version(6).stores({
      ref_surveys: "id, name",
      ref_score_weights: "id, &version",
    });

    /*
     * Version 7 — the device's own orders (`ORD-05`, `OFF-01b`, W11 slice 6).
     *
     * The second store this device *authors*, after `visits`, and the first one holding a state the
     * server never sees: `B4` puts `Draft` on the device, so an order lost before submit is work that
     * existed nowhere else. That is what makes this a store rather than an outbox payload — the
     * outbox holds things already sealed, and a draft is precisely the thing that is not.
     *
     * The indexes are the two questions a screen asks:
     *
     * - `visitId`, because a rep at a counter wants *this* visit's order, and B4 allows at most one.
     * - `status`, because "have I got an unsent draft anywhere" is what the shell asks on launch.
     *
     * `lines` is not indexed and never will be: it is read with the row that carries it, the same
     * call `visits.steps` and a survey's `questions` already make.
     *
     * <b>No `upgrade()`.</b> Nothing existed to transform — a device that opens the app after this
     * ships finds the table empty, which is the state a fresh install is in. What `OFF-13` needs from
     * this version is that a **pending outbox survives it**, and that is `migration.test.ts`'s to
     * assert rather than this block's to claim.
     */
    this.version(7).stores({
      orders: "id, visitId, status",
    });

    /*
     * Version 8 — money on the pull feeds stops being a float (`BR-PRD-8`, W11 slice 7a).
     *
     * `PriceLineSnapshot.Amount` and every decimal on a promotion now cross the wire as strings, so
     * `ReferencePriceLine.amount` and the promotion's four decimals are `string` rather than
     * `number`. A device that synced before this holds the old shape: numbers that have already been
     * through `JSON.parse`, which is exactly the IEEE-754 the pricing engine exists to avoid.
     *
     * <b>So the two watermarks are dropped and the rows re-pulled.</b> Unlike versions 3 and 4 —
     * which re-baselined because a *field was added* — this one re-baselines because the rows on the
     * device are the wrong **type**, and a delta pull would only correct the ones that happened to
     * change afterwards. A price nobody edits would keep its float forever.
     *
     * The rows are left in place rather than cleared: a device that goes offline between the upgrade
     * and the next sync can still show a rep the catalogue and its prices, which is a better failure
     * than an empty shop — and the value is wrong only in the last decimal place it was ever wrong in.
     */
    this.version(8)
      .stores({})
      .upgrade(async (tx) => {
        await tx.table("watermarks").delete("priceLines");
        await tx.table("watermarks").delete("promotions");
      });

    /*
     * Version 9 — tax rates reach the device (`PRD-07`, `OFF-03`, W11 slice 7b).
     *
     * The last pricing input that never travelled. `TaxRate` was not sync-tracked at all, so there
     * was no delta to send and no store to hold it — and `priceLine` reads a missing rate as
     * *unknown* rather than zero, which is honest and still leaves the rep's total short of the
     * server's by exactly the tax on every order.
     *
     * The index is `[countryCode+taxClassId]`, compound, because that is the whole question: a rate
     * is looked up by the shop's country and the product's class, never by one alone. Every rate for
     * that pair is then read and `resolveTaxRate` picks by date — the window cannot be an index,
     * since `BR-PRD-6` resolves against the *order's* day rather than today's.
     *
     * No `upgrade()`: nothing existed to transform. A device that opens the app after this finds the
     * table empty and its watermark at zero, which is what a fresh install is — so the next pull
     * fills it by the ordinary path. The server's migration backfills every existing rate's row
     * version so that first pull is not empty.
     */
    this.version(9).stores({
      ref_tax_rates: "id, [countryCode+taxClassId]",
    });

    /*
     * Version 10 — the outlet says which country taxes it (`PRD-07`, W11 slice 7c).
     *
     * `OutletSnapshot` gained `countryCode`, which is the half version 9 was missing: the rates were
     * on the device and could not be matched to the shop the rep was standing in.
     *
     * <b>The outlets watermark is dropped so every row is re-pulled</b>, for the reason versions 3
     * and 4 did it: a *field was added*, so the rows already here are the right type and the wrong
     * shape — they simply have no `countryCode` — and a delta pull would only fill it in for the
     * shops somebody happened to edit afterwards. A shop nobody touches again would price untaxed
     * for the life of the install.
     *
     * No store declaration: `countryCode` is not indexed. The lookup goes outlet → country → the
     * rates index, so the country is read off a row already in hand rather than searched for.
     *
     * Rows are left in place, as version 8 left the prices: a device that goes offline between the
     * upgrade and its next sync still shows the rep their round, and the field that is missing is
     * one nothing could use an hour ago either.
     */
    this.version(10)
      .stores({})
      .upgrade(async (tx) => {
        await tx.table("watermarks").delete("outlets");
      });

    /*
     * Version 11 — order minimums reach the device (`ORD-06`, `OFF-03`, W11 slice 8b-ii).
     *
     * `BR-ORD-5` is enforced here rather than on the server, because "must be met to submit" is a
     * question asked at a counter with no signal. Slice 8b-i gave the server a minimum to read;
     * without this table there was nothing on the device to read it *with*.
     *
     * Two indexes rather than one compound, because the two scopes are asked about separately: the
     * device looks up the outlet's own minimum, then its channel's, and takes the first. A compound
     * `[channelId+outletId]` would index a pair that is never whole — exactly one of them is set on
     * any row.
     *
     * No `upgrade()`: nothing existed to transform, as with version 9. The table opens empty with its
     * watermark at zero and the next pull fills it by the ordinary path — and no server backfill is
     * needed either, because `OrderMinimum` was born sync-tracked one slice ago and the table is new.
     *
     * <b>An empty table means every order passes</b>, which is the right reading of "if configured"
     * and the only safe one while the first pull is in flight: a device that refused everything until
     * it had synced would block a rep whose tenant has never set a minimum at all.
     */
    this.version(11).stores({
      ref_order_minimums: "id, channelId, outletId",
    });

    /*
     * Version 12 — the device authors an audit (`AUD-01`, `OFF-01b`, W11 slice 9a).
     *
     * The third store this device *writes* rather than receives, after `visits` and `orders`, and it
     * takes the second one's argument whole: an audit is worked at a shelf with no signal, so a
     * draft lost before it is sealed is work that existed nowhere else. The outbox holds things
     * already sealed; a draft is precisely the thing that is not.
     *
     * The indexes are the two questions a screen asks — `visitId`, because `BR-AUD-6` ties an audit
     * to one visit and the server allows one per visit, and `status`, because "is there an unsealed
     * audit anywhere" is what a shell asks on launch. `availability` is not indexed and will not be:
     * it is read with the row that carries it, as `orders.lines` and `visits.steps` already are.
     *
     * <b>No `upgrade()`</b>, for the reason version 7 gives: nothing existed to transform, and a
     * device that opens the app after this finds the table empty — which is what a fresh install is.
     * What `OFF-13` needs is that a **pending outbox survives it**, and that is `migration.test.ts`'s
     * to assert rather than this block's to claim.
     */
    this.version(12).stores({
      audits: "id, visitId, status",
    });

    /*
     * Version 13 — the numbers on the shelf (`AUD-02`, `AUD-03`, W11 slice 9b).
     *
     * <b>The first `upgrade()` on a store this device *authors*</b>, and that is why it exists at all
     * when versions 5, 7, 9, 11 and 12 needed none. Those added tables; this adds three fields to
     * rows that are already there — and unlike every `ref_*` table, an audit draft cannot be
     * re-fetched. A rep halfway down an aisle when the app updates has the only copy.
     *
     * Adding a field normally needs no version: Dexie stores the object whole, and a reader can
     * default what is missing. What makes it worth one here is `captured()` — `CapturedAudit` takes
     * `facings` and `prices` as *required* lists, so a draft sealed with them `undefined` would send
     * JSON missing two properties and be refused as a 400 that retries forever. Normalising once, on
     * open, means every later read can trust the shape rather than each remembering to.
     *
     * `categoryFacings` becomes null rather than 0, which is `BR-AUD-2`'s distinction: null skips the
     * share-of-shelf pillar, and a zero would score the shop as having none of the category.
     *
     * No `stores()` change — none of the three is indexed, and none will be. They are read with the
     * row that carries them.
     */
    this.version(13)
      .stores({})
      .upgrade(async (tx) => {
        await tx
          .table("audits")
          .toCollection()
          .modify((audit: Partial<LocalAudit>) => {
            audit.facings ??= [];
            audit.prices ??= [];
            audit.categoryFacings ??= null;
          });
      });

    /*
     * Version 14 — the questionnaire at the shelf (`AUD-04`, W11 slice 9c).
     *
     * The same shape as version 13 and for the same reason: two fields added to rows a rep may be
     * halfway through, and `captured()` cannot send `undefined` where the wire expects a value.
     * `answers` is normalised to `[]` because `CapturedAudit` reads a null answer list as *no
     * survey* — which is true here, and would stop being true the moment a reader defaulted it
     * somewhere else instead.
     *
     * `surveyFormId` becomes null, which is the ordinary state rather than a missing one: most
     * audits are a shelf and no form.
     */
    this.version(14)
      .stores({})
      .upgrade(async (tx) => {
        await tx
          .table("audits")
          .toCollection()
          .modify((audit: Partial<LocalAudit>) => {
            audit.answers ??= [];
            audit.surveyFormId ??= null;
          });
      });

    /*
     * Version 15 — photographs, and the `blobs` store W8 left uncreated (`OFF-08`, `B5`, W11 11).
     *
     * <b>W8 deliberately did not add this table</b>, because nothing wrote to it: a store with no
     * writer is a schema claim nobody can check, and its shape would have been guessed a phase early.
     * It arrives now with the code that fills it.
     *
     * `photos` is back-filled for the same reason 13 and 14 back-filled theirs: `captured()` sends it
     * as a list, and a draft sealed with it `undefined` would push JSON missing a property.
     *
     * <b>The blobs live in their own store, not on the audit row.</b> An audit is read on every
     * render of the shelf screen; the images are read once, by the uploader. Dexie hands back whole
     * records, so keeping a megabyte of JPEG on the row would make every live query carry it.
     */
    this.version(15)
      .stores({ blobs: "objectKey, auditId" })
      .upgrade(async (tx) => {
        await tx
          .table("audits")
          .toCollection()
          .modify((audit: Partial<LocalAudit>) => {
            audit.photos ??= [];
          });
      });

    /*
     * Version 16 — a photograph knows whether it has been uploaded (`OFF-08`, W11 slice 12b).
     *
     * <b>`uploadedAtUtc` is indexed, and that is the version's whole point.</b> The uploader asks
     * "what is still waiting" on every sync run, and a scan of every blob a rep has taken this week
     * is a scan of megabytes — Dexie hands back whole records, images included, to answer a question
     * about one field.
     *
     * IndexedDB will not index `null`, so *waiting* is stored as the empty string rather than null:
     * `where("uploadedAtUtc").equals("")` is an index seek, and a filter over everything is not. The
     * type says `string | null` because null is what a reader means; the store's own predicate is
     * the only place that distinction is spelled.
     */
    this.version(16)
      .stores({ blobs: "objectKey, auditId, uploadedAtUtc" })
      .upgrade(async (tx) => {
        await tx
          .table("blobs")
          .toCollection()
          .modify((blob: Partial<LocalPhotoBlob>) => {
            blob.uploadedAtUtc ??= WAITING;
            blob.attempts ??= 0;
          });
      });

    /*
     * Version 17 — a photograph remembers why it would not go (`OFF-08`, `OFF-09`, W11 slice 12c).
     *
     * No new index: nothing queries by failure, and this is read one row at a time by whoever is
     * looking at a stuck picture. A field, back-filled empty, because `lastFailure` is a `string` for
     * the same reason `uploadedAtUtc` is — a reader should not have to hold two shapes in mind for
     * "nothing here yet".
     */
    this.version(17)
      .stores({})
      .upgrade(async (tx) => {
        await tx
          .table("blobs")
          .toCollection()
          .modify((blob: Partial<LocalPhotoBlob>) => {
            blob.lastFailure ??= "";
          });
      });

    /*
     * 18: the device tracks whether the *server* knows a photograph arrived (`OFF-08`) — W11 13b.
     *
     * `confirmedAtUtc` is indexed because the confirm pass asks for it on every sync run, and
     * `storedKey` is not: it is read from a row already in hand, never searched for.
     *
     * <b>Photographs uploaded before this version are marked confirmed, and that is a small lie
     * told deliberately.</b> They have no `storedKey` — the tenant-prefixed key came back from a
     * presign nobody kept — and the device cannot rebuild one, so they can never be confirmed. The
     * alternative is a row that retries a call it has no arguments for on every sync, forever. What
     * it costs is stated: the server will read those references as missing once they are a week old,
     * which is the honest outcome for a photograph it was never told about.
     */
    this.version(18)
      .stores({ blobs: "objectKey, auditId, uploadedAtUtc, confirmedAtUtc" })
      .upgrade(async (tx) => {
        await tx
          .table("blobs")
          .toCollection()
          .modify((blob: Partial<LocalPhotoBlob>) => {
            blob.storedKey ??= "";
            blob.confirmedAtUtc ??= blob.uploadedAtUtc ?? WAITING;
          });
      });

    /*
     * 19: an order carries its tax, and what it was priced against (`ORD-08`) — W11 slice 14.
     *
     * No new index — nothing looks orders up by either — so this is a back-fill only.
     *
     * <b>Existing orders get zero tax and a null snapshot, and those are two different admissions.</b>
     * Zero is wrong in the sense that the tax existed; it is right in the sense that this device never
     * recorded it and cannot reconstruct it — the rate came from reference data that has since moved.
     * Null for the snapshot says the same thing without pretending: the device did not note what it
     * priced against, which is not the same as having priced against nothing.
     */
    this.version(19)
      .stores({})
      .upgrade(async (tx) => {
        await tx
          .table("orders")
          .toCollection()
          .modify((order: Partial<LocalOrder>) => {
            order.taxTotal ??= "0";
            order.capturedAgainst ??= null;

            for (const line of order.lines ?? []) {
              line.taxAmount ??= "0";
            }
          });
      });

    /*
     * Version 20 — the outlet says which day it is (`BR-PRD-6`, regression F6, W11½ R6).
     *
     * `OutletSnapshot` gained `timeZoneId`. Until now the device dated its pricing by the *rep's
     * phone* and the server re-priced by the *UTC* day, which are two different rules rather than
     * one rule rounded twice — so an order taken in Bucharest before 03:00 was flagged as
     * disagreeing with a server that had asked a different question.
     *
     * <b>The outlets watermark is dropped so every row is re-pulled</b>, exactly as version 10 did
     * when `countryCode` arrived, and for the same reason: a *field was added*, so the rows already
     * here are the right type and the wrong shape, and a delta pull would fill it in only for the
     * shops somebody happened to edit afterwards. A shop nobody touches again would price against
     * the wrong day for the life of the install.
     *
     * <b>And unlike version 10, the held rows are backfilled</b> — with `""`, which the server can
     * never send, because `Outlet.TimeZoneId` is required and IANA-validated. `countryCode` needed
     * no backfill because *null* was already a meaningful answer there; here there is no meaningful
     * empty, so writing one keeps the declared type honest for the window between this upgrade and
     * the next successful pull. A caller reading `""` declines to answer rather than guessing UTC.
     *
     * Rows are left in place rather than cleared, as versions 8 and 10 left them: a device that goes
     * offline between the upgrade and its next sync still shows the rep their round, and the field
     * that is missing is one nothing could have used an hour ago either.
     */
    this.version(20)
      .stores({})
      .upgrade(async (tx) => {
        await tx.table("watermarks").delete("outlets");

        await tx
          .table("ref_outlets")
          .toCollection()
          .modify((outlet: Partial<ReferenceOutlet>) => {
            outlet.timeZoneId ??= "";
          });
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
    this.surveys = this.table("ref_surveys");
    this.scoreWeights = this.table("ref_score_weights");
    this.taxRates = this.table("ref_tax_rates");
    this.orderMinimums = this.table("ref_order_minimums");
    this.visits = this.table("visits");
    this.orders = this.table("orders");
    this.audits = this.table("audits");
    this.blobs = this.table("blobs");
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


