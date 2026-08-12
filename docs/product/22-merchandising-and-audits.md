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
  > **"Sealed with it" is about when the audit was *taken*, not about the visit's state on arrival**
  > (settled in W11 slice 8d, after the first reading made every offline audit impossible).
  >
  > Read as "a checked-out visit refuses an audit", the rule has no case left: a pushed
  > `CapturedVisit` is created **already checked out**, and a device only enqueues one *at*
  > check-out — so every offline audit arrives at a sealed visit. The order module had the same
  > sentence and the same defect.
  >
  > What it protects against is a measurement taken *after* the visit was filed as done, so that is
  > what is compared: `CapturedAudit.CapturedAtUtc` against the visit's check-out. Both come from the
  > same device's clock, which is what makes the comparison sound on a phone that is wrong about the
  > time. The boundary is **inclusive** — an audit sealed as the rep walks out is the ordinary end of
  > a call.
- **BR-AUD-7** Mandatory survey questions must be answered before the audit step completes.
- **BR-AUD-8** The audit records the **weight-set version** it was scored against (as-of-capture).
  The server recomputes with **those** weights; re-weighting a tenant does **not** retroactively
  re-score sealed audits — historical scores stay comparable, and trend views ([AUD-09](#6-requirements))
  note the weight-version boundary rather than silently mixing scales.

### The three the score cannot be given later (W10 slice 0)

The rules above say *what* is true. Three of them have a consequence that has to be settled before
the first audit is stored, because none can be applied to audits that already exist. This section is
where those are settled, so that slices 1–6 implement a decision rather than each make one.

#### 1 · A published weight set is immutable, and versioned

`BR-AUD-8` has the server recompute a pushed audit using **the weights the audit was scored
against**. That sentence is only meaningful if a published weight set can never change: otherwise
"recompute with version 3" means whatever version 3 says *today*, an administrator adjusting a slider
silently rewrites last quarter's scores, and the device and server can disagree about a completed
audit with neither of them wrong.

So a weight set follows the same lifecycle a journey plan already does — **draft, then published,
and publishing is one-way**. Changing weights means publishing a new version; the old one stays
readable forever because sealed audits point at it.

> **There is no backfill, which is why this is a slice 0.** An audit stored before versioning exists
> has no version to record and nothing to point at, and no amount of later work can invent one. This
> is the same argument `Source` on `Visit` made in W9 slice 0, and it was right there for the same
> reason: a field that can only be true going forward has to exist before the rows do.

#### 2 · A skipped pillar is renormalised away, not scored zero

`BR-AUD-2` skips share-of-shelf when the rep captured no category total — "the pillar is skipped, not
faked". `BR-AUD-4` has weights sum to 100%. Both cannot hold unless something gives, and the two
candidates give materially different numbers for the same shelf:

| | Availability 80 (w 50) | Share-of-shelf **not captured** (w 30) | Price 90 (w 20) | Score |
|---|---|---|---|---|
| **Score the gap zero** | 40 | 0 | 18 | **58** |
| **Renormalise** | 40 | — | 18 | **83** |

**Renormalise**: the score is the weighted mean over the pillars that *were* measured —
`Σ(pillar × weight) ÷ Σ(weight of measured pillars)`.

Scoring the gap zero treats "unknown" as "bad", which is exactly the faking `BR-AUD-2` refuses. It
also punishes a rep for a measurement they could not take — a category with no shelf tag to count, a
kiosk with no comparable section — and makes a store look worse than one whose share-of-shelf is
genuinely poor. That inverts the meaning of the number.

> **The cost, stated because it is real.** Renormalising creates a gaming vector: a rep who skips the
> pillar they are weakest at scores higher than one who measures it. Two things make that visible
> rather than free. The audit records **which pillars were scored**, so a supervisor comparing two
> stores can see they were scored on different bases; and `AUD-09`'s pillar breakdown shows the
> skipped pillar as skipped rather than as a low bar. The alternative would hide a *worse* problem —
> a score that silently means "this rep works a format where the denominator is uncountable".
>
> **If no pillar could be scored, the score is `null`, not `0`.** An audit with nothing measurable is
> not a perfect-store failure; it is an audit that says nothing, and averaging it into a trend as a
> zero would be the same lie one pillar deeper.

#### 3 · An audit is its own mutation, queued with its visit

`BR-AUD-6` seals an audit with its visit, which argues for one payload carrying both. The module
registry names `IAuditIngest` separately, which argues for two. It is decided here rather than in
slice 6 because it is a **wire** decision, and
[`vectors/sync/push.v1.json`](../../vectors/sync/push.v1.json) is read by both languages — the shape
gets pinned by a file the moment it is built, and deciding twice is what that file exists to prevent.

**Two mutations.** The device queues the audit in the *same transaction* that seals the visit, so
"sealed with it" holds where it matters — on the device, where the work is. The outbox drains oldest
first, so the visit lands before its audit.

The deciding argument is what happens when one of them is refused. `/sync/push` answers per mutation
precisely so a batch of twenty does not fail over one bad outlet id — one payload would make an audit
refused on its merits reject a **completed visit**, which is the "lose the nineteen" failure the push
protocol was designed around. Two mutations means the visit lands, the audit's refusal is a result a
person can act on (`OFF-09`), and an audit whose visit was itself refused is refused in turn for a
reason that reads correctly: there is no such visit.

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

### What the module stores, and what it deliberately does not (W10 slice 3a)

The `Audit` aggregate holds **measurements and nothing else**: availability per product, facings per
product plus the category total, and observed-against-expected prices. Consequences worth stating:

- **Append-only, and created sealed.** There is no edit path at all, not even a private one — a
  module with no mutating method is one that cannot be argued into having one. `BR-AUD-6` becomes a
  property of the type rather than a rule somebody remembers to check.
- **One audit per visit**, enforced in the aggregate *and* in the schema. Two would leave "this shop's
  availability last Tuesday" with two answers and no rule for choosing.
- **No score is stored.** `AUD-06` is W10 slice 4 and derives it from these numbers plus the weight
  version the audit records. A stored score would be a second answer that could disagree with the
  recomputation `BR-AUD-8` promises.
- **Nothing is re-resolved server-side.** The MSL (`BR-AUD-1`), the expected price (`BR-AUD-3`) and
  the weight-set version were all resolved on the device while the rep was at the shelf. Asking
  Products or Configuration *now* would describe the audit under configuration republished since.
- **Only what could not have been observed is refused**: a negative count, one product measured twice
  in one section, prices in two currencies, and an audit that measured nothing at all. A server
  second-guessing observations teaches reps to enter whatever gets accepted.
- **A replay is success.** Audit and Sync commit separately, so a pushed audit can land and lose its
  ledger entry; the retry — checked *before* the visit's seal, because a rep may have checked out in
  between — returns success rather than stranding work that is already stored.
- **Reading an audit is `visit:read`**, not a permission of its own. An audit *is* what happened
  during a visit.

Survey answers and photo references land in **W10 slice 3b**, on the same aggregate:

- **An answer carries the question as it was asked**, not just its key. A form can be re-worded — or
  a question dropped — between the rep answering and the push arriving, and a key alone would then be
  an answer nobody can read. The same copy a visit makes of its workflow step (`BR-VIS-6`).
- **`BR-AUD-7` is enforced on the device, not on arrival.** "Mandatory questions must be answered
  before the audit step completes" is a rule about *completing a step*, which happens with the rep
  looking at the form. Re-checking it server-side would test the answers against the questionnaire as
  it reads **today** — refusing an audit for a question that gained its mandatory flag after the rep
  worked the shelf. The same as-of-capture reasoning that keeps this module from re-resolving the MSL.
- **The one thing Audit asks Configuration is whether the named form exists.** An answer set naming no
  questionnaire is uninterpretable — `AUD-09` would hold responses belonging to nothing. What is not
  asked is whether the answers *satisfy* the form.
- **An answer's value is a string** whatever the question's type was. The alternative is five nullable
  columns of which four are always null, and a sixth the day a type is added; the type lives on the
  question, where a reader that cares can find it. An **empty** value is a real answer — "nothing to
  add" is a finding — so only the question has to be present.
- **A photo is a reference and nothing checks the object.** Images are downscaled on-device and
  uploaded separately on reconnect (`B5`); the JSON push regularly wins that race, and the upload path
  itself is W11 (`OFF-08`) — so every key stored today points at nothing. A reader shows a gap, never
  an error. What *is* refused is a reference with no key, or one object referenced twice in an audit.
- **An audit that is only a questionnaire, or only a photograph, is a real audit.** A shop that will
  not let a rep count the shelf still lets them answer questions and take a picture.
- **`AuditSection` is deliberately not `ScorePillar`.** The first three members read alike and then
  the lists diverge: `Survey` and `General` are things a rep points a camera at and nothing weighs.
  Sharing one enum would make adding a scored pillar silently change where photos can be filed.

### How the score is actually computed (W10 slice 4)

A pure function — measurements and a weight set in, a score out — so the same rules run on a phone
that is offline and on the server (`BR-AUD-5`). `decimal` throughout, never `double`: a
share-of-shelf ratio is a division, and `7 / 30` in float64 is where two systems start disagreeing
about one shelf.

| Pillar | Computed as | Skipped when |
|---|---|---|
| Availability | products **present** ÷ products checked | nothing was checked |
| Share of shelf | own facings ÷ **captured category total**, capped at 100 | no total, no facings, or a total of 0 |
| Price compliance | prices within tolerance ÷ prices **with an expected price** | nothing had an expected price |

Then `Σ(pillar × weight) ÷ Σ(weight of measured pillars)` — the renormalisation decided in slice 0.

Consequences worth stating:

- **Absent and out-of-stock are both misses.** They mean opposite things to the business, which is
  why they are stored separately; from the shelf's point of view the product was not there to sell.
  Splitting them is `AUD-09`'s job, not the score's.
- **An unpriced product leaves the denominator as well as the numerator.** The gap is in somebody's
  price list, not the rep's work, and scoring it either way would be a claim nobody measured.
- **The price tolerance is absolute.** Charging under the expected price is as non-compliant as
  charging over — an under-price is a margin leak and often an unauthorised promotion. The bound is
  inclusive: "within 5 bani" plainly includes 5. The default is **0** ([§10](#10-open-questions)) and
  nothing configures it yet.
- **Share of shelf is capped at 100.** Own facings above the category total is a miscount — usually
  the rep forgot to include their own products in the total. Uncapped it produces a score above 100,
  which means nothing to any consumer; the raw numbers stay in the audit, so the miscount is still
  visible.
- **The score is null, not zero, in two cases**: nothing was measured, or every pillar that *was*
  measured is weighted zero. A zero would be a claim about a shop nobody looked at, or a verdict with
  no basis.
- **The total is computed from the rounded pillar percentages**, not from the unrounded ones. That
  loses a hair of precision and buys a breakdown that reconciles with its total — which is what a
  supervisor reading `AUD-09` needs, and what makes the parity vectors able to check the intermediate
  values as well as the answer.

**The score is computed at ingest and stored** (W10 slice 6). `IScoreWeights` resolves the version
the audit names — **published sets only**, because a draft can still be edited and an audit scored
against one would have a score nobody could reproduce — and `Audit.Record` scores in the same step as
storing, so the score, the entries and the version are one row that either exists or does not.

That reverses a line written in slice 4 ("no stored score"), and the distinction is worth keeping:
what would have been a second answer is the *device's* score, which is why the wire carries none. What
is stored is the server's own recomputation over sealed inputs. A score derived on read would instead
change silently the day the scorer is corrected — re-scoring a sealed record without anyone deciding
to.

An audit naming a version this tenant never published is **refused** (`audit.ingest.weightSetUnknown`)
rather than scored against something else, and — because `/sync/push` answers per mutation — that
refusal cannot strand the visit the audit belonged to.

**`BR-AUD-5` is checked, not asserted** (W10 slice 5). `frontend/lib/audits/score.ts` is the device
mirror, on `decimal.js` with the same `ROUND_HALF_UP` clone money uses, and both engines run the same
files under [`vectors/audits/`](../../vectors/README.md) — 16 hand-written cases that say what the
rules should be, and 400 generated ones that give the mirror an oracle across shapes nobody would
author. Each case **carries its own weight set**, which no other vector file does: the score's
arithmetic is configured, and renormalisation only shows up when the weights vary.

### What reaches the device (W10 slice 7)

Survey forms and the perfect-store weightings pull like any other reference data — tenant-wide, no
scope, their own cursors. Two things about the weightings are unlike every other feed:

- **Every published version travels, not just the newest.** An audit records the version it was
  scored against (`BR-AUD-8`), so a device holding work captured last week has to be able to show
  the rep what that audit scored. It is cheap in the way that argument usually is not: a published
  set is immutable, so each version downloads exactly once, and a tenant re-weights a few times a
  year.
- **Only published ones travel.** A device scoring against a draft would produce a number the server
  cannot reproduce, and would then have that audit refused on push — so the device never sees a
  version it cannot legitimately name.

A weight's percentage crosses the wire and is stored on the device as a **string**. `BR-AUD-5` has
the two scores match exactly, `decimal.js` reads a string, and `JSON.parse` would turn a bare
`33.34` into a float before the device's scorer ever saw it.

## 7. Offline behavior

Audits run **fully offline** inside a visit. Templates, MSL, and expected prices are synced
reference data; entries and the computed score are **device-owned, append-only** and pushed via
the outbox. **Photos** are downscaled on-device and uploaded **separately** on reconnect via
presigned URLs, retried independently of the JSON push ([B5](decisions-and-assumptions.md#b5--photo--binary-sync)).

## 8. Module contract (exposed to others)

- `IAuditQuery` — audits for a visit, or an outlet's recent ones, newest first by **when the rep
  measured** (reporting). Read-only; everything that creates an audit goes through `IAuditIngest`.
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
