# Functional Spec — Merchandising & Audits

> **Module:** Audit · **Group:** Field · **Phase:** 3 · **Status:** ✅ Baseline
> **Depends on:** Visit, Products · **Consumed by:** reporting (perfect-store)

## 1. Purpose

Merchandising & Audits captures **the state of the shelf** and scores it. Inside a visit, the
rep records availability, visibility, and price compliance, backs it with photos, and the
module computes a **perfect-store score**. This is where field sales proves execution quality.
The model is **structured (not photo-only)** with **share-of-shelf** and a **configurable
weighted score** — see [decision A2](decisions-and-assumptions.md#a2--audit--perfect-store-structured-checks--share-of-shelf--photo).

## 2. Actors

| Actor | Interest |
|---|---|
| Field Rep | Runs the audit in-store, offline |
| Sales Ops / Admin | Authors audit templates, surveys, and score weights |
| Supervisor | Tracks perfect-store scores & compliance trends |

## 3. Core concepts

- **Audit** — a structured shelf assessment within a visit, made of measurement lines.
- **Availability check** — per MSL SKU ([B2](decisions-and-assumptions.md#b2--assortment--must-stock-list-msl)):
  *present / absent / out-of-stock*.
- **Share-of-shelf** — **facings** counted per SKU/brand → share-of-shelf %.
- **Price check** — observed shelf price vs. expected ([Pricing](13-products-and-pricing.md));
  flags mismatches.
- **Survey / questionnaire** — a **configurable form** ([A1](decisions-and-assumptions.md#a1--per-tenant-customization-config-driven-moderate))
  of typed questions (single/multi choice, number, text, boolean, photo), optional conditional
  logic.
- **Photo evidence** — one or more photos per audit section ([B5 sync](decisions-and-assumptions.md#b5--photo--binary-sync)).
- **Perfect-store score** — a **weighted** score across pillars *availability*, *visibility
  (share-of-shelf)*, *price compliance* (+ survey-driven pillars); **weights are tenant config**.

## 4. Capabilities & flows

### F1 · Author audit templates & surveys (back office)
- Admin defines, per channel/tenant: which pillars apply, the survey questions, and the **score
  weights**.

### F2 · Run an audit (in-store, offline)
1. From a Visit audit-step, the rep works the template:
   - marks **availability** for each MSL SKU,
   - enters **facings** per SKU/brand (share-of-shelf),
   - records **shelf prices** (price check),
   - answers **survey** questions,
   - captures **photos**.
2. The **perfect-store score** is computed **on-device** from the entries + weights (instant
   feedback for the rep).

### F3 · Review (back office)
- Supervisors see scores, pillar breakdowns, photos, and trends per outlet/territory.

## 5. Business rules

- **BR-AUD-1** Availability checks are driven by the outlet's **MSL** ([B2](decisions-and-assumptions.md#b2--assortment--must-stock-list-msl)).
- **BR-AUD-2** Share-of-shelf % = own-SKU/brand facings ÷ **total category facings**. The rep
  captures own-SKU facings (numerator) **and a total-category-facings count** (denominator) — the
  denominator is **not** the sum of own facings (that would always be ~100%). Without a captured
  total, share-of-shelf is *not computed* (the pillar is skipped, not faked). A lightweight
  competitor catalog is a possible future denominator source (*Could*, AUD-11).
- **BR-AUD-3** Price-check compares to the **expected price** resolved for that outlet/date
  ([Pricing](13-products-and-pricing.md)); a delta beyond tolerance is a compliance flag.
- **BR-AUD-4** The perfect-store score is a **weighted** combination of pillar scores; weights
  come from the **Configuration module** (`IScoreWeights`) and must sum to 100%.
- **BR-AUD-5** Score computation is **deterministic** and runs identically on device and server —
  under the **same decimal discipline as pricing**: the TS device engine uses a decimal library
  (never native `number`) with the documented **round-half-up** policy, and agreement is proven by
  **generated cross-language vectors** (BR-AUD-12; mirrors [BR-PRD-8/9](13-products-and-pricing.md#decimal-parity-resolves-finding-s4)).
  Share-of-shelf ratios and weighted sums are exactly where float64 would diverge.
- **BR-AUD-6** An audit belongs to a visit and is **sealed with it** (append-only, not editable
  after sync — [B7](decisions-and-assumptions.md#b7--conflict-resolution-matrix)).
- **BR-AUD-7** Mandatory survey questions must be answered before the audit step completes.
- **BR-AUD-8** The audit records the **weight-set version** it was scored against (as-of-capture).
  The server recomputes with **those** weights; re-weighting a tenant does **not** retroactively
  re-score sealed audits — historical scores stay comparable, and trend views ([AUD-09](#6-requirements))
  note the weight-version boundary rather than silently mixing scales.

## 6. Requirements

| ID | Requirement | MoSCoW | Phase |
|---|---|---|---|
| AUD-01 | MSL availability check (present/absent/OOS) | Must | 3 |
| AUD-02 | Facings capture (own SKUs) **+ total-category facings** → share-of-shelf % | Must | 3 |
| AUD-03 | On-shelf price check vs. expected + compliance flag | Must | 3 |
| AUD-04 | Configurable survey/questionnaire forms (typed questions) | Must | 3 |
| AUD-05 | Photo evidence per section | Must | 3 |
| AUD-06 | Configurable weighted perfect-store score, computed on-device | Must | 3 |
| AUD-07 | Back-office audit template + weight authoring | Must | 3 |
| AUD-08 | Conditional survey logic (show-if) | Could | 4 |
| AUD-09 | Perfect-store trends & pillar breakdown reporting | Should | 3 |
| AUD-10 | Planogram-coordinate compliance | Won't (v1) | — |
| AUD-11 | Lightweight competitor catalog as share-of-shelf denominator | Could | 4 |
| AUD-12 | Decimal-parity score engine (C#≡TS, generated vectors) | Must | 3 |

> AUD-10 is explicitly **out** for v1 per [A2](decisions-and-assumptions.md#a2--audit--perfect-store-structured-checks--share-of-shelf--photo)
> (coordinate-based planograms were the rejected, heaviest option).

## 7. Offline behavior

Audits run **fully offline** inside a visit. Templates, MSL, and expected prices are synced
reference data; entries and the computed score are **device-owned, append-only** and pushed via
the outbox. **Photos** are downscaled on-device and uploaded **separately** on reconnect via
presigned URLs, retried independently of the JSON push ([B5](decisions-and-assumptions.md#b5--photo--binary-sync)).

## 8. Module contract (exposed to others)

- `IAuditQuery` — audits/scores for an outlet/visit (reporting).
- `IPerfectStoreScore` — score computation (shared server/device, decimal-parity per BR-AUD-5/12).
- `IAuditIngest` — apply a pushed audit through this module, used by **Sync** ([module boundaries §7](../architecture/10-module-boundaries.md#7-module-registry)).
- Consumes `IAssortmentService`, `IPricingService` (MSL + expected price), `IVisitContext`, and
  `ISurveyForms` + `IScoreWeights` (Configuration — survey definitions & weights, AUD-04/06/07).
- Publishes `AuditCompleted` (score, flags) → reporting.

## 9. Acceptance criteria (sample)

- An offline audit computes the same perfect-store score the server recomputes from the same
  entries and weights.
- Photos captured offline appear against the audit after reconnect, even if the JSON push
  succeeds before the images finish uploading.

## 10. Open questions

- Facings per **SKU** vs. per **brand** as the share-of-shelf base — tenant-configurable?
  (Assumed: SKU, roll up to brand.)
- Price-check tolerance default. (Assumed: tenant-config, default 0.)
