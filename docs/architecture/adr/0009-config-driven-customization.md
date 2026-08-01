# ADR-0009: Config-driven customization model

- **Status:** Accepted
- **Date:** 2026-08
- **Deciders:** Tiberiu Socea
- **Related:** decision [A1](../../product/decisions-and-assumptions.md#a1--per-tenant-customization-config-driven-moderate),
  [data & persistence](../14-data-and-persistence.md)

## Context

Real SFA platforms are *"highly customizable"* per tenant, but customization is where products go
to die: an over-general metadata/EAV engine becomes an unmaintainable abstract soup, while a rigid
schema can't tell the customization story at all. [Decision A1](../../product/decisions-and-assumptions.md#a1--per-tenant-customization-config-driven-moderate)
picked the middle path; this ADR fixes the *mechanism*.

## Decision

**Config-driven customization with a fixed schema.** Tenants customize **data, forms, workflows,
and theme** — not entities or code.

### 0. An owning module: `Configuration` (resolves finding S5)
Customization is not a floating concern — it is a **bounded context with an owner**. A dedicated
**Configuration module** (schema `config`, [module registry §7](../10-module-boundaries.md#7-module-registry))
owns all definitions: the field-definition catalog, visit-workflow, survey/audit forms, and
perfect-store weights. It exposes `IFieldDefinitionCatalog`, `IVisitWorkflow`, `ISurveyForms`,
`IScoreWeights`, and — because definitions are reference config the device needs offline — an
`IReferenceChangeFeed`. Definitions are **snapshot-versioned reference data**: they sync to the
device like prices, and a value captured offline against definition version *v* is validated
against *v* (**as-of-capture**, [sync engine §4](../12-offline-sync-engine.md#4-push-protocol-device-owned-mutations)),
so a mid-offline-window definition change doesn't silently invalidate captured work.

### 1. Custom fields — typed JSONB + a definition catalog
- Core entities (outlet, product, order, visit) have a `CustomFields JSONB` column.
- A per-tenant **field-definition catalog** (`{ key, label, type, required, validation, options }`)
  describes what's allowed. Values are **validated against the catalog** on write (server-side);
  the UI renders fields dynamically from it.
- JSONB (not EAV tables) keeps reads simple and lets Postgres index hot custom fields (GIN /
  expression indexes) when needed ([data & persistence](../14-data-and-persistence.md)).

### 2. Configurable workflows & forms
- The **visit step sequence** and **survey/audit forms** are tenant **configuration** (ordered
  step/question definitions), interpreted by the field app — not branches in code
  ([Visit](../../product/21-visit-execution.md), [Audit](../../product/22-merchandising-and-audits.md)).

### 3. Theming
- Per-tenant branding via **design tokens** (CSS variables), consumed by the shadcn/ui components
  ([A7](../../product/decisions-and-assumptions.md#a7--ui-toolkit-shadcnui--tailwind)).

### Explicitly out
Tenant-defined **entities**, tenant-authored **logic/scripts**, and arbitrary relationships. The
schema is fixed; only the fields/forms/workflows/theme flex.

## Options considered

| Option | Verdict | Why |
|---|---|---|
| Fixed schema, no customization | Rejected | Can't tell the platform's signature "customizable" story. |
| **Config-driven (JSONB fields + config forms/workflows)** | **Chosen** | Meaningful flexibility, bounded complexity, strong architecture talking point. |
| Full metadata/EAV engine | Rejected | Maximum flexibility, maximum accidental complexity; a solo-build trap and a query/perf minefield. |

## Consequences

**Positive**
- Genuine per-tenant flexibility with a **comprehensible, queryable** schema.
- Custom fields, forms, and workflows sync to the device like any other reference config
  ([sync engine](../12-offline-sync-engine.md)) and render dynamically.
- Clear, defensible boundary on how far customization goes.

**Negative / costs**
- Server-side **validation against the definition catalog** is essential (JSONB is schemaless by
  itself) — a real component, not free. Note it is a **mirrored surface**: the device pre-validates
  from the same definitions for UX, so required/regex/range semantics can drift — but the server
  **always re-validates**, so drift degrades UX, not integrity (lower stakes than the pricing/score
  decimal mirror, but real).
- **Definition version retention.** "As-of-capture" validation/scoring means the module must keep
  **historical definition versions**, not just current state (a storage/lifecycle consequence, not
  just a schema one) — see [Configuration BR-CFG-1](../../product/14-configuration.md#5-business-rules).
- Dynamic form/workflow rendering adds front-end complexity.
- Reporting on custom fields is limited to indexed JSONB paths (acceptable for operational needs).

**Follow-up:** the owning module and its lifecycle are specified in the
[Configuration functional spec](../../product/14-configuration.md); the field-definition catalog and
JSONB indexing approach are in [data & persistence](../14-data-and-persistence.md).
