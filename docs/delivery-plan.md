# Delivery Plan — week-sized work packages

> **Status:** ✅ Baseline · **Last updated:** 2026-08 · **Companion to:** [roadmap.md](roadmap.md)

The [roadmap](roadmap.md) is the phase-level view. This is the **execution view**: the build
sliced into **~1-week work packages**, each sized to a part-time solo cadence.

## Assumptions

- **Cadence:** ~2–3 h/day × 5 weekdays ≈ **12–15 h per week package**. Weekends are buffer.
- **Solo + AI-assisted** (Claude Code / Cursor, per the workflow this project showcases).
- Every package ends **committable and demoable** — no half-finished weeks.
- Packages are **sequential** (each builds on the last); a few are genuinely heavy and flagged
  **⚠︎ likely 1.5×** — treat those as ~1.5–2 weeks, not one. (W6 was added to the ⚠︎ set after
  review — it carries the whole promotion rule-set + the deterministic engine.)
- The build spans **ten modules** (the 10th, **Configuration**, was added in review — it owns the
  customization definitions); scope reflects that.
- Effort estimates are honest, not optimistic. **Nominal 15 weeks; realistic ~18–20 weeks**
  (~4.5 months) once the heavy weeks and integration friction are counted.

## Overview

| Wk | Package | Phase | Ends with (demo) |
|---|---|---|---|
| 1 | Backend skeleton & building blocks | 0 | App boots; a real module answers; arch-tests pass |
| 2 ⚠︎ | Next.js + Aspire wiring + CI | 0 | Themed Next.js shell served by Aspire; CI green |
| 3 | IAM · Keycloak · multi-tenancy | 1 | Login via Keycloak; every call tenant-scoped |
| 4 ⚠︎ | Organization · Outlets · Configuration module | 1 | Territories + outlets CRUD via API, config validation, events firing |
| 5 | Back-office shell + admin screens | 1 | **Admin models an org & loads outlets** (Phase 1 demo) |
| 6 ⚠︎ | Products & Pricing + pricing engine (C#) | 2 | Deterministic price/promo resolution, unit-tested |
| 7 ⚠︎ | Journey · Visit · TS pricing mirror | 2 | Journeys generate; visit lifecycle; C#≡TS pricing parity |
| 8 ⚠︎ | Sync engine v1 (server + client) | 2 | Offline visit pushed idempotently; no dupes on retry |
| 9 | Field PWA + offline journey/visit | 2 | **Sync → offline visits → reconcile** (Phase 2 demo) |
| 10 | Audit + config-builder backend | 3 | On-device perfect-store score == server; forms are config |
| 11 ⚠︎ | Order + offline UIs + sync v2 + photos | 3 | Offline order+audit captured, reconciled, photos uploaded |
| 12 | Dashboards + config-builder UI | 3 | **Full golden path offline → reconcile** (Phase 3 demo) |
| 13 | Observability + security hardening | 4 | Domain metrics in dashboard; isolation tests green |
| 14 | E2E + seed data + polish | 4 | Playwright golden path (online + offline) green in CI |
| 15 | Deploy to ACA + case-study | 4 | **Clickable live demo**; README sells it |

---

## Phase 0 — Foundation

### Week 1 · Backend skeleton & building blocks
**Goal:** turn the weather-forecast scaffold into the real module-hosting skeleton. Shipped as
several small PRs.
- **[✓ PR #2]** Solution layout + `SharedKernel` (`Money`, `GeoPoint`, `Result`, `IClock`, `TenantId`)
  + `BuildingBlocks` (pure abstractions) + `ArchTests`; **AT-7** enforced at compile time via the
  banned-API analyzer ([module boundaries §5](architecture/10-module-boundaries.md#5-enforcement--architecture-tests)).
- **[✓ this slice]** `Infrastructure`: EF base `ModuleDbContext` with `HasDefaultSchema`
  (schema-per-module), the tenant query filter + `EntityStampingInterceptor` (tenant + audit),
  `TenantId` value converter; **PostgreSQL added to the AppHost**; verified on real Postgres
  (Testcontainers) — schema, stamping, and tenant isolation ([ADR-0005](architecture/adr/0005-postgres-schema-per-module.md)).
- **[✓ messaging slice]** Transactional **outbox** + in-process dispatch: `AggregateRoot` events →
  per-module `outbox_message` (same-tx interceptor) → `OutboxProcessor` claims (`FOR UPDATE SKIP
  LOCKED`) and delivers idempotently ([ADR-0006](architecture/adr/0006-in-process-messaging-and-outbox.md)); verified on real Postgres.
- **[✓ module-hosting slice]** `IModule` self-registration (`FieldKit.Web`) + first real module
  (`Catalog`, `POST/GET /api/products`) replacing `WeatherForecast` — renamed `Products` before W6,
  since it already held that module's route and permissions; end-to-end verified with
  `WebApplicationFactory<Program>` on real Postgres. **The modular monolith runs.**
- **[✓ migrations slice]** Per-module **EF migrations** (`ModuleMigrator`, per-schema history) replacing `EnsureCreated`.
- **[✓ W2]** Front end **Vite → Next.js** (App Router) + AppHost re-wired (`AddJavaScriptApp`, standalone output).
- **[✓ W2]** **shadcn/ui + Tailwind v4 + FieldKit design tokens** (teal, light/dark; Button/Card/Badge) — the wireframe toolkit is real.
- **[✓ W2]** **next-intl** scaffold — `en` + `ro`, always-prefixed locale routing, locale switcher,
  localized 404, catalog-parity test ([ADR-0010](architecture/adr/0010-internationalization.md)).
- **[✓ W2]** **PWA baseline** (`OFF-10`) — per-locale manifest, icons, and a Workbox app-shell
  service worker with per-locale offline fallback; built post-`next build` to keep Turbopack
  ([ADR-0004](architecture/adr/0004-nextjs-offline-first-frontend.md#building-the-service-worker-phase-0)).
- **[next]** Per-tenant **row-version stamping** (the `IReferenceChangeFeed` primitive) — with the sync slices.

**Done when:** `dotnet run --project FieldKit.AppHost` boots the app + Postgres; one module answers
`/api/…`; arch-tests pass. *(Bus/outbox/row-version and the first module are the remaining W1 slices.)*

### Week 2 · Next.js + Aspire wiring + CI ⚠︎
**Goal:** the front-end platform, themed and installable, plus the identity container and CI.
- ✓ Remove Vite; scaffold **Next.js (App Router)** in `/frontend`; re-wire Aspire JS hosting (standalone output) ([ADR-0004](architecture/adr/0004-nextjs-offline-first-frontend.md)).
- ✓ **shadcn/ui + Tailwind + design tokens** (light/dark, teal) — reproduce the [wireframe](ux/README.md) shell.
  Light/dark resolves from the device preference ([A7](product/decisions-and-assumptions.md#a7--ui-toolkit-shadcnui--tailwind)).
- ✓ **next-intl** scaffold (EN + RO), locale routing ([ADR-0010](architecture/adr/0010-internationalization.md)).
- ✓ **PWA baseline**: per-locale manifest, Workbox service worker (app-shell cache + offline
  fallback), installability.
- ✓ Add **Keycloak** container to the AppHost; JWT bearer validation skeleton in the API ([ADR-0008](architecture/adr/0008-authentication-and-multitenancy.md)).
  Dev realm imported from source; tokens validated against it in integration tests on a real Keycloak.
- ✓ **GitHub Actions CI**: build (dotnet + next) → unit tests → arch-tests, both jobs required in
  branch protection.

**Done when:** the themed Next.js shell is served through Aspire, installs as a PWA, and CI is green. **⚠︎ Heavy** (front-end migration + PWA + Keycloak) — budget ~1.5 weeks.
*(All of W2 has landed, and it went past the "skeleton" in the bullet above: `ITenantContext` is
token-derived and business endpoints are permission-checked, which is the substance of `IAM-02` and
`IAM-05`. W3 still owns the IAM module itself — users, roles, realm provisioning.)*

---

## Phase 1 — Admin core

### Week 3 · IAM · Keycloak · multi-tenancy
**Goal:** know who the user is and which tenant they may touch — everywhere.
- IAM: `User`, `Role`, permission model, `Tenant`; permission-catalog contribution ([IAM spec](product/10-identity-and-access.md)).
- **Keycloak realm-per-tenant**; OIDC auth-code+PKCE login in Next.js; token → `ITenantContext` + permissions ([ADR-0008](architecture/adr/0008-authentication-and-multitenancy.md)).
  ✓ **Multi-issuer validation** — issuer and signing keys resolved per request from the tenant table,
  and a token's `tenant` claim bound to the tenant that owns its issuer. A second dev realm exists so
  those are testable: with one realm they pass whether resolution is per-request or hard-coded.
  ✓ **Sign-in in Next.js** — auth-code + PKCE against the realm of the workspace the user names,
  tokens held on-device so a session survives going offline, and the API called same-origin with the
  bearer. `IAM-01` complete.
- **Multi-tenancy**: global query filter + insert stamping; arch-test banning `IgnoreQueryFilters` / raw bypass.
- ✓ Users & roles CRUD (backend); ✓ tenant seed — a seeded tenant starts with the system role
  templates (`IAM-06`), so it has someone who can administer it rather than permissions nobody holds.
  **Roles** (`IAM-04`) + the **permission catalogue** they validate against — each module declares
  the permissions it owns, so a role naming one nothing enforces is rejected rather than stored.
  **Users** (`IAM-03`) — profile, roles, deactivate/reactivate; deactivation publishes
  `UserDeactivated` through the outbox. **Profile only**: creating the *Keycloak account* is
  `IAM-10` (Phase 2), because doing it here means Keycloak admin credentials in the request path.

**Done when:** login works; API is tenant-scoped; a crafted `tenantId` cannot cross tenants (test). `IAM-01…05`.

### Week 4 · Organization · Outlets · Configuration module ⚠︎
**Goal:** the master data the field app needs — and the customization mechanism. **⚠︎ Heavy** — this
week absorbed the new **Configuration module** (finding S5) on top of Organization + Outlets; budget
~1.5 weeks.
- Organization: `OrgUnit`, `Territory`, rep assignment; `ITerritoryDirectory`/`IRepScope`; `RepAssignmentChanged` ([Org spec](product/11-organization-and-territory.md)) — `ORG-01…06`.
  ✓ **Org units** (`ORG-01`) — configurable-depth hierarchy, sibling-unique names, moves refused when
  they would put a unit inside its own branch. The module's public contracts land with the slices
  that give them a consumer.
  ✓ **Positions + management line** (`ORG-02`) — users attached to units through IAM's
  `IUserDirectory`; the line derived upward for roll-up and the visibility scope downward for
  BR-ORG-4. Current state, not history: `ORG-08` is Phase 2, and a visit keeps its author regardless.
  ✓ **Territories + outlet membership** (`ORG-03`, `ORG-05`) — territories hang off an org unit so
  BR-ORG-4 is computable, and single-primary is a unique index rather than a rule on every write
  path. Outlets are validated and labelled through `IOutletCatalog`, which lands here because this is
  its first consumer.
  ✓ **Rep assignments + `RepAssignmentChanged`** (`ORG-04`) — inclusive date ranges with overlaps
  rejected (BR-ORG-2), "current" resolved in the caller's timezone, and every change published
  through the outbox naming both the incoming and outgoing rep. Interval logic lives in a
  `DateRange` value object in SharedKernel with its own tests, not in an endpoint.
- Outlets: `Outlet` with channel/segment/geo/contacts, lifecycle; `IOutletCatalog`/`IOutletClassification`; events ([Outlets spec](product/12-outlets-master-data.md)) — `OUT-01…04`.
  ✓ **Classification + lifecycle** — channels as tenant-owned reference data, outlets carrying
  code/name/channel/segment/banner, and the Active → Inactive → Closed lifecycle with `Closed`
  terminal (`OUT-04`). Every transition is recorded append-only with a reason, so BR-OUT-4's
  "retains history" holds even after later edits overwrite the outlet's own audit stamps.
  ✓ **Location + contacts** — structured address, optional coordinates always validated against the
  shared `GeoPoint`, a required IANA time zone checked against the runtime, and contacts as personal data
  ([B8](product/decisions-and-assumptions.md#b8--privacy--gdpr-posture)) replaced wholesale so
  erasure is an empty list. BR-OUT-2's "required to participate in journeys" lands with Journey (W7),
  where participation is defined.
- **Configuration module (10th):** field-definition catalog + `IFieldDefinitionCatalog` + JSONB values + server validation, wired into Outlets first ([ADR-0009](architecture/adr/0009-config-driven-customization.md)). (Workflow/survey/weight definitions land in W10.)
  ✓ **Field definitions + custom-field validation** (`CFG-01`, `CFG-02`, `OUT-02`) — five types
  (text/number/boolean/date/choice), the catalogue reached through `IFieldDefinitionCatalog` so
  Outlets never learns what a tenant's fields mean, and values in typed JSONB validated
  server-side (BR-CFG-3). Current definitions only: `BR-CFG-1`'s retention serves as-of-capture
  validation, which needs the sync protocol that arrives with `CFG-06`/`CFG-07`
  ([spec §6.1](product/14-configuration.md#61-what-is-built-phase-1)).
- Bulk-import / seed outlets.
  ✓ **Bulk import** (`OUT-05`) — CSV as the request body, with the content type choosing the reader
  so JSON and Excel are later readers rather than later endpoints. Held to the same rules as
  `POST /api/outlets`, plus the one thing a typeless format needs: coercion driven by the tenant's
  field definitions (`CFG-01`). `AllOrNothing` or `Partial` at the admin's choice, both atomic, with
  the rejected rows handed back as a file to fix and re-send. The **import screen** lands in W5 and
  the **demo seed data** in W14 — this is the mechanism both of them use
  ([spec §6.1–6.2](product/12-outlets-master-data.md#61-import-formats-still-to-come)).

**Done when:** territories + outlets CRUD via API; custom fields validate; events land in the outbox.

### Week 5 · Back-office shell + admin screens
**Goal:** the Phase 1 demo — a usable back office.
- Back-office route group + shell (nav per wireframes), auth guard, TanStack Query, i18n.
  ✓ **`ITerritoryDirectory`** (`ORG-05`) — Organization's contract for which territory covers an
  outlet, landing with the outlets screen as its first consumer. Makes Org and Outlets the first
  module pair referencing each other's contracts, which is safe at build time and gated at runtime by
  the new **AT-10** ([why](architecture/10-module-boundaries.md#two-modules-may-point-at-each-other)).
  ✓ **Back-office shell** — route group, client-side auth guard, TanStack Query, and the nav from the
  wireframes with **unbuilt destinations visibly disabled and labelled with the week that ships
  them**. Sign-in lands on Outlets. Proven by a read-only **outlets table**: a token minted by a
  tenant's realm, validated by the API, a tenant-scoped query, and a territory resolved across a
  module boundary, all arriving in one screen. Filters, create/edit and the import screen are the
  slices after it.
- **Outlets** screen (table, filters, create/edit incl. custom-field form) + **import screen** —
  upload, dry-run, fix the flagged cells in an **editable grid**, apply (`OUT-05`). Correcting before
  the write rather than after is the point; the rejected-rows download stays as the escape hatch.
  ✓ **Paged, searchable, sortable table** (`OUT-01`) — offset paging with a total, search over code
  and name, filters by channel and status, and sortable headers. **The query lives in the URL**, per
  the client-state decision in [ADR-0004](architecture/adr/0004-nextjs-offline-first-frontend.md#state-three-kinds-and-only-one-library):
  a filtered view is shareable, survives a reload, and is what a colleague can be sent. Create/edit
  and the import screen are what remain.
  ✓ **Create / edit, with the tenant's own fields** (`OUT-01`, `OUT-02`) — one form for both, code
  set once and read-only after. The custom-field section is rendered from the tenant's catalogue
  (`CFG-01`), and its **Zod schema is generated from the same descriptor** rather than written by
  hand — so the client cannot drift from the rules `BR-CFG-3` says the server owns. React Hook Form
  carries it, which is what puts each message beside the control that caused it.
  ✓ **Contacts** (`OUT-01`) — a field array, and the fix for a form that deleted every contact on
  every outlet it saved: contacts are replaced wholesale, so a client omitting them from a `PUT` is
  a client erasing them. The API now validates them too, in front of the write rather than at the
  column widths.
  ✓ **Import screen — upload, check, apply** (`OUT-05`) — the dry run costs nothing and returns
  exactly what the real run would, so Apply stays unavailable until Check has run. The row cap and
  the readable formats come from **`GET /api/outlets/import`** rather than a copy in the front end,
  because a drifted copy fails silently.
  ✓ **The editable grid** (`OUT-05`) — the file comes back as a table with the flagged cells editable
  and a checkbox per row; fix what can be fixed, uncheck what cannot, check again, apply. Any change
  makes Apply unavailable until it has been re-checked, so the file that was checked is exactly the
  file that is written. A dry run hands the file back **as the server read it**,
  so the screen corrects rows the server numbered rather than parsing the upload a second time: two
  CSV readers disagreeing about which row is row 7 would flag a cell in the wrong shop, with no
  symptom until someone corrected data that was fine
  ([spec §6.2](product/12-outlets-master-data.md#62-the-import-screen-week-5)).
- **Territories** screen (list + rep assignment).
  ✓ **The list** (`ORG-03`) — name, org unit and outlet count, filtered by org unit through the URL,
  with create, rename and delete. The count is the server's; counting it here would mean fetching
  every membership of every territory to render a column of numbers. Deleting a territory that still
  holds outlets is refused rather than cascaded, and the screen shows the server's own sentence —
  those outlets are a rep's offline scope (`BR-ORG-3`), so a cascade is a set of shops vanishing from
  a device tomorrow morning.
  ✓ **Rep assignment** (`ORG-04`) — the detail panel the wireframe draws, opened from the territory
  name and held in the URL like every other view on this screen. A **history**, not a current
  holder: `BR-ORG-2` allows one rep at a time, so several rows mean the periods do not overlap. An
  end date is optional and means *until further notice*, and `isCurrent` is the server's answer in
  the caller's own timezone rather than the browser's — the two disagree for anyone travelling.
  Overlap stays the server's rule: two people can be editing the same territory, so a client-side
  check is a guess about a set it does not own.
- **Users & roles** screen (list + role permission toggles).
  ✓ **The user list** (`IAM-03`) — name, email, the roles each holds *named* rather than counted,
  and whether the account is on. Create and edit, with roles as checkboxes because `BR-IAM-3` says
  the answer cannot end up empty and a multi-select where ctrl-click silently drops the rest is the
  wrong control for that. Deactivation is its own verb, not a field on the profile update: it
  publishes `UserDeactivated` so Sync releases the bound device (`A8`), and a consequence that size
  should not be reachable by an unrelated edit to somebody's timezone. Deactivated accounts stay on
  the list — "why can't Ana log in" is answered there or nowhere. The **subject id** is typed in and
  read-only after creation, which is honest about `IAM-10` not existing yet; the **Device** column
  stays deferred to `IAM-07`.
  ✓ **Roles and their permission toggles** (`IAM-04`) — on the same page below the users, because
  they are one decision taken from two directions: an admin arrives asking "what may Ana do", and
  the answer is a role. Every permission the system enforces is a checkbox with **its description
  beside it**, grouped by resource — the catalogue is code, so the form cannot offer a grant nothing
  checks, and `outlet:write` is a shape rather than a capability until something says what it does.
  A built-in template can be **recomposed but not deleted**: it is the way back to a working set
  (`IAM-06`), and the refusal explains that rather than the button quietly not being there.

- **Sales hierarchy** (`ORG-01`) — above the territories on the same page, because a territory
  hangs off an org unit and a workspace with no hierarchy cannot have one. **Not in the original W5
  list**, and its absence was found by walking the demo: every screen existed, the nav was complete,
  and a fresh tenant still could not create its first territory. Depth and labels are the tenant's,
  so it renders as an indented tree rather than fixed columns for levels that may not exist. A unit
  is not offered its own subtree as a parent — the API refuses the cycle, but unlike a name collision
  this is never what somebody meant.

- **Outlet ↔ territory membership** (`ORG-05`) — in the territory detail panel, and **not** on the
  outlet form: membership is Organization's fact, and having Outlets write it for convenience is what
  module boundaries exist to prevent. The outlet list *reads* it through `ITerritoryDirectory`; this
  is the only thing that writes it. The second gap the demo walk found — the Territory column had
  read `Unassigned` for every outlet since it was built, because nothing could populate it, which
  also meant every rep assignment covered nothing.

- **Trade channels** (`OUT-01`) — behind `/outlets/channels`, linked from the outlet header.
  **Not in the original W5 list**, and the third gap the demo walk found: every outlet needs a channel
  (`BR-OUT-1`), the endpoints existed since W4, and nothing called them — so a fresh tenant could not
  create its first outlet, and the only channels on the dev database were left by integration tests.
  Its own route rather than a section on the outlet list, because it is set up once and rarely
  revisited. `channel:write` is separate from `outlet:write` on purpose — it is the permission the
  importer pointedly lacks, so a typo in one cell cannot mint a permanent classification.

- **Outlet custom fields** (`CFG-01`) — behind `/outlets/custom-fields`, linked from the outlet
  header. **Not in the original W5 list**, and the fourth instance of the same pattern, found by the
  pre-Phase-2 audit rather than by the demo walk: the outlet form has rendered a tenant's own fields
  since W4 and the import validates against them, but nothing could put anything *in* the catalogue.
  Every definition on the dev database existed because an integration test had made one — so the
  module that carries the product's "highly customizable per tenant" claim shipped read-only.
  The **key is derived from the label** and fixed after creation, because it is the JSONB property
  name already written into every row. Deleting a definition is **confirmed**, unlike deleting a
  channel: a channel still in use is refused by the API with a count, while this cannot be — the
  values live in another module's rows (ADR-0005) and stay there, undescribed, until each outlet is
  next saved and then vanish.

- **Outlet lifecycle** (`OUT-04`) — a panel below the edit form, **outside it**. The API gave status
  its own endpoint so that closing a shop could not ride along on an unrelated edit, and a control
  inside the form would hand that back: one Save, two decisions. The fifth API-with-no-caller the
  audit found — the outlet table has shown a Status column since W5 that could only ever read
  `Active`, and the append-only trail behind it was written by integration tests and read by nobody.
  `Closed` is terminal, so a closed outlet is offered **no control at all** rather than a select the
  API will refuse; what it gets instead is the spec's own guidance, that a location which trades
  again is a new outlet with its own code. The trail is readable by anyone with `outlet:read` —
  "why can't I order for this shop" is answered there or nowhere.

**Done when:** an admin logs in, models a small org, and loads outlets — matches the [wireframes](ux/README.md), all tenant-scoped. **▶ Phase 1 demo.**

---

## Phase 2 — Field ops + sync engine

### Week 6 · Products & Pricing + pricing engine (C#) ⚠︎
**Goal:** the commercial engine, deterministic. **⚠︎ Heavy** — the most rules-dense module (all
promo types + tax + the resolver + a decimal-parity vector suite) in one week; budget ~1.5 weeks.
- Products, categories, UoM/pack, tax class; Assortment + MSL; PriceList + prices; Promotion types ([Products spec](product/13-products-and-pricing.md)) — `PRD-01/02/03/05`.
- **Pricing engine (C#)**: `ResolvePrice(outlet,product,qty,date)` — specificity + promo priority + tax + `Money`; pure, unit-tested with vectors — `PRD-04/06/07`, `BR-PRD-7`.
- `IProductCatalog`/`IAssortmentService`/`IPricingService`; events. Back-office Products & pricing screen.

**Done when:** pricing resolves deterministically; the vector suite passes.

#### Decomposition

A week is many PRs ([pull-requests §2](engineering/pull-requests.md)), and this one is the heaviest
in the plan. Sliced below in stacking order — contracts/schema → domain → application → API → UI.
Sizes are hand-written diff estimates against the ~400-line budget; generated migrations are excluded.

| # | Slice | Requirements | ~Size |
|---|---|---|---|
| 0 | **Refusal codes** — `FieldProblem` grows `Code`/`Args` ([ADR-0012](architecture/adr/0012-server-message-localization.md) stage 1) | — | 170 |
| 1 | **Classification vocabulary** — `Category` (hierarchical), `Brand`, `TaxClass` | `PRD-01` | 350 |
| 2 | **Product grows up** — brand, category, UoM, pack size, tax class, status, custom fields | `PRD-01` | 400 |
| 3 | **Assortment + MSL** — channel assortment, MSL flags, per-outlet overrides; `IAssortmentService` | `PRD-02`, `BR-PRD-4` | 400 |
| 4 | **Price lists** — currency + effective window, product prices, channel/outlet assignment; publishes `PriceListPublished` | `PRD-03`, `BR-PRD-1` | 400 |
| 5 | **`IOutletClassification`** — Outlets grows the contract slice 6 needs (see below) | — | 120 |
| 6 | **Price resolution** — specificity + effective date, as a pure function; decides the [vector format](../vectors/README.md) (see below) | `PRD-04`, `BR-PRD-2`, `BR-PRD-7` | 350 |
| 7 | **Promotion authoring** — %-off and fixed-amount (7a), volume/tiered (7b), BOGO/bundle (7c), scope + `PromotionActivated` (7d) | `PRD-05` | 400 ×4 |
| 8 | **Promotion resolution** — priority selection, validity window in the outlet's timezone; second [vector file](../vectors/README.md) | `PRD-06`, `BR-PRD-3/6` | 350 |
| 9 | **Tax** — tax class × tenant/country, on the rounded net line; third [vector file](../vectors/README.md); `IOutletClassification` grows `CountryCode` | `PRD-07`, `BR-PRD-5/9` | 250 |
| 10 | **Parity vector suite** — a deterministic generator, committed artifacts checked against it, and the properties that test C# where generated vectors cannot | `PRD-08`, `BR-PRD-8/9` | 350 |
| 11 | **Product catalogue screen** — `/products`, list + create/edit; nav item goes live | `PRD-01` | 400 |
| 12 | **Classification screen** — `/products/classification`, all three vocabularies on one page | `PRD-01` | 400 |
| 13 | **Assortment screen** — `/products/assortments`, channel assortment + MSL | `PRD-02` | 400 |
| 14 | **Per-outlet overrides** — `/outlets/[id]/assortment`, one shop's departures from its channel | `PRD-02` | 400 |
| 15 | **Price list screens** — `/products/price-lists` and its prices; money stays a string end to end | `PRD-03`, `BR-PRD-8` | 400 |
| 16 | **Price list scope** — `/products/price-lists/[id]/scope`, channels ticked and outlets searched; saving withdraws as readily as it publishes | `PRD-03` | 300 |
| 17 | **Promotions screen** — `/products/promotions`, all four `B1` types in one form; type and currency fixed after creation | `PRD-05` | 400 |
| 18 | **Promotion targets** — `/products/promotions/[id]/targets`, categories with their ancestry and products by name; an empty set takes a deal out of play | `PRD-05` | 300 |
| 19 | **Promotion tiers** — `/products/promotions/[id]/tiers`, one kind for the whole ladder; offered on the one type that has tiers | `PRD-05` | 350 |
| 20 | **Promotion scope** — channels and outlets, as a price list's scope already is | `PRD-05` | — |

**Slice 5 is a prerequisite, and it is deliberately late.** `BR-PRD-2` resolves a price by *outlet
override → channel → default*, so the engine must map an outlet to its channel. `Outlet.ChannelId`
exists but is internal to Outlets, and [AT-1](architecture/10-module-boundaries.md#5-enforcement--architecture-tests)
forbids Products reading it; `IOutletCatalog.OutletSummary` carries no channel. So Outlets must grow
a public contract — **channel only**, nothing else, designed against the resolver as its actual
caller. That placement is the registry's own discipline: `IOutletCatalog` and `ITerritoryDirectory`
both waited for a real consumer rather than being guessed at up front
([module boundaries §7](architecture/10-module-boundaries.md#7-module-registry)).

**The vector format is a W7 contract, not a slice-10 detail.** W7's TypeScript mirror consumes the
same file, so the format is decided in **slice 6**, with the first engine code — not invented at
slice 10, where it would be shaped by whatever C# found convenient to emit.

> **Landed as [`vectors/`](../vectors/README.md)** with slice 6, hand-written, one case per rule.
> Invented at slice 10 against emitted output, the format would have recorded whatever C# happened
> to do; decided against a real engine, it had to state rules a second language could implement.
>
> The tiebreak between two equally-specific lists is the example, and it took two attempts to get
> the *reasoning* right even though the code was right the first time. Slice 6 replaced
> `Guid.CompareTo` with an explicit big-endian byte comparison, justified by the claim that .NET
> compares a Guid's first field as a *signed* int. **That claim was false** — .NET compares it
> unsigned, so `CompareTo` agrees with byte order — and the "hostile" id pair chosen to catch it
> discriminated nothing. Slice 10's mutation testing found it: reverting the resolver to
> `Guid.CompareTo` broke no test. The real trap is `Guid.ToByteArray()` with no argument, which is
> little-endian for the first three groups and orders `00000100-…` below `00000002-…`; the vectors
> now carry a pair that catches *that*. The implementation never changed — specifying the ordering
> in a form TypeScript can implement was right for a reason that survived the correction.

**A promotion's scope is its own slice, as a price list's was.** 7a authors the rule — type, value,
window, priority, and what it targets; where it reaches (channels and outlets) and the
`PromotionActivated` event follow, exactly as `PriceListAssignment` followed `PriceList`. The
intermediate state is honest rather than awkward: a promotion that exists and discounts nobody is
what a draft *is*, and splitting there keeps the aggregate's invariants and its reach reviewable
separately.

> **`PRD-05` took three PRs, not the two budgeted above.** The estimate treated "the two remaining
> types" as one slice because both were *not-flat*; in fact they are not-flat in different ways.
> Volume/tiered moves the discount onto rows keyed by quantity, which changes what a promotion's own
> value columns mean and forces every value path to become optional. BOGO changes what the promotion
> *does* — it gives something away rather than reducing a price — and needs a second subject. Sharing
> a PR would have meant ~900 hand-written lines and two unrelated rule sets reviewed at once. The
> estimate was wrong about the shape of the work rather than its size, which is the more useful thing
> to record.
>
> **7d is the scope slice, and it is a prerequisite for slice 8 rather than a tidy-up.** Promotion
> resolution has to answer "which promotions reach this outlet" before it can pick one, exactly as
> price resolution needed `PriceListAssignment` before it could resolve a price. `PromotionActivated`
> — the event the [module registry](architecture/10-module-boundaries.md#7-module-registry) names —
> belongs here for the same reason `PriceListPublished` belongs on assignment: reach is the moment a
> rule starts affecting what a rep sees.
>
> **7c completed the four types**, and closed something 7a opened. The `promotion` check constraint was
> written as a `CASE` per type ending in `ELSE TRUE` — deliberate room, so each arriving type was a
> new `WHEN` rather than an `ALTER` reasoned about against rows already stored. The cost, flagged at
> the time, was that any unrecognised `type` string was stored unconstrained. B1 names exactly four
> types and all four are now constrained, so the clause becomes `ELSE FALSE` and the escape closes
> with it — scaffolding removed by the last slice it was built for, rather than left standing.

**`IPricingService` is not built by slice 6, deliberately.** The resolver is a pure static function
and the endpoint is its only caller; a cross-module contract needs a cross-module consumer, which is
Order (Phase 3). Same reasoning that keeps `IAssortmentService` and `Products.Contracts` unbuilt, and
the same discipline the registry applies to `IRepScope` and `IOrgHierarchy`
([module boundaries §7](architecture/10-module-boundaries.md#7-module-registry)).

**`Money` crosses the wire as a string** amount + currency (`BR-PRD-8`,
[api-contracts §1](architecture/13-api-contracts.md#1-shape--conventions)), which constrains every
DTO from slice 4 onward. Retrofitting it later is a breaking API change.

**Refusals carry codes from slice 1, which is what slice 0 is for**
([ADR-0012](architecture/adr/0012-server-message-localization.md)). This module is refusal-heavy — no
price list for this currency, product not in assortment, promotion outside its window — and writing
them as English prose now would mean migrating a module's worth of messages a month later. Deciding
that before W6 rather than during it was the point; the envelope has to grow `Code`/`Args` before
anything can use them, which is a prerequisite this decomposition initially failed to sequence.

**Not in W6:** `IReferenceChangeFeed` for Products, which lands with the W8 sync slices for the same
reason row-version stamping does — a primitive designed against a protocol that does not exist yet is
a guess. `PRD-09` (localized product names) and `PRD-10` (off-assortment ordering) are Phase 4
*Coulds* and stay there.

The `📝 ASSUMPTION` answers in the [spec's open questions](product/13-products-and-pricing.md#10-open-questions)
are taken as settled: no promotion stacking beyond one line-level plus order-level, volume tiers
per-line, currency per price list rather than per tenant.

### Week 7 · Journey · Visit · TS pricing mirror ⚠︎
**Goal:** the field domain (online) + the cross-language pricing guarantee.
- Journey: frequency config, working calendar, generation; `IJourneyQuery` ([Journey spec](product/20-journey-planning.md)) — `JRN-01…06`.
- Visit: lifecycle, check-in/geofence, config-driven steps, seal-on-checkout; `IVisitContext` ([Visit spec](product/21-visit-execution.md)) — `VIS-01…07`.
- **Pricing engine TS mirror** on **`decimal.js` + the documented rounding policy** (never native `number`); a **generated/property-based** parity harness running the *same* vectors on C# and TS ([BR-PRD-8/9](product/13-products-and-pricing.md#decimal-parity-resolves-finding-s4)).

**Done when:** journeys generate; visit lifecycle works online; C# and TS pricing agree on every vector. **⚠︎ Heavy** — budget ~1.5 weeks.

### Week 8 · Sync engine v1 (server + client) ⚠︎
**Goal:** the hard part — offline round-trip for reference + visits.
- Server: row-version change tracking; `/sync/pull` territory-scoped delta (watermarks + tombstones); `/sync/push` idempotent (mutationId ledger, Redis + persisted); device registry (bind / one-active) ([sync engine](architecture/12-offline-sync-engine.md), [ADR-0007](architecture/adr/0007-offline-sync-strategy.md)).
- Client: IndexedDB (Dexie) stores (`ref_*`, `outbox`, `meta`); sync manager (push→pull); watermark handling.
- Wire visits through push; outlets/journeys/products/prices through pull.

**Done when:** a device binds, pulls a snapshot, creates a visit **offline**, and pushes it idempotently — replaying the batch changes nothing. Idempotency + resume tests pass. **⚠︎ Heaviest week — budget ~2 weeks.**

### Week 9 · Field PWA + offline journey/visit
**Goal:** the Phase 2 demo — the field app, offline.
- Field route group (mobile-first, shadcn); **Today's Journey** + **Visit** screens reading the local store (per [wireframes](ux/README.md)).
- Service-worker offline shell; connectivity indicator + outbox/pending UI + **Sync now**.

**Done when:** sync a journey → go offline → complete visits → reconnect → back office sees the results. **▶ Phase 2 demo.**

---

## Phase 3 — In-store depth

### Week 10 · Audit + config-builder backend
**Goal:** structured shelf capture and the scoring model.
- Audit: MSL availability, facings + **total-category facings** → share-of-shelf, price check, survey answers, photo refs; **weighted perfect-store score** with a **decimal-parity C#≡TS engine + generated vectors** (BR-AUD-5/12, same regime as pricing), weight-version as-of-capture ([Audit spec](product/22-merchandising-and-audits.md), [A2](product/decisions-and-assumptions.md#a2--audit--perfect-store-structured-checks--share-of-shelf--photo)) — `AUD-01…07, 12`.
- Config-builder backend in the **Configuration module**: visit-workflow + survey + weight definitions as snapshot-versioned reference config ([ADR-0009](architecture/adr/0009-config-driven-customization.md)).

**Done when:** the on-device score equals the server's recomputation; workflow/forms are configuration.

### Week 11 · Order + offline UIs + sync v2 + photos ⚠︎
**Goal:** close the golden path, offline, with binaries.
- Order: aggregate, lines, on-device pricing/promos, minimum, submit/lock, **rejected→re-open-editable** (`IOrderIngest`, BR-ORD-9) ([Order spec](product/23-order-capture.md)) — `ORD-01…07, 12`.
- Offline **Audit** + **Order capture** screens (per wireframes), fully offline.
- Sync v2: transactional conflict rules (append-only, snapshot-version flagging, as-of-capture validation); **device-swap drain-push** + local-store migration; **photo out-of-band upload** (presign + retry) ([B5](product/decisions-and-assumptions.md#b5--photo--binary-sync), [B7](product/decisions-and-assumptions.md#b7--conflict-resolution-matrix)).

**Done when:** an offline audit + order are captured, submitted, and reconciled; photos upload independently. **⚠︎ Heavy** — budget ~1.5 weeks.

### Week 12 · Dashboards + config-builder UI
**Goal:** the Phase 3 demo — the full loop, both sides.
- Supervisor **dashboard** (coverage, strike rate, perfect-store, order value) from module query contracts ([reporting](product/00-product-overview.md#reporting--kpis-cross-cutting-read-side)).
- **Config-driven builder UI** (workflow steps, perfect-store weight sliders, survey questions) — the customization showcase (per [wireframes](ux/README.md)).

**Done when:** the dashboard reflects field activity; editing a workflow/weights/form flows to the field app on next sync. **▶ Phase 3 demo — full golden path.**

---

## Phase 4 — Production polish

### Week 13 · Observability + security hardening
- Custom OTel metrics (sync push latency/size, outbox backlog, visits/orders, pricing duration) + dashboards; extended health checks ([observability](architecture/15-observability.md)).
- Rate limiting (`/sync`, auth), security headers, CORS, secrets; verify tenant-isolation tests + the bypass ban; threat-model pass ([security](architecture/16-security.md)).

**Done when:** domain metrics visible; security checklist + isolation tests green.

### Week 14 · E2E + seed data + polish
- Playwright E2E: the golden path **online and offline** (network toggling) + a tenant-isolation E2E ([testing strategy](architecture/17-testing-strategy.md)).
- Polished seed/demo data (believable Veridian tenant) for a live demo — loaded **through the bulk
  import** (`OUT-05`), so the demo data proves the path a customer would actually use.
- UI polish: accessibility, empty/error states, i18n coverage.

**Done when:** E2E green in CI; demo data loads; the app feels finished.

### Week 15 · Deploy to ACA + case-study
- `aspire deploy` → **Azure Container Apps**; managed Postgres/Redis/Blob + Keycloak; scale-to-zero; OTLP export ([ADR-0011](architecture/adr/0011-deployment-azure-container-apps.md)).
- CI/CD: build → test → arch-test → image → deploy.
- README case-study polish: screenshots/GIFs from the **real** app; live-demo link.

**Done when:** a clickable live demo exists and the README sells it.

---

## Sequencing & risk notes

- **Critical path** runs through the sync engine (W8). It's the highest-risk, highest-value
  package — protect its buffer; don't let W6/W7 slip into it.
- **The four ⚠︎ weeks** (W2, W7, W8, W11) carry most of the schedule risk. If time is tight,
  they are where a second week gets spent — plan for it rather than compressing them.
- **Demo-driven checkpoints** at W5, W9, W12, W15 are natural places to stop, record a GIF, and
  bank a portfolio-ready milestone even if later phases slip.
- **First portfolio-viable cut** is end of **W9** (offline field round-trip). Everything after
  deepens the story; the architecture and offline claims are already *demonstrated* by then.
- Custom-fields and i18n plumbing (W2/W4) are easy to under-scope — they touch many screens
  later; doing them early is deliberate, not gold-plating.

## How to track

Each package is a milestone; its bullets are the issues/tasks. Check items against the spec IDs
they cite so scope stays honest. Update the roadmap's phase checkboxes as packages land.
