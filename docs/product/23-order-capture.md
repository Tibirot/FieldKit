# Functional Spec — Order Capture

> **Module:** Order · **Group:** Field · **Phase:** 3 · **Status:** ✅ Baseline
> **Depends on:** Visit, Products & Pricing · **Consumed by:** reporting; ends at "submitted"

## 1. Purpose

Order Capture is where the visit turns into **revenue intent**. The rep builds an order against
the outlet's **assortment**, the platform prices it and applies **promotions** deterministically
**on-device**, and the order is submitted for downstream fulfillment (which is
[out of scope](00-product-overview.md#6-scope--non-goals) — FieldKit ends at *submitted*).
Lifecycle and mechanics per [B4](decisions-and-assumptions.md#b4--order-lifecycle) and
[B1](decisions-and-assumptions.md#b1--pricing--promotions).

## 2. Actors

| Actor | Interest |
|---|---|
| Field Rep | Captures the order in-store, offline, priced correctly |
| Supervisor | Reviews order value, lines, promo usage |
| Sales Ops / Admin | Configures order rules (minimums, off-assortment policy) |

## 3. Core concepts

- **Order** — a document tied to a visit + outlet: header (currency, totals, status) and
  **order lines**.
- **Order line** — product, quantity (in UoM/pack), resolved unit price, applied promotion,
  line total, tax.
- **Suggested list** — the outlet's assortment / MSL / last-order used to speed capture
  ([B2](decisions-and-assumptions.md#b2--assortment--must-stock-list-msl)).
- **Applied promotion** — the single line-level promo selected by priority, plus any order-
  level promo ([B1](decisions-and-assumptions.md#b1--pricing--promotions)).
- **Order status** — `Draft → Submitted → Accepted | Rejected → Cancelled` ([B4](decisions-and-assumptions.md#b4--order-lifecycle)).
  A **Rejected** order re-opens into an editable state on the device (see BR-ORD-9).

## 4. Capabilities & flows

### F1 · Capture an order (in-store, offline)
1. From a Visit order-step, the rep starts an order (currency from the outlet's price list).
2. Adds lines from the **suggested list** or by search; sets quantities.
3. Each line is **priced on-device** via the shared engine: unit price (specificity rules) +
   best promotion + tax ([Pricing F5](13-products-and-pricing.md#4-capabilities--flows)).
4. Header totals update live; **order minimum** is validated.
5. Rep reviews and **submits** → order is sealed locally and queued.

### F2 · Draft & resume
- An in-progress order stays **Draft** on device and can be edited until submitted.

### F3 · Submit & downstream
- On sync, the submitted order is pushed idempotently. Back office may **Accept/Reject** (optional
  step); FieldKit does not fulfill/invoice/deliver.

### F4 · Rejected-order remediation
- If the server **rejects** the order on push (e.g. a line's SKU went off-assortment or the outlet
  closed during the offline window), the rejection is **whole-order**, carries a **reason + the
  offending line**, and surfaces as a *needs-attention* item.
- The rejected order **re-opens into an editable state on the device** (a controlled exception to
  the post-submit lock). The rep fixes the flagged line(s) and **resubmits under a new mutation
  id** — the original submission's id is terminal, so the push stays idempotent and no work is
  lost. Resolves finding **S1**.
- **The rejected order is retained server-side in `Rejected` state and pulls back to the rep's
  active device**, so remediation survives even a **device swap** (it's the rep's own record) — the
  one transactional record that flows *down* by design ([sync engine §7](../architecture/12-offline-sync-engine.md#7-device-lifecycle)).
- **When there is nothing to fix** (e.g. `OUTLET_CLOSED`, `OUTLET_ON_HOLD` — no line the rep can
  edit to make it valid), the rep **Cancels** the rejected order (`Rejected → Cancelled`); the
  cancellation syncs like any device-owned mutation. A rejected order left unactioned past a
  configurable window is escalated to the supervisor (not silently dropped).

## 5. Business rules

- **BR-ORD-1** Only products in the outlet's **assortment** can be ordered (unless "off-
  assortment with reason" is enabled — [PRD-10](13-products-and-pricing.md), *Could*).
- **BR-ORD-2** Lines are priced by the **deterministic shared engine**; the device and server
  produce identical results ([BR-PRD-7](13-products-and-pricing.md#5-business-rules)).
- **BR-ORD-3** At most **one line-level promotion per line** (priority) + optional order-level
  promo ([B1](decisions-and-assumptions.md#b1--pricing--promotions)).
- **BR-ORD-4** An order is editable **only while Draft**; **locked after submit** — this is the
  rule that keeps orders conflict-free on sync ([B7](decisions-and-assumptions.md#b7--conflict-resolution-matrix)).
  The **one exception** is a server-rejected order (BR-ORD-9).
- **BR-ORD-5** Order minimum (**value**), if configured, must be met to submit. A minimum expressed
  in **quantity** is out of scope until units of measure become a vocabulary with conversions — see
  below.
  > **The rule used to say "value/qty", and that was settled to value in W11 slice 8b-i.** Not as a
  > scope cut: a quantity minimum is not a well-defined thing in this model yet.
  >
  > [B1](decisions-and-assumptions.md#b1--pricing--promotions) — a reviewed assumption — says
  > *"optional minimum order value per channel/outlet"*, value alone. The rule was the only place
  > "qty" appeared, and `ORD-06` is a `Should`.
  >
  > **What makes quantity undefined rather than merely unbuilt.** `Product.UnitOfMeasure` is a
  > deliberately inert label — its own comment says *"nothing branches on it… it labels a quantity
  > rather than deciding anything"* — and there is no unit conversion anywhere in the system. So
  > "minimum 20" has no answer for an order of 6 cases and 5 bottles: summing to 11 is meaningless.
  > `PackSize` gets part of the way (6 cases × 12 = 72 units) and is null for anything sold loose or
  > by weight, so a quantity rule would silently miscount exactly the products it cannot describe.
  >
  > **The prerequisite, named so it is not rediscovered mid-slice.** A quantity minimum needs units
  > of measure promoted to a vocabulary with a base unit and conversion factors — the "additive
  > migration plus a backfill" `Product.UnitOfMeasure` already predicts. The alternatives are worse:
  > a minimum that names one UoM has to refuse or ignore lines in others, and "total units, ignore
  > UoM" is the version that looks like it works and quietly does not.
  >
  > **Worth revisiting if a customer asks.** Minimum quantity is genuinely common in FMCG as a pallet
  > or truck-efficiency rule rather than an invoice-size one. The response then is to raise the UoM
  > vocabulary, not to squeeze a quantity check on top of a label nothing branches on.
  >
  > The minimum is authored in **Products**, beside price-list assignment, and carries its own
  > currency — an order's comes from the list that priced it (`BR-ORD-7`), so a mismatch is reported
  > rather than compared. Enforcement is on the device (slice 8b-ii): "must be met to submit" is a
  > question answered at a counter with no signal.
- **BR-ORD-6** The order records the **snapshot version** of pricing it was captured against; if
  the server re-prices and differs, it is **flagged, not silently changed**.
- **BR-ORD-7** Currency comes from the resolved price list; **no cross-currency lines** in one
  order ([A3](decisions-and-assumptions.md#a3--internationalization-full-multi-currency--multi-language-ui)).
- **BR-ORD-8** Returns are **out of scope** for v1 ([B4](decisions-and-assumptions.md#b4--order-lifecycle)).
- **BR-ORD-9** A **server-rejected** order re-opens editable on the device and is resubmitted under
  a **new mutation id**; the original submission id is terminal (idempotent). Rejection is
  **whole-order**, with a reason code + offending line. This is the sole documented exception to
  BR-ORD-4, and it guarantees rejected work is never stranded ([B4](decisions-and-assumptions.md#b4--order-lifecycle),
  [sync engine §4](../architecture/12-offline-sync-engine.md#4-push-protocol-device-owned-mutations)).

> **The captured order has nowhere to put tax** (found building the capture screen, W11 slice 7).
> `CapturedOrderLine` carries `unitPrice` and `lineTotal` and nothing else; `OrderLine.LineTotal` is
> *"what the device made of the line after any promotion it applied"* — the **net** — and the order's
> `Total` is a sum of those. So a device that prices tax under `ORD-02` shows the rep a gross the wire
> cannot carry, and the back office receives an order net of VAT.
>
> Storing the gross in `lineTotal` instead is worse, not better: the server sums that column into a
> total with no tax in it, so the two sides would then disagree by exactly the VAT on every order —
> the failure `ORD-07`'s sync work spent three slices removing. The fix is a field, and it belongs
> with `BR-ORD-6`'s re-price comparison rather than inside a screen slice.

### 5.1 Two things the rules leave to the schema (W11 slice 0)

Both are settled here rather than when the code needs them, because neither can be applied to orders
that already exist — the same argument `Source` on `Visit` made in W9 slice 0, arriving a second time.

**A disagreement is stored as two numbers, not a boolean.** `BR-ORD-6` says a server re-price that
differs is *flagged, not silently changed*, which fixes what must **not** happen and leaves open what
is recorded. A flag alone says something was wrong without saying what, and the question anyone asks
next — by how much, and on which line — would need a recomputation that no longer has the inputs. So
the order keeps **the device's totals as the record**, and the server's recomputation is stored
**beside** them with the snapshot version each was produced under.

That is the opposite of what an audit does, and the contrast is the point. `BR-AUD-8` has the server's
recomputed score *replace* the device's, because a score is a derived measurement and the server's is
the more trustworthy one. An order's prices are **what a human being agreed to buy at**. Overwriting
them would change the commercial fact the rep and the shopkeeper settled on, so here the device wins
the record and the server wins the annotation.

**An order has one identity and many submissions.** [F4](#f4--rejected-order-remediation) already
answers the identity question — a rejected order is *re-opened*, retained server-side, and pulls back
to the rep's device — so it stays one order with one id, and "how many orders did this outlet place"
counts intent rather than attempts. What that leaves open is the part `BR-ORD-9` depends on: **each
submission is an append-only child record**, carrying its own mutation id, timestamp and outcome.

Without it, "the original submission's mutation id is terminal" is unverifiable — the aggregate would
hold only the latest attempt, and a replay of the rejected id would have nothing to be terminal
*against*. It is also what keeps the re-open honest with [B7](decisions-and-assumptions.md#b7--conflict-resolution-matrix):
device-owned data is append-only, and moving `Rejected → Draft` looks like an exception to that until
you notice the *history* is what appends while the aggregate is what re-opens.

> **Built in W11 slice 3, with the outcome column deferred.** `OrderSubmission` carries the mutation
> id, the submission time and its order; there is no outcome on it yet because nothing can produce a
> second value for one — every submission that exists today succeeded. It arrives in slice 4 with the
> rejection that makes it mean something, alongside the `Rejected → Draft` branch that is the whole
> reason the child record is append-only. Its mutation id is the same id
> [Sync's ledger](../architecture/adr/0007-offline-sync-strategy.md) keys on, deliberately: two
> different answers to "has this push already been applied" is how a replay lands twice.

## 6. Requirements

| ID | Requirement | MoSCoW | Phase |
|---|---|---|---|
| ORD-01 | Capture order lines against assortment; quantities in UoM/pack | Must | 3 |
| ORD-02 | On-device pricing (unit price + tax) via shared engine | Must | 3 |
| ORD-03 | On-device promotion application (line + order level) | Must | 3 |
| ORD-04 | Suggested list (assortment/MSL/last order) | Should | 3 |
| ORD-05 | Draft/resume; edit until submit | Must | 3 |
| ORD-06 | Order-minimum validation (**value**; quantity needs a UoM vocabulary — see `BR-ORD-5`) | Should | 3 |
| ORD-07 | Submit → sealed, locked, queued for sync | Must | 3 |
| ORD-08 | Snapshot-version capture + server re-price flag | Should | 3 |
| ORD-09 | Back-office Accept/Reject | Could | 4 |
| ORD-10 | Off-assortment ordering with reason | Could | 4 |
| ORD-11 | Returns / credit orders | Won't (v1) | — |
| ORD-12 | Server-rejected order re-opens editable; resubmit under new mutation id (BR-ORD-9) | Must | 3 |
| ORD-13 | Custom fields on orders, validated against the tenant field-definition catalog | Should | 3 |
| ORD-14 | Requested delivery date on the order header | Should | 3 |
| ORD-15 | Block submission for an outlet on order-hold/credit-block (reason `OUTLET_ON_HOLD`) | Should | 3 |

## 7. Offline behavior

Order capture is **fully offline** — the defining SFA moment. Pricing/promotions resolve on
device from synced reference data via the **shared engine**, so the rep sees correct totals with
no signal. Submitted orders are **device-owned, append-only, locked**, and pushed idempotently
via the outbox ([B7](decisions-and-assumptions.md#b7--conflict-resolution-matrix)). On reconnect
the server may re-price; any delta is **flagged** against the order rather than mutating it. If the
server **rejects** the order, it re-opens editable on the device for correction and resubmission
(BR-ORD-9) — rejected work is never stranded.

## 8. Module contract (exposed to others)

- `IOrderQuery` — orders for an outlet/visit/rep (reporting), including the current rejection.
- `IOrderIngest` — apply a pushed order (create/resubmit) through this module, used by **Sync** so
  domain rules run server-side ([module boundaries §7](../architecture/10-module-boundaries.md#7-module-registry)).
- Consumes `IAssortmentService`, `IPricingService`, `IVisitContext`, and `IFieldDefinitionCatalog`
  (custom-field validation, [Configuration module](../architecture/adr/0009-config-driven-customization.md)).
- Publishes `OrderSubmitted` (value, lines summary) → reporting/downstream boundary.

## 9. Acceptance criteria (sample)

- A rep builds and submits a promoted, multi-line order fully offline; totals match the server's
  recomputation; the order lands exactly once after reconnect.
- Editing an order after submit is not possible **while it is Accepted/pending**; the sole
  exception is a **server-rejected** order, which re-opens editable for correction (BR-ORD-9). A
  post-sync price change surfaces as a flag, not a silent edit.

## 10. Open questions

- Is back-office **Accept/Reject** in the demo scope, or just "Submitted"? (Assumed: Submitted
  is the terminal demo state; Accept/Reject is a *Could*.) **Partly settled (W11 slice 0):**
  `ORD-12` is a `Must` and needs something to do the rejecting, so **rejection ships as an API with
  no screen** — driven in the demo by an `.http` request. The *screen* stays a `Could`.
- ~~Order-level promotions in v1, or line-level only?~~ **Answered by `BR-ORD-3`**, which already
  says at most one line-level promotion per line *plus* an optional order-level one. This question
  outlived the rule that settled it; noticed in W11 slice 0.
