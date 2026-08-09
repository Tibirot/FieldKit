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
| Outlets — bulk import | `OUT-05` `CFG-01` `CFG-02` | Dry-run first; bad cells corrected in the grid before anything is written |
| Products & pricing | `PRD-01` `PRD-02` `PRD-03` `PRD-05` `PRD-06` | Catalog, assortment/MSL, price list, active promotions |
| Territories | `ORG-03` `ORG-04` `ORG-05` `A4` | Territory list, single active rep, channel mix; drives offline scope |
| Journey planning | `JRN-01` `JRN-02` `JRN-03` `JRN-04` | Week grid generated from frequency + capacity; frequency compliance. **Built as three screens, not one** (W7 slice 10): what a supervisor *sets* — frequency, then the working calendar — before what the system *produces*. The wireframe draws only the third, because that is the one worth drawing; the two inputs have no picture and are most of the decisions |
| Users & roles | `IAM-03` `IAM-04` `IAM-05` `IAM-07` | Users, permission-bundle roles, one active device per rep |
| Visit-workflow / audit builder | `A1` `ADR-0009` `VIS-03` `AUD-04` `AUD-06` `AUD-07` | The config-driven story: per-channel steps, perfect-store weights, survey forms |

## Not yet wireframed

The set now covers all core admin and field flows. Remaining screens (order review/approval,
outlet detail with custom fields, survey run-through) reuse the same shell and are left to build
time.

The **outlet create/edit form** is deliberately in that list rather than mocked. Its conventional half
is a form; its interesting half — the custom-field section rendered from the tenant's own catalogue —
is better *specified* than drawn, because what it looks like depends entirely on what a tenant
defined ([Configuration §6.1](../product/14-configuration.md#61-what-is-built-phase-1) has the five
field types and their rules).

The screen that *authors* that catalogue is a third case: never wireframed, and shipped in W5
anyway. It went unnoticed for the same reason it was easy to miss in review — the wireframe set was
drawn around what a tenant's data looks like, and this is the screen that decides what a tenant's
data *is*. Its own list is fixed: a label, a key, a type, and the one rule that type can carry.

## What Week 5 actually builds

These wireframes are **design intent, drawn against the finished product**. Several screens show data
that arrives in later weeks, and building them faithfully in
[Week 5](../delivery-plan.md#week-5--back-office-shell--admin-screens) is impossible. Recording that
here rather than silently dropping columns: the gap is a schedule, not a change of mind.

| Screen | Built in W5 | Deferred, and to when |
|---|---|---|
| Shell + nav | Full nav per the wireframe, **unbuilt destinations visibly disabled**; lands on Outlets | Dashboard **W12**, Journeys **W7** *(now built)*, Visits/audits **W9**, Products **W6** *(built)* |
| Outlets | Table with code, name, channel, segment, status, **primary territory**; filters by channel and status; create/edit incl. the dynamic custom-field form | **Frequency** (`F2 · weekly`) — journey planning, **W7**. **Field proposals** count and the `Proposed` chip — `OUT-06`/`OUT-07`, **Phase 3** |
| Outlets — import | The whole flow: upload, dry run, editable grid, apply | — |
| Outlets — custom fields | The catalogue for `Outlet`: define, edit, delete, with the type's own rule | Products, orders and visits get their own catalogues **W6+**, with the screens that render them |
| Outlets — lifecycle | Status panel below the edit form: the transitions still open, a reason, and the append-only trail | — |
| Territories | List, outlet counts, rep assignment in the detail panel | **Coverage %** — reporting, **W12**. **Channel mix** bars — computable but has no endpoint; lands with the dashboard's read side |
| Users & roles | User list, role bundles, permission toggles | **Device** column — `IAM-07`; no device concept exists in the IAM module yet |

**A screen the signed-in user may not read is hidden, not disabled.** That is a different case from
an unbuilt one and gets the opposite treatment. "Arrives in W7" is a fact about the product, worth
showing everyone, and a disabled item with a week badge says it precisely. "You may not see this" is
a fact about the caller — constant for their session, and no click will change it — so a disabled
item there is a dead control that explains nothing, which is the pattern this codebase rejects
everywhere else. The same rule governs write controls: someone who may read territories but not
write them gets the list and none of the buttons.

The permissions come from **`/api/auth/whoami`**, which re-derives them from the token the API
validated — never from decoding the token in the browser, which the realms deliberately make
impossible by keeping `permissions` off the ID token. Every endpoint still checks; hiding a control
is about not offering a door that will not open. Found by walking the Phase 1 demo, where a user who
had just been refused the user list was still being offered a **New user** button.

Three decisions taken with it:

- **The nav renders in full, with unbuilt items disabled.** A five-item nav would misrepresent the
  product; a full nav with live links to nothing would lie. A visibly disabled item does neither, and
  it shows the shape of what is coming — which is the honest version for a portfolio.
- **No tenant slug in the URL.** The wireframes show `app.fieldkit.io/veridian/dashboard`, but the
  tenant comes from the token's realm ([ADR-0008](../architecture/adr/0008-keycloak-identity.md)) and
  a slug would need a slug→realm mapping that buys nothing. Routes stay `/[locale]/…`.
- **Primary territory is a real server-side field, not a client-side join.** It needs
  `ITerritoryDirectory` — Organization's contract for the fact it owns — which lands with the outlets
  screen as its first consumer. `BR-OUT-1` already says an outlet *has* a primary territory, so this
  completes Outlets' own model rather than borrowing someone else's.

## Source

The wireframes are a single self-contained HTML file (no build step, no external assets). To edit,
update the source and re-publish to the same Artifact URL.
