# Functional Spec — Configuration (Customization)

> **Module:** Configuration · **Group:** Admin · **Phase:** 1 → 3 · **Status:** ✅ Baseline
> **Depends on:** IAM · **Consumed by:** Outlets, Products, Visit, Audit, Order (validation & config)
> **Decision:** [A1](decisions-and-assumptions.md#a1--per-tenant-customization-config-driven-moderate) · [ADR-0009](../architecture/adr/0009-config-driven-customization.md)

## 1. Purpose

Configuration is the module that makes FieldKit **"highly customizable" per tenant** without a
metadata engine. It owns the *definitions* every other module reads to bend to a tenant's needs:
custom fields, the in-store visit workflow, survey/audit forms, and perfect-store weights. It was
added in review (finding S5) to give these definitions a single owner, contract, and lifecycle
rather than scattering them.

## 2. Actors

| Actor | Interest |
|---|---|
| Tenant Admin / Sales Ops | Author custom fields, visit workflows, survey forms, and score weights |
| Every module with custom fields | Validates values against the field-definition catalog |
| Field app | Renders workflows/forms/fields dynamically from synced config |

## 3. Core concepts

- **Field definition** — a per-tenant custom-field descriptor for an entity: `{ entity, key, label,
  type, required, validation, options }`. Governs the `CustomFields` JSONB on outlets, products,
  orders, visits ([ADR-0009](../architecture/adr/0009-config-driven-customization.md)).
- **Visit workflow** — the ordered, per-channel sequence of visit steps (audit/order/survey/photo/
  signature), each with a *mandatory* flag ([Visit VIS-03](21-visit-execution.md)).
- **Survey form** — a set of typed questions (single/multi/number/text/boolean/photo), optional
  conditional logic ([Audit AUD-04](22-merchandising-and-audits.md)).
- **Perfect-store weights** — the pillar weights (availability/visibility/price + survey-driven),
  summing to 100% ([Audit BR-AUD-4](22-merchandising-and-audits.md#5-business-rules)).
- **Theme tokens** — per-tenant branding (design tokens) ([A7](decisions-and-assumptions.md#a7--ui-toolkit-shadcnui--tailwind)).
- **Configuration set** — a **versioned bundle** of the above that ships to devices atomically (so
  cross-references — a workflow step → a survey form — never dangle).

## 4. Capabilities & flows

### F1 · Author custom fields
- Admin defines custom fields per entity; the owning module validates values on write.

### F2 · Build the visit workflow & forms (the builder)
- Admin composes the per-channel visit step sequence, survey questions, and perfect-store weights
  (the wireframe's [workflow/audit builder](../ux/README.md)). Publishing produces a new
  **Configuration set version**.

### F3 · Publish & sync
- A publish emits `ConfigurationPublished`; the new set version syncs to devices as
  **snapshot-versioned reference config** (via `IReferenceChangeFeed`), applied atomically.

## 5. Business rules

- **BR-CFG-1** Definitions are **versioned**; the module **retains historical versions** (not just
  current) so a value/score captured offline against version *v* can be validated/recomputed against
  *v* — the storage consequence of "as-of-capture" ([sync engine §4](../architecture/12-offline-sync-engine.md#4-push-protocol-device-owned-mutations)).
- **BR-CFG-2** A configuration set ships and applies **atomically** on the device — no partial apply
  that would leave a workflow step referencing a not-yet-pulled form.
- **BR-CFG-3** Custom-field validation runs **server-side authoritatively**; the device pre-validates
  from the same definitions for UX (a mirrored surface — kept simple: the server always re-validates,
  so drift degrades UX, not integrity).
- **BR-CFG-4** Perfect-store weights must sum to **exactly 100%** — checked on **every write**, not
  only at publish (`BR-AUD-4`; see [§6.4](#64-authoring-the-perfect-store-weighting-week-10)).
- **BR-CFG-6** A **published** weight-set version is **immutable**, and publishing is one-way:
  re-weighting drafts a new version. `BR-AUD-8` has the server recompute a sealed audit with the
  weights it was scored against, and that only means something if those weights cannot move
  ([audits §5](22-merchandising-and-audits.md)).
- **BR-CFG-5** Config is **reference data**: server-authoritative, read-only on device, no conflicts
  ([B7](decisions-and-assumptions.md#b7--conflict-resolution-matrix)).

## 6. Requirements

| ID | Requirement | MoSCoW | Phase |
|---|---|---|---|
| CFG-01 | Field-definition catalog (per entity) + `IFieldDefinitionCatalog` | Must | 1 |
| CFG-02 | Server-side custom-field validation against definitions | Must | 1 |
| CFG-03 | Visit-workflow definitions (per channel) `IVisitWorkflow` | Must | 3 |
| CFG-04 | Survey-form definitions `ISurveyForms` | Must | 3 |
| CFG-05 | Perfect-store weight config `IScoreWeights` (sum = 100%) | Must | 3 |
| CFG-06 | Versioned configuration set + change-feed to devices (atomic apply) | Must | 3 |
| CFG-07 | Historical version retention (as-of-capture validation/scoring) | Must | 3 |
| CFG-08 | Per-tenant theme tokens | Should | 2 |
| CFG-09 | Conditional survey logic (show-if) | Could | 4 |

### 6.1 What is built (Phase 1)

`CFG-01` and `CFG-02` ship as the **current** catalogue only — one definition per `(entity, key)`,
no version history. That is deliberate rather than partial: `BR-CFG-1`'s retention exists to serve
**as-of-capture** validation, and nothing captures offline yet. Building version history now would
mean shipping a schema whose only reader arrives in Phase 3, designed against a sync protocol that
does not exist — the retention lands with `CFG-06`/`CFG-07`, alongside the change feed that makes it
mean something.

Five field types are supported: `Text`, `Number`, `Boolean`, `Date`, `Choice`. They are the types a
tenant can describe with a rule the server can enforce without a second module — a photo or a
reference field needs storage or a lookup, so those belong with the builder in Phase 3.

Consequences worth stating:

- **A key is immutable after creation.** It is the JSONB key already written into every row; a rename
  would orphan every value stored under the old one. Labels change freely — that is what an admin
  actually wants when they say "rename this field".
- **Deleting a definition does not rewrite data.** The values stay in the JSONB and simply stop being
  described. It stops the field being collected; it is not a redaction.
- **Values are replaced wholesale on write, not patched.** An empty map clears them, which is the only
  way an optional field can be unset over a `PUT` that carries the whole entity.
- **An undescribed key is rejected, not dropped.** Silently discarding it would lose an import's data
  with no signal — and the catalogue exists precisely so that what is stored can be described.

### 6.2 Authoring the catalogue (Week 5)

`F1` reaches a screen at `/outlets/custom-fields`, linked from the outlet header and gated on
`config:read` / `config:write`. One entity per screen rather than an entity picker: outlets are the
only entity with custom fields wired through today, and products bring their own catalogue and their
own screen in W6.

Three things the screen has to say out loud, because they are all consequences the API cannot undo:

- **The key is derived from the label, and fixed once saved.** Left to themselves an admin types
  "Chiller count" into both boxes and the second is refused, so the key is filled in from the label
  — diacritics folded rather than treated as separators, since "Suprafață de raft" must not become
  `suprafa_de_raft`. It stops deriving the moment someone types a key of their own.
- **Deleting is confirmed, and the confirmation names the cost.** Unlike a channel, which the API
  refuses to delete while outlets use it, this cannot be refused: the values live in another module's
  rows and Configuration may not read them
  ([ADR-0005](../architecture/adr/0005-postgres-schema-per-module.md)).
  They stay where they are, undescribed, until each outlet is next saved — and then they are gone.
- **Only the constraints the chosen type carries are sent.** The API clears options on a non-choice
  itself but keeps `maxLength` and the bounds, so a field briefly typed as a number would otherwise
  hold bounds that render nowhere, validate nothing, and become authoritative again the moment
  someone switched it back.

Everything else stays the server's to decide. A choice with no options and a minimum above its
maximum are both refused with the offending control named, and the API's problem fields are already
the form's field names — so a refusal lands beside the control that caused it without a mapping table
that could drift ([api-contracts §3](../architecture/13-api-contracts.md)).

### 6.3 Authoring survey forms (Week 10)

`CFG-04` ships as `/api/config/surveys` — a tenant's questionnaires, each with an id, a name, and an
ordered list of typed questions (`Text`, `Number`, `Boolean`, `SingleChoice`, `MultiChoice`,
`Photo`). Created, replaced wholesale by id, deleted.

**Named and identified, not keyed by channel.** A visit workflow is keyed by channel because a
channel has exactly one answer to "how is a visit worked here". A tenant genuinely runs several
questionnaires at once — a standing compliance form and a quarterly brand survey — so a form has an
id and a name a person picks from a list.

Consequences worth stating:

- **An answer is filed under a question's `key`, not its id.** Questions are replaced wholesale on
  every edit and their ids are regenerated with them, so an id would leave `AUD-09`'s reporting
  holding a dangling pointer after the first re-wording. A key survives reorders, rewordings and
  re-authoring — the same bargain `BR-CFG-1`'s custom-field key makes. The key is an identifier
  rather than prose (`^[a-z][a-z0-9_]{0,59}$`) and the authoring screen derives it from the question
  text, as the custom-field screen already does.
- **An empty form is refused**, unlike an empty visit workflow. A workflow with no steps is a real
  thing — a presence call. A form with no questions is a screen that opens and offers nothing to do.
- **A choice question must offer something to choose from.** A mandatory one that does not would make
  the audit step impossible to finish (`BR-AUD-7`).
- **Options are kept only for the choice types** and dropped for everything else, so they cannot
  survive a type change to become quietly authoritative again.
- **`mandatory` and `options` may be omitted; key, text and type may not.** A question that is
  optional by omission costs an unanswered box; one that is mandatory by omission blocks a rep's
  check-out over a flag nobody typed.
- **No version numbers**, unlike the weight sets. Those are versioned because `BR-AUD-8` recomputes
  a sealed audit with the exact numbers it was scored against; a form makes no such arithmetic
  promise, because an audit stores the answers it was given together with the question as it was
  asked. Row-versioned for sync, like the visit workflow.

**Nothing points at a form yet.** A `Survey` step in a visit workflow names none; how an audit
chooses a form is W10 slice 3's decision, taken with the module that has to live with it. `ISurveyForms`
does ship, one slice ahead of that consumer, exactly as `IVisitWorkflow` shipped ahead of check-in.

**The screen** (`AUD-07`, W10 slice 9a) is `/configuration/surveys/[id]`, and two of its rules are
its own rather than the API's:

- **A question's key is fixed once the question has been saved.** The API would take a renamed key
  without complaint — the questions are replaced wholesale, so nothing there can tell a rename from a
  replacement. The screen refuses it because an answer is filed under the key, and Configuration
  cannot see whether a rep has answered yet (ADR-0005), so the only safe assumption about a saved
  question is that somebody has. An admin who wants to ask something else removes the question and
  adds another, which is the honest description of what they are doing. The key is *disabled* rather
  than hidden: it is what `AUD-09` groups by, so there is every reason to read it.
- **Order is edited with buttons, not by dragging.** The wireframe draws a handle. A drag-only
  reorder cannot be operated from a keyboard and is invisible to a screen reader, and order is the
  whole meaning of this list — so the move is a pair of buttons and it is announced. A handle can be
  added on top later without changing the model underneath.

Two things the screen catches before the round trip, because the API's refusal cannot say *which*
question: a **duplicate key** — which the screen itself causes, since it derives keys from question
text and two questions worded alike derive one key — and a **choice with no options**. Both
questions in a collision are marked, not the newcomer: whichever is renamed fixes it.

**The list** (`/configuration/surveys`, W10 slice 9b) shows each form with what it asks — the number
of questions, and how many of them a rep cannot skip, because that second number is what decides
whether an audit step can be finished at all (`BR-AUD-7`). Sorted by the server, not re-sorted here.

Its **delete confirmation says what does not happen**, which is the opposite of the custom-field
catalogue's warning and the more surprising fact. Deleting a field leaves values undescribed, and
they vanish the next time their row is saved. Deleting a form loses nothing: the answers already
given stay in Audit's rows and stay **readable**, because each carries its question's text as it was
worded. Configuration can neither remove them nor refuse the delete on their behalf (ADR-0005), and
does not need to.

### 6.4 Authoring the perfect-store weighting (Week 10)

`CFG-05` ships as `/api/config/score-weights` — a tenant's weighting **by version**, drafted, edited
while it is a draft, then **published one-way**. The lifecycle is the journey plan's, chosen for a
reason that is not symmetry: `BR-AUD-8` has the server recompute a pushed audit with the weights that
audit was scored against, and that is a sentence about a *fixed set of numbers*. A single editable
set, or a soft "current version" flag, would make "recompute with version 3" mean whatever version 3
says today.

Consequences worth stating:

- **Exactly 100, with no tolerance.** A tolerance is the right call for floating point and the wrong
  one here: these are decimal percentages an administrator typed, and `33.33 × 3` is exactly `99.99`
  in `decimal` — nothing to forgive. Waving it through would have the score renormalise against a
  total that is not 100, silently rescaling every audit stored under it.
- **The sum is checked on every write, not at publish.** Refusing only at publish would let an
  administrator build an invalid set over an afternoon and be told at the end, and would make the
  stored shape "sometimes valid" — something every reader then has to re-check.
- **A pillar may be worth nothing.** That is a tenant switching share-of-shelf off, and is a different
  thing from `BR-AUD-2`'s *skipped* pillar, which is a measurement the rep could not take.
- **The pillars are a closed set, not tenant-defined.** Each is computed from data captured in a
  particular way, so a tenant-named pillar would be a weight with no measurement behind it — and
  `AUD-09`'s cross-tenant trend views would compare vocabularies rather than numbers.
- **Nothing is ever deleted, and versions never restart.** The next version is `Max + 1`, not
  `Count + 1`: sealed audits name a number, and re-using one would re-point them.

`IScoreWeights` was **not** part of the endpoint slice: its first caller is the scorer, and an
interface with no caller is a guess about a shape — the same rule that kept the Journey contracts
waiting for theirs. It landed with that caller and is now in [§8](#8-module-contract-exposed-to-others).

**The screen** (`AUD-07`, W10 slice 8) is `/configuration/score-weights`, and its job is making the
one-way publish legible rather than merely enforced:

- **A published version has no edit control** — not a disabled one. Beside it is *start a new version
  from this*, pre-filled with its numbers, because an administrator re-weighting is usually adjusting
  one pillar and retyping the other two is how a typo enters a published set. The rule is shown as
  the thing to do next instead of as a warning about the thing that will fail.
- **Every version stays listed, newest first.** Sealed audits name one forever, so hiding the old
  ones would hide the only way to read a historical score.
- **A running total, always shown.** "Exactly 100, no tolerance" is a rule an administrator otherwise
  learns by being refused. The total is summed in **integer hundredths, each weight rounded before it
  is added** — the column is `numeric(5,2)`, so the screen totals what will be *stored*: `33.335 × 3`
  is `100.02` in the row and `100.005` in the boxes. Rounding once at the end would agree with the
  typing and disagree with the database, and summing in float64 would refuse sets the server accepts
  (`0.01 + 64.04 + 35.95` is `100.00000000000001` there).
- **A reader sees no controls at all**, per the [UX note](../ux/README.md#what-week-5-actually-builds)
  on hidden-versus-disabled. It is reached from an Admin **Configuration** nav item that the
  wireframes do not draw — the reasoning is in that same note.

## 7. Offline behavior

All config is **reference data**: pulled (territory/tenant-scoped) and read-only on device, applied
as an **atomic versioned bundle**. Definitions changing mid-offline-window are reconciled
**as-of-capture** — a value/score captured under version *v* is validated/recomputed against *v*,
so a mid-day re-publish never silently invalidates captured work.

## 8. Module contract (exposed to others)

- `IFieldDefinitionCatalog` — definitions + validation (used by Outlets, Products, Order, Visit).
- `IVisitWorkflow` — step sequence per channel (used by Visit).
- `ISurveyForms` — survey/question definitions, by id or all (used by Audit). Returns **null** for a
  form nobody defined, unlike `IVisitWorkflow`'s default: an empty form is refused at authoring
  precisely because it is a screen that asks nothing, so there is no sensible default to invent.
- `IScoreWeights` — perfect-store weights per version (used by Audit).
- `IReferenceChangeFeed` — versioned config bundle delta, for **Sync**.
- Consumes `ITenantContext` (IAM). Publishes `ConfigurationPublished` → Sync triggers a config delta.

## 9. Acceptance criteria (sample)

- Adding a custom field to outlets makes it render dynamically in the back office and validate on
  save; an invalid value is rejected server-side even if the device let it through.
- Re-weighting perfect-store does **not** re-score sealed audits; new audits use the new weights and
  trend views mark the boundary.

## 10. Open questions

- Tenant-authored **conditional logic** depth (show-if only vs. richer rules) — assumed show-if
  (CFG-09, Could).
- Whether theme tokens are full theming or a constrained palette — assumed constrained.
