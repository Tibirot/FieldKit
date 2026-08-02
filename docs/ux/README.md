# UX & Wireframes

> **Status:** ✅ Baseline (key flows) · **Last updated:** 2026-08
> **Design direction:** [ADR-0004](../architecture/adr/0004-nextjs-offline-first-frontend.md) · decision [A7](../product/decisions-and-assumptions.md#a7--ui-toolkit-shadcnui--tailwind)

Mid-fidelity wireframes of FieldKit's key flows — enough to make the product legible and to guide
the build, without pretending to be final visual design.

## 🖼️ View the wireframes

**[▶ Interactive wireframes (Artifact)](https://claude.ai/code/artifact/e97b6c9d-43bb-4631-aae9-3c95104a12d0)**
— renders light/dark, responsive; every screen is tagged with the spec requirement it realizes.

> The wireframes use a **fictional** tenant (“Veridian”) and data. They are design mockups, not
> the running app.

## Design direction

- **Toolkit:** shadcn/ui + Tailwind ([A7](../product/decisions-and-assumptions.md#a7--ui-toolkit-shadcnui--tailwind)),
  themed via design tokens (the mechanism for per-tenant branding, [ADR-0009](../architecture/adr/0009-config-driven-customization.md)).
- **Palette:** cool slate neutrals + a **teal** brand accent; semantic colors kept *separate* from
  the accent — emerald = synced/good, amber = pending, rose = rejected — because sync state is the
  product's core story and must read at a glance.
- **Light & dark:** both palettes ship as token sets and resolve from the **device preference**
  (`prefers-color-scheme`) — pure CSS, so no theme JS and no flash of the wrong theme on a cold
  offline start. An explicit `.dark` / `.light` class on `<html>` overrides the preference; that arm
  is wired but nothing sets it today, so **no in-app theme toggle ships** — per-user theme *choice*
  is unspecified and deferred, and would plug in as a provider that sets the class. Beyond the root,
  `.dark` on any element re-themes **its whole subtree** (a forced-dark panel), while `.light` is a
  root override only — arbitrary nesting isn't expressible in a selector, so it isn't offered.
  (Per-*tenant* theme tokens are a separate concern —
  [CFG-08](../product/14-configuration.md#6-requirements).)
- **Two experiences, one app:** the **field app** is mobile-first and offline-first; the **back
  office** is desktop-first ([ADR-0004](../architecture/adr/0004-nextjs-offline-first-frontend.md)).
- **Type:** UI sans for the interface, monospace for the data layer (SKUs, quantities, prices,
  KPI figures) — grounded in a code-and-numbers domain.

## Screens & spec traceability

### Field app — the golden path (offline)

| # | Screen | Realizes | Notes |
|---|---|---|---|
| 1 | Today's Journey | `JRN-05` `JRN-03` `OFF-01` `OFF-05` | Day's stops, pulled for offline; status, distance, per-stop workflow |
| 2 | Check-in & geofence | `VIS-01` `VIS-02` `VIS-03` `VIS-04` | Geofence validation; outside-fence proceeds with a reason |
| 3 | Shelf audit & perfect store | `AUD-01` `AUD-02` `AUD-03` `AUD-05` `AUD-06` | MSL availability, facings/share-of-shelf, price check, photos, live score |
| 4 | Order capture | `ORD-01` `ORD-02` `ORD-03` `ORD-06` `ORD-07` | On-device pricing & promotions; order-minimum; submit |
| 5 | Sync & reconcile | `OFF-04` `OFF-06` `OFF-08` `OFF-09` | Idempotent push, out-of-band photos, synced/pending/attention states |

### Back office (desktop)

| Screen | Realizes | Notes |
|---|---|---|
| Supervisor dashboard | Reporting & KPIs, `JRN-10`, `AUD-09` | Coverage, strike rate, perfect-store, order value; coverage-by-territory; live activity |
| Outlets — master data | `OUT-01` `OUT-03` `OUT-04` `OUT-06` | Classified/territory-assigned/lifecycle; field-proposal review queue |
| Products & pricing | `PRD-01` `PRD-02` `PRD-03` `PRD-05` `PRD-06` | Catalog, assortment/MSL, price list, active promotions |
| Territories | `ORG-03` `ORG-04` `ORG-05` `A4` | Territory list, single active rep, channel mix; drives offline scope |
| Journey planning | `JRN-01` `JRN-02` `JRN-03` `JRN-04` | Week grid generated from frequency + capacity; frequency compliance |
| Users & roles | `IAM-03` `IAM-04` `IAM-05` `IAM-07` | Users, permission-bundle roles, one active device per rep |
| Visit-workflow / audit builder | `A1` `ADR-0009` `VIS-03` `AUD-04` `AUD-06` `AUD-07` | The config-driven story: per-channel steps, perfect-store weights, survey forms |

## Not yet wireframed

The set now covers all core admin and field flows. Remaining screens (order review/approval,
outlet detail with custom fields, survey run-through) reuse the same shell and are left to build
time.

## Source

The wireframes are a single self-contained HTML file (no build step, no external assets). To edit,
update the source and re-publish to the same Artifact URL.
