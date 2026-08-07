# Functional Spec — Products & Pricing

> **Module:** Products & Pricing · **Group:** Admin · **Phase:** 2 · **Status:** ✅ Baseline
> **Depends on:** — (Outlets/Organization for scoping) · **Consumed by:** Journey, Audit, Order

## 1. Purpose

Products & Pricing is the **commercial engine**. It holds the catalog reps sell, the
**assortments** that say which products belong in which outlets, the **price lists** that say
what they cost, and the **promotions** that discount them. Order capture computes prices and
promotions **on-device from this synced data**, so the model must be self-contained and
deterministic offline. This is the most rules-heavy module — see drafted mechanics
[B1](decisions-and-assumptions.md#b1--pricing--promotions) and [B2](decisions-and-assumptions.md#b2--assortment--must-stock-list-msl).

## 2. Actors

| Actor | Interest |
|---|---|
| Sales Ops / Admin | Maintain catalog, assortments, price lists, promotions |
| Field Rep | Sells against the resulting prices/promos (read-only, offline) |
| Supervisor | Reviews assortment compliance & promo coverage |

## 3. Core concepts

- **Product / SKU** — a sellable unit: code, name, brand, category, unit of measure, pack size,
  tax class, status. Names localizable ([A3](decisions-and-assumptions.md#a3--internationalization-full-multi-currency--multi-language-ui), Could).
- **Category / brand** — hierarchy for grouping, share-of-shelf, and reporting.
- **Assortment** — the set of products expected/allowed for a scope (per **channel**, with
  per-outlet overrides — [B2](decisions-and-assumptions.md#b2--assortment--must-stock-list-msl)).
- **Must-stock list (MSL)** — the subset of an assortment flagged *must-stock*; drives audit
  availability checks and the order suggested-list.
- **Price list** — a set of product prices in **one currency** ([Money](decisions-and-assumptions.md#a3--internationalization-full-multi-currency--multi-language-ui)),
  assigned per `(channel)` with optional per-outlet override.
- **Promotion** — a discount rule with a type, scope, validity window, and priority.
- **Tax class** — maps a product to a VAT rate per tenant/country.

## 4. Capabilities & flows

### F1 · Maintain catalog
- CRUD products with classification, UoM/pack, tax class, custom fields, and (optional)
  localized names.

### F2 · Manage assortments & MSL
- Define a channel assortment; flag MSL items; apply per-outlet overrides (add/remove).

### F3 · Manage price lists
- Create a price list (currency + effective window); set product prices; assign it to channels
  and/or specific outlets. Overlap resolved by specificity (outlet > channel) then effective date.

### F4 · Manage promotions
- Author promotions of these **types** ([B1](decisions-and-assumptions.md#b1--pricing--promotions)):
  - **% off** a product/category
  - **fixed amount off**
  - **volume / tiered** (buy N+ → discount)
    > 📝 ASSUMPTION: a tier's lower bound is **inclusive with no upper bound** — "N or more" — and
    > resolution takes the highest threshold the quantity reaches. Tiers therefore never state where
    > they stop, and so cannot leave a gap or an overlap by disagreeing about it, which is the same
    > reasoning as the half-open date window one dimension over. A tier below 2 is refused: "buy one
    > or more" is every line that matched at all, which is a **% off** wearing a tier's clothes.
    > Within one promotion the tiers are all percentages or all amounts, and amount tiers share one
    > currency (`BR-PRD-1`) — mixed sets are well-defined, since tiers are selected by quantity and
    > never compared, but they are refused as far likelier to be a slip than an intention.
  - **BOGO / bundle** (buy X get Y)
    > 📝 ASSUMPTION: what is given is stated as a **percentage off the given units**, so `100` is
    > free and the classic BOGO is not a separate shape from "buy two, get one half price". Naming
    > **no** get-product means *the same product that was bought* — which is the only workable
    > reading when the promotion targets a whole category, since there is then no single id to write
    > down; naming one turns the same mechanism into a cross-sell bundle. Both quantities are at
    > least 1: "buy none, get one" gives the product to anyone who orders anything, and "buy two, get
    > none" is a rule that does nothing while still winning the `BR-PRD-3` priority contest against
    > one that would have.
    >
    > This is the one type that **does not reduce a price** — it adds units — so `PRD-06` applies it
    > by adding an order line rather than adjusting one. It carries no `value` for the same reason.
- Set scope (product/category/outlet/channel), validity window, and **priority**.

### F5 · Price & promo resolution (the deterministic core)
Given `(outlet, product, quantity, date)`:
1. Resolve the applicable **price list** (outlet override → channel → default) → base price.
2. Resolve **line-level promotions** in scope & in-window; apply the **highest-priority single**
   one ([B1 stacking rule](decisions-and-assumptions.md#b1--pricing--promotions)).
3. Compute tax from the product's tax class.
This function is pure and runs **identically on server and device** (shared rules).

## 5. Business rules

- **BR-PRD-1** A price list has exactly one currency; **no implicit cross-currency math**
  ([A3](decisions-and-assumptions.md#a3--internationalization-full-multi-currency--multi-language-ui)).
- **BR-PRD-2** Price resolution specificity: **outlet override > channel > default**; ties
  broken by most-recent effective date.

> 📝 ASSUMPTION: **specificity is checked before recency, and there is a third tiebreak.** Two
> readings of "ties broken by most-recent effective date" are possible, and they disagree: a channel
> list published *after* an outlet override either beats it or does not. It does not — a price
> negotiated for one shop is a deliberate exception, and a channel-wide list should not erase it by
> being newer. Recency only separates candidates already equal in specificity.
>
> That leaves two lists at the same scope with the same effective date, which the rule does not
> cover. It is a data problem — an author has said two contradictory things — and no tiebreak makes
> it *right*. What one buys is determinism: the **higher price-list id, compared as big-endian
> bytes**, wins. Ids are UUIDv7 and creation-ordered, so this is the recency instinct applied one
> level down, and byte order is chosen over any platform's built-in Guid comparison because
> [.NET's sorts `ffffffff-…` below `00000001-…`](../../vectors/pricing/price-resolution.v1.json)
> while TypeScript's string comparison does not. Both readings are pinned by the shared vectors, so
> the device and the server cannot drift apart on either.
- **BR-PRD-3** At most **one line-level promotion per order line**, selected by priority; order-
  level promos are separate ([B1](decisions-and-assumptions.md#b1--pricing--promotions)).

> 📝 ASSUMPTION: **a higher priority number wins.** The rule says "selected by priority" without
> saying which end is which, and the opposite convention is at least as common — "priority 1" reads
> like *first* to most people. Higher-wins is chosen for what each does to the data over time: under
> lowest-wins, authoring a promotion that must beat everything already in place means renumbering the
> others, and once something sits at 1 the next one needs 0, then -1. Under higher-wins the author
> picks a bigger number and touches nothing else.
>
> **Ties are allowed at authoring time and broken at resolution.** Two promotions at the same
> priority are a legitimate intermediate state while someone is editing, so refusing them would block
> the edit rather than the mistake. `PRD-06` breaks the tie deterministically, for the same reason
> `BR-PRD-2`'s does: the answer must not depend on which device asked.
- **BR-PRD-4** A product not in an outlet's assortment cannot be ordered there (unless the
  tenant enables "off-assortment with reason" — a *Could*).
- **BR-PRD-5** Prices are stored **net**; tax is computed at order time from the tax class.
- **BR-PRD-6** Promotions apply only within their validity window (evaluated in the outlet's
  timezone).

> 📝 ASSUMPTION: **the timezone is honoured by requiring the business date, not by reading a clock.**
> Both resolution endpoints take a mandatory `?on=` and refuse a request without one. A default would
> mean the *server's* today, and an outlet in Bucharest changes day six hours before one in London —
> so a promotion running "1–30 June" would be live at the wrong moments for most of a tenant's
> estate. The date is therefore computed where the timezone is known (the device, or a caller holding
> the outlet) and handed in.
>
> That is also what keeps resolution **reproducible**, which `BR-PRD-7` needs and which an order
> re-priced during sync depends on: the same line must select the same promotion days later. A
> resolver that asked what day it is could not promise either property.
>
> **Selection ignores the size of the discount.** `BR-PRD-3` says "by priority" and this takes it
> literally — the highest-priority promotion wins even when a lower one saves more. That is what makes
> priority worth authoring, and it is pinned by a vector, because a resolver that quietly preferred
> the bigger saving would look correct on most data and be wrong exactly when it mattered.
>
> A promotion that cannot act at the ordered quantity — a tier below its lowest threshold, a bundle
> below its buy quantity — is **filtered out before the priority contest** rather than allowed to win
> and then do nothing. Same reasoning as authoring refusing a 0% discount.
- **BR-PRD-7** Price/promo resolution is **deterministic and side-effect-free** so it is
  identical offline and online.

## 6. Requirements

| ID | Requirement | MoSCoW | Phase |
|---|---|---|---|
| PRD-01 | CRUD products with classification, UoM, tax class, custom fields | Must | 2 |
| PRD-02 | Channel assortments + MSL flags + per-outlet overrides | Must | 2 |
| PRD-03 | Price lists (currency, effective window) assigned to channel/outlet | Must | 2 |
| PRD-04 | Deterministic price resolution (specificity + effective date) | Must | 2 |
| PRD-05 | Promotions: %-off, fixed, volume/tiered, BOGO/bundle (authoring) | Must | 2 |
| PRD-06 | Promotion resolution with priority + validity window (engine) | Must | 2 |
| PRD-07 | Tax computation from tax class × tenant/country | Should | 2 |
| PRD-08 | Shared price/promo engine usable on server **and** device | Must | 2 |
| PRD-09 | Localized product names | Could | 4 |
| PRD-10 | Off-assortment ordering with reason | Could | 4 |

> **Phase note:** promotion *authoring* and the *resolution engine* (PRD-05/06/07) are **Phase 2**
> — the engine ships with pricing so Order (Phase 3) has something deterministic to call. The
> visible *application* of promotions in the order UI is exercised in Phase 3. This matches the
> [delivery plan](../delivery-plan.md) (Week 6).

## 7. Offline behavior

All of this is **reference data**, pulled territory-scoped ([A4](decisions-and-assumptions.md#a4--offline-data-scope-territory-scoped))
and read-only on device: the products, assortments, applicable price lists, and active
promotions for the rep's outlets. The **price/promo resolution engine ships to the device**
(shared TypeScript/logic mirror of the server rules) so Order capture prices correctly offline
and gets the same answer the server would. Admin edits sync down on next pull; in-flight orders
keep the snapshot version they were priced against ([B7](decisions-and-assumptions.md#b7--conflict-resolution-matrix)).

### Decimal parity (resolves finding S4)

"Identical on server and device" is only true if both sides do **decimal**, not float, math. C#
uses `System.Decimal`; JavaScript/TypeScript has only IEEE-754 float64, which diverges from decimal
exactly at percentage/tiered discounts, tax, and rounding. Therefore:

- **BR-PRD-8** The TS device engine **must** use an arbitrary-precision decimal library
  (`decimal.js`/`big.js`) — never native `number` — for all money math.
- **BR-PRD-9** A **single documented rounding policy** applies on both sides: round **half-up** to
  the currency's minor units, **per line**, tax computed on the rounded net line. (Per-tenant/
  jurisdiction override is a *Could*.)
- Parity is proven by **generated / property-based** cross-language vectors, not only hand-written
  cases, so uncovered input regions can't hide drift ([testing strategy §2](../architecture/17-testing-strategy.md#2-unit-tests-many)).
- `Money` crosses the wire as a **string** amount + currency ([API contracts §1](../architecture/13-api-contracts.md#1-shape--conventions)) to avoid float coercion in transit.

## 8. Module contract (exposed to others)

- `IProductCatalog` — product/category lookups.
- `IAssortmentService` — assortment/MSL for an outlet (used by Order, Audit).
- `IPricingService` — `ResolvePrice(outlet, product, qty, date)` and promo evaluation (used by
  Order; mirrored on device, subject to BR-PRD-8/9).
- `IReferenceChangeFeed` (sync source) — territory-scoped, row-version delta of products/prices/
  assortments/promotions with tombstones, for **Sync** ([module boundaries §7](../architecture/10-module-boundaries.md#7-module-registry)).
- **Consumes** `IOutletClassification` (Outlets) — channel/segment for price-list resolution
  ([BR-PRD-2](#5-business-rules)).
- Publishes `PriceListPublished`, `PromotionActivated` → Sync triggers a reference delta.

## 9. Acceptance criteria (sample)

- The same `(outlet, product, qty, date)` yields the identical net price + promotion on server
  and device.
- An outlet-specific price overrides its channel price; an expired promotion is not applied.

## 10. Open questions

- Promotion **stacking** beyond "one line + one order-level" — needed in v1? (Assumed: no.)
- Are volume tiers per-line or per-order aggregate? (Assumed: per-line in v1.)
- Multi-currency at the *tenant* level (a tenant operating several currencies) vs. per price
  list only. (Assumed: per price list; a tenant may have several.)
