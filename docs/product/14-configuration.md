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
- **BR-CFG-4** Perfect-store weights must sum to **100%** before a set can be published.
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

## 7. Offline behavior

All config is **reference data**: pulled (territory/tenant-scoped) and read-only on device, applied
as an **atomic versioned bundle**. Definitions changing mid-offline-window are reconciled
**as-of-capture** — a value/score captured under version *v* is validated/recomputed against *v*,
so a mid-day re-publish never silently invalidates captured work.

## 8. Module contract (exposed to others)

- `IFieldDefinitionCatalog` — definitions + validation (used by Outlets, Products, Order, Visit).
- `IVisitWorkflow` — step sequence per channel (used by Visit).
- `ISurveyForms` — survey/question definitions (used by Audit).
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
