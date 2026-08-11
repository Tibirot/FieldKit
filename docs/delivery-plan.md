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
| 9 ⚠︎ | Field PWA + offline journey/visit | 2 | **Sync → offline visits → reconcile** (Phase 2 demo) |
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

> **✅ W6 has landed**, in 26 PRs against the 24 the decomposition below budgets (21 rows, one of them
> ×4), plus a handful of untagged fixes found while checking the screens in a browser. The
> engine resolves prices, promotions and tax as pure functions checked by hand-written and generated
> [vectors](../vectors/README.md) plus property tests, and the back office can author every one of its
> inputs: catalogue, classification, assortments and per-outlet overrides, price lists with their
> prices and scope, promotions with their targets, tiers and scope, and tax rates per class and
> country. The last slice closed the loop end to end — a promotion authored, targeted and scoped
> through the UI resolves for a real outlet.
>
> > **"The back office is complete" was written here one slice early.** Tax *rates* had no screen —
> > `PRD-07`'s engine, endpoints and vectors were all done, and the only way to give a tax class a
> > rate was the `.http` file, so a tenant could file a product under a class and never tax it. Found
> > by walking the app before W7 rather than by any test, because nothing was broken: every screen
> > that existed worked. The row for it is slice 21 below, added after the fact rather than
> > backdated.
>
> The overrun was the promotion children being three screens rather than one row's worth — targets,
> tiers and scope are three different questions — on top of `PRD-05` needing three type slices rather
> than two (see below). Neither was a surprise about *size*; both were about shape, which is the part
> an estimate is worst at.

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
| 20 | **Promotion scope** — channels and outlets; the outlet picker extracted rather than copied from a price list's scope | `PRD-05` | 300 |
| 21 | **Tax rates, and the refusal codes nobody was reading** — a rate editor per tax class, ADR-0012 stage 2, and the docs W6 falsified | `PRD-07`, `BR-PRD-9` | 700 |

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

#### Decomposition

Two new modules and a second implementation of an engine that already exists. Sliced below in
stacking order; as in W6, the prerequisite contracts come first and are designed against their actual
callers rather than guessed at.

**The TS mirror is independent of Journey and Visit** — it consumes [`vectors/`](../vectors/README.md)
and nothing else in this week. It is listed last for readability, not sequenced last; it can be built
alongside, and it is the slice that most rewards being started early, because a parity failure is a
question about the *spec* and those take a while to settle.

| # | Slice | Requirements | ~Size |
|---|---|---|---|
| 0 | **`IRepScope`** — Organization grows the contract journey generation needs: which outlets a rep covers on a given day | — | 150 |
| 1 | **Journey module + call frequency** — new assembly and `journey` schema; visits-per-cycle and cycle length, per outlet or derived from segment | `JRN-01` | 400 |
| 2 | **Working calendar** — the rep's working days, holidays and daily capacity | `JRN-02` | 300 |
| 3 | **Generation, as a pure function** — frequency × territory × calendar → planned visits; capacity respected, closed outlets excluded | `JRN-03`, `BR-JRN-1/3/5` | 400 |
| 4 | **Publish + `IJourneyQuery` + `JourneyPublished`** — a generated plan becomes a thing others can read | `JRN-04` | 350 |
| 5 | **Rep-side annotations** — not-visited with reason, unplanned visit, reschedule within cycle; `PlannedVisitMarkedNotVisited` | `JRN-06`, `BR-JRN-2/4` | 400 |
| 6 | **`IVisitWorkflow`** — Configuration grows the per-channel step sequence, and whether presence is expected | `VIS-03` | 250 |
| 7 | **Visit module + check-in** — new assembly and `visit` schema; geo capture, geofence check, override reason | `VIS-01/02`, `BR-VIS-2` | 400 |
| 8 | **Steps and mandatory gating** — the workflow instantiated per visit; check-out refused while a mandatory step is open | `VIS-03/04`, `BR-VIS-3` | 400 |
| 9 | **Check-out and seal** — outcome, time-on-site, check-out geo-stamp, sealed thereafter; `VisitCompleted` | `VIS-05`, `BR-VIS-3/4/5` | 400 |
| 9b | **`IJourneyQuery`** — Journey's `.Contracts` split, and check-in validating the planned call it claims to fulfil | `JRN-04` | 300 |
| 10a | **The call frequency screen** — segment defaults and per-outlet overrides; the Journeys nav item goes live | `JRN-01` | 400 |
| 10b | **The working calendar screen** — a rep's days and daily capacity, and the tenant's holidays | `JRN-02` | 400 |
| 10c | **Generate, publish, review** — the week grid, the shortfalls, and the plan a rep is given | `JRN-03/04` | 400 |
| 11 | **TS `Money` and the rounding policy** — `decimal.js`, never a native `number`; half-up away from zero | `BR-PRD-8/9` | 300 |
| 12 | **TS price resolution** — the mirror of `PriceResolver`, against `pricing/price-resolution.v1.json` | `PRD-04` | 300 |
| 13 | **TS promotion resolution** — the mirror of `PromotionResolver`, including the tiebreak | `PRD-06` | 350 |
| 14 | **TS tax** — the mirror of `TaxEngine`, on the rounded net line | `PRD-07` | 250 |
| 15 | **The parity harness in CI** — both languages, the same vectors, one job that fails on either | `PRD-08` | 300 |

**Slice 0 is W6's slice 5 again, and the shape is worth reusing.** `BR-JRN-1` generates only for
outlets in the rep's **active territory**. `ITerritoryDirectory` is built and answers what a territory
contains; nothing answers *which territories this rep covers today*, which is `IRepScope` — planned in
the [registry](architecture/10-module-boundaries.md#7-module-registry) since W1 and still unbuilt,
because until now no module asked. The generator is its first real caller, so it gets designed against
the generator and nothing else.

> **Slice 4 shipped the publish half and left `IJourneyQuery` where its consumer is.** The row below
> assigns both to it, on the reasoning that Visit consumes the contract *inside this week* — but when
> slice 4 was built Visit still did not exist, and designing the interface then would have been the
> guess this plan spends the next paragraph warning against. `JourneyPublished` shipped anyway, and
> the split is the useful distinction: **an event is a statement about something that happened and is
> true whether or not anyone is listening** (`PriceListPublished` has been emitted into an empty room
> since W6), while **an interface is a promise to a caller**, and a promise made before the caller
> arrives is one they have to live with. `IJourneyQuery` lands with slice 7, shaped by what Visit
> asks for.
>
> **It did not, and the reason is worth more than the prediction was.** Slice 7 built check-in and
> asked Journey *nothing*. A visit carries the id of the planned call it fulfils and needs no other
> fact about the plan: the rep's device already has the round, and check-in's questions are all about
> where the rep is standing. So `PlannedVisitId` is a nullable, unvalidated `Guid` — deliberately not
> a foreign key, because the plan lives in Journey's schema (`AT-1`), and deliberately unchecked,
> because there is nothing yet that would notice a wrong one. **`IJourneyQuery` moves to slice 9**,
> where `VisitCompleted` carries the planned id out to reporting and a fabricated one would start
> counting as coverage — that is the first moment anybody genuinely asks Journey a question, and the
> question ("is this planned call this rep's, at this outlet?") is a better interface than the one
> slice 4 or slice 7 would have guessed. Journey's `.Contracts` assembly splits then, not now.

**Journey gets a `.Contracts` assembly; Visit does not, yet.** Visit consumes `IJourneyQuery`, so
Journey has a real cross-module consumer inside this week and splits accordingly. Visit's own
contracts — `IVisitContext`, `IVisitQuery` — are consumed by Audit and Order, which are Phase 3. They
stay unbuilt for the same reason `IPricingService` did through all of W6: a contract designed before
its consumer asks is a guess the consumer then has to live with. `IJourneyIngest` and `IVisitIngest`
are Sync's, and land with W8.

**Generation is a pure function before it is an endpoint**, exactly as `PriceResolver` was. It is the
most rule-dense thing in the week — frequency, capacity, territory, working days, exclusions — and it
is the one part a supervisor will argue with, so it has to be testable without a database.

**Day sequencing is not in this week, and the generator has to say so.** `F2` step 3 sequences a day's
outlets by a proximity/segment heuristic, which reads like part of generation — but it is `JRN-09`, a
*Should* at **Phase 3**, while everything else here is a Phase 2 *Must*. So generation emits a stable,
arbitrary order (by outlet code) and the heuristic replaces it later. Worth stating rather than
leaving implicit, because "the order looks wrong" is otherwise a bug report against a slice that never
claimed to order anything.

> **Slice 8 built the steps and half of `BR-VIS-3`, which is all the rule there is until check-out
> exists.** The visit answers *which mandatory steps are still open* — at check-in and on every
> response that returns a visit — and slice 9 turns that answer into a refusal. Splitting it this way
> was not a compromise: a rep who only learns at the door that the visit cannot end is a rep walking
> back into the shop, so "what is outstanding" has to be a running answer rather than a check-out
> verdict, and building it that way first is what makes the refusal a formality.
>
> The steps are a **copy** of the channel workflow taken at check-in, not a live read — the one
> design decision in the slice that could have gone either way, and the reason it did not is that an
> admin editing a workflow at eleven would otherwise change what a rep who checked in at ten is
> required to do. Storing `VisitStepType` in Visit's schema does put Configuration's vocabulary in
> another module's table, which is the honest cost: renaming a member there is a data migration here,
> and it is noted where the column is configured.

**Check-in and check-out are separate slices because they fail differently.** Check-in is about
whether a rep may start work somewhere they may not be standing — and `BR-VIS-2` says *never block,
always record*, which is a rule that reads as a bug until it is written down. Check-out is about
whether a visit is finished and, once it is, that it can never change again (`BR-VIS-4`). Reviewing
"we let them in" and "we sealed it shut" in one diff would flatten two opposite instincts into one.

> **`BR-VIS-2`'s assumption needs slice 7 to exist.** Remote-capable visit types skip the override,
> because a phone call is legitimately not at the outlet and demanding a reason records an exception
> where nothing exceptional happened. That is a per-channel policy, so it lives on `IVisitWorkflow` —
> which is why Configuration grows the contract *before* check-in is built rather than after, and why
> slice 7 is a prerequisite rather than a tidy-up.

> **Slice 7 also needed a contract this plan did not budget: `IOutletGeofence`.** A geofence check
> needs the outlet's coordinates and its radius, and `IOutletCatalog` — designed in W5 against
> territories — deliberately exposes neither. Growing it to would have handed every existing caller
> a location it has no use for, and put `OUT-08`'s per-outlet radius on an interface the back office
> reads. So Outlets got a second small contract instead, on the same test the registry applies
> throughout: it answers one question one caller asks. The **default radius (150 m) is a constant on
> the contract**, not a number Visit knows, so the day `OUT-08` makes it per-outlet the query behind
> it changes and check-in does not. That is one contract more than the row's 400 lines assumed, and
> `IJourneyQuery` moving to slice 9 is roughly what paid for it.

> **Slice 9 shipped check-out, the seal and `VisitCompleted` — and left two of the row's words
> unbuilt on purpose.** The "reference snapshot version" (`BR-VIS-6`) has nothing to record yet: a
> version is what a device synced *against*, and Sync mints one in **W8**. A client-supplied string
> stored in the meantime would be a column nothing writes and nothing reads, which is the sort of
> field that later gets trusted. The half of `BR-VIS-6` that check-out actually depends on — the
> step snapshot — landed in slice 8. `IJourneyQuery` also stayed out: check-out asks Journey nothing
> either, so the promised move from slice 4 to slice 9 has become a move to **slice 9b**, where
> validating a planned-visit id is the whole purpose rather than a passenger.
>
> `TimeOnSite` is derived from the two timestamps rather than stored, and the event does not carry
> it: a computed duplicate is a second answer that can disagree with the first. Nothing flags an
> abnormally short visit, because `BR-VIS-5` says those are reporting facts and never blocks — and
> the threshold that would decide "abnormal" is a `VIS-10` decision against a population this system
> does not have.

> **Slice 9b built `IJourneyQuery`, and it closed a hole rather than adding a feature.** Check-in had
> carried a `plannedVisitId` on trust since slice 7: nothing would have noticed a fabricated one until
> it reached a coverage report, where it reads as a call that was made. The interface answers the
> whole question in one call — this rep's, at this shop, on a published plan — because a lookup plus
> two comparisons in the caller is a rule every future caller has to remember, and one of them would
> forget the rep.
>
> It also found a **second hand-maintained gate list**: `ModuleBoundaryTests.ModuleAssemblies`, which
> `AT-10` walks, was still five modules while `AT-1` gated seven. Journey and Visit had been outside
> the cycle check since they were built, and it went unnoticed because neither had a contract
> implementation to walk — the slice that gave Journey one is the slice that made it matter. The
> duplication itself was removed straight after, as **`AT-11`**: one list, everything derived from it,
> and a test comparing it against `FieldKit.slnx` so a module cannot be added and left ungated.

> **Slice 10 is three screens, not two.** The row budgeted "400 ×2" for frequency, calendar,
> generate-and-publish and plan review together, and that was one screen's worth of estimate short:
> the first two are what a supervisor *sets* and the third is what the system *produces*, and each
> has its own refusals, its own permission story and its own empty state. Splitting them keeps each
> PR reviewable and keeps the nav honest at every step — 10a points Journeys at frequencies because
> that is the first journey screen that exists, and 10c moves it to the plan.
>
> The wireframe draws only the plan grid, which is the right thing to draw and also why the estimate
> came up short: the two screens with no picture are where most of the decisions are.

> **W7 landed, and the parity harness is real** (slice 15). `parity (C# ↔ TypeScript vectors)` runs
> both engines against `vectors/` and fails on either — plus the one assertion neither suite can make
> about itself: that both languages read the same set of files. Two engines agreeing once is an
> anecdote; a job that goes red when they stop is the guarantee this week was for.
>
> It also closed a gap the mirror found: the generated tax file had been **EUR-only**, so every one of
> its 252 cases asked a two-decimal currency and an implementation hard-coding 2 passed all of them.
> It now sweeps four currencies — no minor unit (JPY), two (EUR, RON) and three (KWD) — at 448 cases,
> and the expectations are printed at each currency's own scale rather than at a two-decimal minimum
> that would have written `"1.00"` for a yen.

**The parity harness is the week's actual deliverable.** `PRD-08` is why the vectors were written
against a real engine in W6 rather than emitted from one — the format had to state rules a second
language could implement. Slices 11–15 are that claim being tested for the first time, and slice 15 is
the only one that makes the guarantee permanent: two implementations agreeing once is an anecdote, and
a CI job that fails when they stop agreeing is the guarantee.

> The numbers in the sentence above said "12–16" until the week was built, which is what an estimate
> written before the work looks like afterwards. There is no slice 16; the mirror needed a money type
> first (slice 11), and that turned out to be the right order rather than an extra step — every rule
> in the vector files is arithmetic on amounts.

**Two of the week's requirements have no row of their own, on purpose.** `VIS-07` (not-visited
handling) is `JRN-06` seen from the other side — the reason is captured against the *planned* visit,
so it lands in slice 5 and Visit never grows a state for a visit that did not happen. `VIS-06` (notes
and photos as steps) is a *Should*: notes fall out of slice 8 as an ordinary step type, and photos
need the upload path, which is **W11**.

**Not in W7:** `JRN-05` (the rep works the journey) and the on-device visit UI are **W9**, because
they are the offline PWA and there is no sync engine until W8. `JRN-07/08/09/10` and `VIS-08/09/10`
are *Should*/*Could* at Phase 3 and stay there. `IReferenceChangeFeed` for Journey lands with W8, for
the same reason Products' does.

> **Seventeen PRs against a ~1.5-week budget** (16 rows, one of them ×2). W6 was budgeted at 24 the
> same way and took 26. Recorded rather than adjusted, because the estimate is the plan's own claim
> and rewriting it after the fact would hide what a decomposition is for: two new modules, two
> prerequisite contracts and a second implementation of a rules engine is closer to two weeks than one
> and a half, and it is better to say so before starting than to explain it afterwards.

### Week 8 · Sync engine v1 (server + client) ⚠︎
**Goal:** the hard part — offline round-trip for reference + visits.
- Server: row-version change tracking; `/sync/pull` territory-scoped delta (watermarks + tombstones); `/sync/push` idempotent (mutationId ledger, **Postgres** — see below); device registry (bind / one-active) ([sync engine](architecture/12-offline-sync-engine.md), [ADR-0007](architecture/adr/0007-offline-sync-strategy.md)).
- Client: IndexedDB (Dexie) stores (`ref_*`, `outbox`, `meta`); sync manager (push→pull); watermark handling.
- Wire visits through push; outlets/journeys/products/prices through pull.

**Done when:** a device binds, pulls a snapshot, creates a visit **offline**, and pushes it idempotently — replaying the batch changes nothing. Idempotency + resume tests pass. **⚠︎ Heaviest week — budget ~2 weeks.**

#### Decomposition

**Thin end-to-end first.** Week one takes *one* entity through pull and *one* mutation through push,
both ends, so the protocol meets a real consumer while it is still cheap to change. Week two widens
it. The alternative — the whole server surface, then the client — stacks more cleanly and is how a
protocol reaches week two with a design nobody has used.

Two things W7 already paid for land here: [`IRepScope`](architecture/10-module-boundaries.md#7-module-registry)
is exactly the territory scoping pull needs, and `IReferenceChangeFeed` was deferred out of W6 on the
grounds that "a primitive designed against a protocol that does not exist yet is a guess" — this is
the protocol.

**Week one — the round trip**

| # | Slice | Requirements | ~Size |
|---|---|---|---|
| 0 | **The change sequence** — a monotonic, per-tenant stamp applied on save by an interceptor. Every delta below reads it; nothing else in the week works without it | `OFF-03` | 300 |
| 1 | **Tombstones** — a delete becomes an observable event. Without one, a delta pull can add and update forever and never *remove*, and a device keeps outlets it lost months ago | `OFF-03` | 250 |
| 2 | **Device registry** — bind, one active per user, unbind; an unbound device is refused before any scoping question is asked | `OFF-12` | 350 |
| 3 | **`/sync/pull`, outlets only** — watermark in; changes, tombstones and the next watermark out; territory-scoped through `IRepScope`. One entity, so the protocol is argued about once | `OFF-03` | 400 |
| 4 | **The idempotency ledger** — a Postgres table, unique on `(tenant, device, mutationId)`. A replay returns the first result rather than doing the work again | `OFF-04` | 300 |
| 5 | **`/sync/push`, visits only** — a batch applied idempotently, a per-mutation result, refusals in the [ADR-0012](architecture/adr/0012-server-message-localization.md) code shape | `OFF-04`, `OFF-09` | 400 |
| ~~4~~ | *Folded into 5.* The ledger's table and mapping shipped alone; **nothing could test it.** A ledger is only observably a ledger through the endpoint that consults it, and a test that resolved `IMutationLedger` from the container never got as far as a tenant — `KeycloakTenantContext` throws without a request. A slice whose only evidence is "it compiles" is not a slice | — | — |
| 6 | **Client: the local store** — Dexie `ref_*`, `outbox` and `meta`; watermarks persisted where a crash cannot lose them | `OFF-02` | 350 |
| | *Shipped with three things the spec's table left open: one database per tenant+subject, no `acked` status, and `watermarks` as its own store. `blobs` is not built — photo upload is W11, and a store with no writer is a schema version spent on nothing* | | |
| 7 | **Client: the sync manager** — push, then pull, then reconcile; the round trip the week is judged on | `OFF-01`, `OFF-06` | 400 |
| | *Shipped single-flight, a batch of 100 under the server's 200, a lost batch returned to `pending` rather than left in flight, and a four-way interruption taxonomy. The round trip is proven against a mocked API, not a live one — a real device pulling from a real server is W9's demo* | | |

**Week two — widening and hardening**

| # | Slice | Requirements | ~Size |
|---|---|---|---|
| 8 | **Pull across the reference set** — journeys, products, prices, promotions, configuration. Mostly slice 3 applied five times; the interesting part is what each one scopes by | `OFF-03` | 400 |
| 8a | **Journeys** — scoped by the rep the *plan* names, not by territory. No baseline half and no tombstones, and both absences are statements about the domain rather than gaps | `OFF-03` | 400 |
| 8b | **Configuration (visit workflows)** — scoped by *nothing*, because the cheapest correct scope is sometimes no scope. The first feed whose tombstones are both produced and sendable, and the first whose payload is an aggregate rather than a row | `OFF-03` | 400 |
| 8c | **The product catalogue** — scoped by nothing again, and for a second reason: a rep has to be able to *name* what is on the shelf, so a catalogue narrowed to the assortment gives a blank where a name should be. Holding a product is not permission to sell it | `OFF-03` | 400 |
| 8d | **The assortment** — one rule in two halves with two scopes: the channel list by nothing, the per-outlet overrides by the device's own outlet set. The first entity to reuse that scope, and the first besides outlets to need a baseline | `OFF-03` | 400 |
| 8e | **Prices** — lists, lines and assignments. The assignment half reuses 8d's shape; the lists are the one place a device holds data outside its territory, recorded as a limitation rather than defended | `OFF-03` | 400 |
| 8f | **Promotions** — the last reference entity. Each travels whole, targets and tiers inside, which found a real bug: the endpoints that set them never touched the root, so the change never reached a device | `OFF-03` | 400 |
| 9 | **Replay and resume as properties** — a generated suite: any batch replayed changes nothing, any pull interrupted at any point resumes without loss or duplication | `OFF-04` | 350 |
| | *Two suites, not one: the server answers identically and resumes from any cursor; the client — the real manager against a model server — converges. Fixed sweeps rather than seeded randomness, per the position W6 took. It also disproved slice 3a’s “not self-healing” note* | | |
| 10 | **Partial failure** — one bad mutation in a batch does not reject the batch; the device learns which, and why | `OFF-09` | 300 |
| 11 | **Local-store migration** — an app update must not strand a pending outbox | `OFF-13` | 300 |
| | *Version 2 is a real index the drain needed, not a placeholder: `pending()` sorted in memory at the top of every push. The suite writes a v1 database and opens it with v2 code — and fails on an upgrade that clears the outbox, which is the check that makes it worth having* | | |
| 12 | **Drain-push on device swap** — a deactivated device completes its last push rather than losing a shift's work | `OFF-12` | 300 |
| 13 | **Connectivity + pending UI** — the indicator, per-item badges, and *Sync now* | `OFF-05`, `OFF-06` | 350 |
| | *The pending count is the fact and connectivity is the explanation — no green tick, because `navigator.onLine` is true on a captive portal. Counts are live off Dexie, so they move on capture as well as on drain. **Week 8 complete.*** | | |

**Not in W8:** photo upload (`OFF-08`) and background sync (`OFF-07`) — both are W11 and Phase 3
respectively, and both are separate transports rather than more of this one. Audit and order offline
(`OFF-01b`) arrive with those modules.

**The ledger is Postgres, not Redis.** This line previously read "Redis + persisted", and the hot
cache is dropped: a dedupe lookup is one indexed read on a database that is already deployed, already
backed up, and already paid for, while a Redis container app is ~$11/month against a demo whose whole
bill is ~$16–21 ([ADR-0011](architecture/adr/0011-deployment-azure-container-apps.md#costing-and-the-backing-service-split-2026-08)).
It buys latency this system has no way to demonstrate. Reversible by design — putting a cache in
front of the ledger later changes one class, and [ADR-0007](architecture/adr/0007-offline-sync-strategy.md)
records what would justify it.

> **Slices 10 and 12 arrived with slice 5, because the endpoint could not be written without them.**
> Partial failure is not a hardening pass over a batch-or-nothing push — it is the shape of the
> response, and building the all-or-nothing version first would have meant designing `PushResponse`
> twice. Drain-push is the same: `/sync/push` had to answer "may an inactive device push?" the moment
> it looked a device up, and the answer (`Swapped` may, `Compromised` may not) is one branch, not a
> slice. What week two still owes on both is *generated* evidence — slice 9's replay/resume property
> suite over arbitrary batches, rather than the worked examples slice 5 shipped.

### Week 9 · Field PWA + offline journey/visit ⚠︎
**Goal:** the Phase 2 demo — the field app, offline.
- Field route group (mobile-first, shadcn); **Today's Journey** + **Visit** screens reading the local store (per [wireframes](ux/README.md)).
- Service-worker offline shell; connectivity indicator + outbox/pending UI + **Sync now**.

**Done when:** sync a journey → go offline → complete visits → reconnect → back office sees the results. **▶ Phase 2 demo.** **⚠︎ Budget ~2 weeks — see the decomposition for why this is not a week of screens.**

#### Decomposition

**This is not a week of screens.** It reads like one — two of the thirteen slices below are a screen
and nothing else — and taking it that way is how it overruns. Three things W9 has to build have no
precedent anywhere in the codebase, and each is a slice before any screen can be written.

**1. The first entity the device *authors*.** Every store the local database has is a copy of
something the server still holds: lose it and the next pull rebuilds it. The outbox is the exception
and it holds *opaque payloads* — one write, then never touched again except to mark its fate. A visit
in progress is neither. It is created on check-in, mutated several times as a rep works the steps,
and only becomes an outbox mutation when it is sealed at check-out. It is the first thing on the
device that a rep would actually lose, which makes it the first store where "durable" (`OFF-02`) has
teeth.

**2. A second implementation of a server rule, on the device, that the server then trusts.**
[`Geofencing.Assess`](../FieldKit.Modules.Visit/Geofencing.cs) is pure — two positions, a radius and
a presence policy — exactly like `PriceResolver`, and it has to run on the device because a rep
standing in a shop with no signal still has to be told whether they are inside the fence.
`CapturedVisit` then carries the device's verdict and **the server stores it unmodified, deliberately**
([`IVisitIngest`](../FieldKit.Modules.Visit.Contracts/IVisitIngest.cs): re-judging yesterday's visit
against today's radius would reclassify a rep who was legitimately inside it). So a device that
disagrees with the server writes a permanently wrong record that nothing will ever correct. That is
the pricing-parity argument again, with a worse failure mode — money can be recomputed; "was the rep
there" cannot. It gets the same regime: a TS mirror, generated vectors, and the
[parity job](../.github/workflows/ci.yml) that already exists.

**3. The radius has to travel, and today it does not.** `OutletSnapshot` carries latitude and
longitude and no radius; the server reads it from `IOutletGeofence.DefaultRadiusMetres`. A TypeScript
constant of `150` would agree with the server exactly until `OUT-08` makes the radius per-outlet, and
then disagree silently on the one dimension nobody re-checks — which is the drift the parity harness
exists to prevent, arriving through the one value the harness cannot see. It goes on the snapshot.

**What W8 already paid for.** `SyncProvider`, `SyncIndicator` and `SyncBadge` shipped in W8 slice 13
with **no mount point** — built, tested, and rendered by nothing. Slice 1 is that mount point, and it
comes before every screen for that reason: until a layout owns a database and a bound device, no
screen below can read anything. The readers the screens need (`plannedVisits`, `workflowFor`,
`outlet`, `assortmentFor`, `priceOf`, `promotionsFor`) all exist and are unused.

**Week one — a device that can work a day**

| # | Slice | Requirements | ~Size |
|---|---|---|---|
| 0 | **The visit's provenance** — `Source` on `Visit`, set on both paths, and `CreatedAtUtc` exposed as `recordedAtUtc`. Server-side, client-independent, and **first because it is the one thing here that cannot be added later**: every visit ingested before it lacks the data permanently. See below | `VIS-05`, `OFF-04` | 250 |
| | *Shipped as **one** column, not two. The planned `RecordedAtUtc` already existed: `EntityStampingInterceptor` has stamped `CreatedAtUtc` from `IClock` on every entity, on both write paths, since W1 — so adding it would have been a second answer to the same question, which is what `TimeOnSite` is derived to avoid. It is exposed under the domain name and left in place. That also disposes of the backfill problem for half the slice: the timestamp is already right on every visit ever stored, and only `Source` is nullable* | | |
| 1 | **The field shell** — a mobile-first `(field)` route group whose layout opens the rep's database, binds a device on first run, and mounts `SyncProvider`. Includes the rebind screen the indicator's `deviceRejected` state already points at and nothing implements | `OFF-05`, `OFF-06`, `OFF-12` | 400 |
| | *Mounting W8's components found two of their states unreachable. Only runs the rep **pressed a button for** reported an outcome, so a device rejected during a reconnect sync left the app looking healthy — the manager now takes an observer. And nothing synced when the app **opened**: `online` fires when connectivity changes, not when a rep starts their morning on a working connection. The session guard came out of `BackOfficeShell` on the way, so an expired session in the field asks the same question the back office asks rather than dumping a rep to `/login` mid-visit* | | |
| 2 | **The radius travels** — `OutletSnapshot.RadiusMetres`, from `IOutletGeofence`. Per-outlet on the wire though constant in the source, so `OUT-08` is a server change and not a protocol one. Local-store version 4 | `OFF-03`, `VIS-01` | 250 |
| | *Version 4 repeats version 3's upgrade verbatim, and that is the point: Dexie does not replay a version a database has already seen, so editing 3 would strand every device already on it holding outlets with no radius. The server-side test asserts against `IOutletGeofence.DefaultRadiusMetres` rather than `150` — a literal would pass on the day `OUT-08` changes the constant and the feed forgets to follow, which is the drift sending the value exists to prevent* | | |
| 3 | **Geofencing in TypeScript** — a mirror of `Geofencing.Assess`, with generated vectors through the existing parity job. The device's verdict is the record; there is no second opinion | `VIS-01`, `VIS-02` | 400 |
| | *The first shared rule that is not decimal arithmetic, so it needed a comparison rule of its own: distances within a micron, verdicts exactly, and a generator that drops any case within a millimetre of the radius — a verdict on the boundary is one two correct engines may legitimately split on. Mutation-probing the new tests found a vector case that claimed to exercise the antipodal clamp and did not: `sqrt` of `1 + 2^-52` rounds back to exactly 1, so the clamp never fires. The case and the comments were corrected rather than the code* | | |
| 4 | **The local visit** — the first device-authored store: created on check-in, mutated through the steps, sealed at check-out, and only then enqueued as one `CapturedVisit`. Local-store version 5 | `OFF-01`, `OFF-02` | 450 |
| | *Two transactions carry the slice, and each has one test that fails without it: sealing and queueing together, so a device killed between them cannot leave a finished visit nothing will send; and step completion, because a read-modify-write of the `steps` array loses one of two quick taps. `LocalVisit` is shaped as `CapturedVisit` so sealing is a projection rather than a translation — a field the type lacks is a compile error rather than a thinner record. The device re-runs the server's rules because offline nobody else will* | | |
| 5 | **Today's Journey** — the day's stops from `plannedVisits`, ordered, each with its status and its sync badge. The first screen, and the one the demo opens on | `JRN-05`, `OFF-01`, `OFF-05` | 400 |
| | *Three joins the screen does not make for itself: which visit answers which call (the one naming it, unless one is still open), what a stop says when the plan and the device disagree (the device's work wins — an annotation is stale until the next pull), and what happens to a call whose shop this device no longer holds (it stays, last, named as a gap). Ordering is alphabetical **by decision** — a plan assigns calls to days and nothing sequences a day, so a route would be a spec rather than a `sort`. The date heading found a real bug: the app formats in UTC, so a `Date` built at local midnight rendered the day before* | | |
| 6 | **Check-in** — the geofence assessed on the device, the override reason when outside, the presence policy read from the pulled workflow rather than assumed | `VIS-01`, `VIS-02` | 400 |
| | *The rule the screen is built around is **the verdict shown is the verdict stored**: the fix is taken once when the screen opens, and the assessment the rep reads is the one written to the visit. Re-measuring at the tap sounds more accurate and is worse — a rep shown *inside* and recorded *outside* has been given a reason box they never saw. `maximumAge: 0` is the other half: a cached fix is the previous shop's car park, and the geofence would agree with it. Two states needed splitting from one — "locating" from "no fix", or every rep sees the override box flash; and "still reading" from "not held", or a shop this device does not have waits forever. A refusal code cannot be a message key, because next-intl reads `visit.checkIn.x` as three levels of nesting and prints the key at the rep* | | |

**Week two — the visit, and the proof**

| # | Slice | Requirements | ~Size |
|---|---|---|---|
| 7 | **Steps** — the sequence rendered from the pulled configuration, not from a hard-coded list. Note steps carry their text; a step nobody has built a control for renders as a labelled no-op rather than breaking the visit | `VIS-03`, `VIS-06` | 400 |
| | *Rendered from the **visit**, not from Configuration — the copy check-in took, which is `BR-VIS-6`'s snapshot rule doing its job and also why the screen needs no signal. The no-op decision is load-bearing and costs something worth naming: a ticked `Audit` step records that the rep did an audit and carries none of its numbers. The alternative is worse — a mandatory step nobody can complete is, by `BR-VIS-3`, a rep who cannot check out, so the visit would be broken by a feature not being finished yet. Two navigation edges came with it: check-in now opens the visit it started, and a stop the rep is standing in links to that visit rather than back to check-in — without which a rep who navigated away had no way back in, and the check-in screen would correctly refuse to start a second one and then have nothing to offer. A step type this device has never heard of is named generically rather than dropped: a device is routinely older than the server, and a blank mandatory row is the same dead end* | | |
| 8 | **Check-out** — `BR-VIS-3`'s mandatory gating enforced *on the device*, outcome and reason, then seal → one outbox mutation. The second rule the device has to agree with the server about, and the one that decides whether a rep gets out of the shop | `VIS-04`, `VIS-05` | 400 |
| | *What is outstanding is on screen the **whole time**, not only when the rep tries to leave — being told at the door is the version of `BR-VIS-3` that sends someone back into a shop they have walked out of. The button stays live anyway: a rep who taps gets the names, which is more use than a dead control with no explanation. The position is taken **at the tap**, which is the opposite of check-in, and the difference is that nothing here is shown before it is stored — check-in must honour a verdict it displayed, this only records where the phone was when the visit ended. Five seconds and then `null`: `BR-VIS-3` is the only thing allowed to keep a rep in a shop, and a satellite is not. Time on site is derived on the device exactly as it is server-side (`BR-VIS-5`) — a stored copy is a second answer that can disagree with the first. Adding the panel broke nine of slice 7's tests, all for one reason: `<Visit>` now needs a router* | | |
| 9 | **Not visited, from the device** — the annotation W7 built server-side, reachable with no signal. The **second mutation type** through `/sync/push`, which is what turns `PushedMutation.Type` from a field into a discriminator | `VIS-07`, `OFF-04` | 350 |
| | *Shipped **larger than planned, by decision**: `IJourneyIngest` was built with all three annotation kinds — not-visited, reschedule and unplanned — rather than the one this slice's UI needs. Reschedule and unplanned therefore ship with no device caller, so their integration tests carry the whole weight. **The local store is never written**, and that is the slice's real design choice: writing "not visited" into `ref_planned_visits` would look right until the server refused the mutation, and then be wrong forever — a refused annotation changes no row version, so the next pull sends nothing to correct it. The outbox is the record, and the round overlays it. **Idempotency differs by kind**: re-marking finds the state it wants and keeps the first reason, a reschedule to the same day is a no-op, and only the unplanned call needed a guard because it creates a row. Two bugs surfaced during it — the "which plan covers this day" query used `SingleOrDefault` on a wrong theory that a rep's published plans do not overlap (they do; a regenerated round is how a plan is corrected), and the unplanned path hit the same EF client-key gotcha the HTTP path had already solved, presenting as a 500 rather than a refusal* | | |
| 10 | **Visit summary** — the recap before check-out. `Should`, and the first candidate to slip | `VIS-09` | 250 |
| | *Did not slip, and came in under budget because it earns its place by **not** repeating the screen above it. "Recap before check-out" reads as an interstitial between the button and the seal; that taxes every visit of every day with a tap to catch a mistake on a few, so it sits inline above the outcome instead, where the rep is already looking. Three of its four facts are otherwise unavailable: **optional steps left undone** (`BR-VIS-3` gates on mandatory ones, so nothing else mentions these — and they are the only thing on the screen a rep can still act on), time in the shop **while the visit is open**, and that check-out is irreversible. `minutesOnSite` moved to `lib/visits/summary.ts` because it was about to be derived in a third place, and the copy on the sealed record answered **zero for every open visit** — it read `checkedOutAtUtc ?? checkedInAtUtc`, which was correct for the only caller it had. The recap's labels collide with the step list's, which broke four of slice 7's assertions; the fix was to give the step list an `aria-label`, since three unnamed lists on one screen is a real screen-reader problem the tests happened to notice first* | | |
| 11 | **The offline shell, actually offline** — the service worker built in W5 caches a shell nothing navigates to. Field routes precached, `/offline` wired as the fallback, install prompt, and `requestPersistentStorage` finally *called* | `OFF-10`, `OFF-11` | 300 |
| 12 | **The round trip against a live server** — the client stack (`db`, `manager`, `reference`) driven against a real API rather than a mocked one, and the Phase 2 demo recorded | `OFF-01`, `OFF-04` | 350 |

**Not in W9:** audit and order capture (`OFF-01b`, W10/W11) — the two step types that make a visit
*valuable* rather than merely complete, and both are their own modules. Photo upload (`OFF-08`) and
background sync (`OFF-07`) are separate transports, W11 and Phase 3. `VIS-08` (signature) is `Could`.

**Two decisions W9 inherits from nobody — and slice 0 is where they are made.**

**Whose clock is a visit stamped with?** `CapturedVisit` carries the device's `checkedInAtUtc` and
`checkedOutAtUtc`, the server stores them as sent, and nothing records when the visit *arrived*. A
visit ingested three days late is therefore indistinguishable from one that happened at the time it
claims — and time-on-site, the measure `VIS-05` exists to produce, is computed entirely from two
numbers a device controls.

**Is an ingested visit marked as one?** `Visit` has no source discriminator, so a supervisor reviewing
a day cannot tell a visit worked live in the back office from one drained off a phone.

**Both are answered by slice 0, together, because they are one question.** The visit records
**`RecordedAtUtc`** — when *this server* first stored it, from `IClock`, on both the live and the
ingested path — and **`Source`** (`Live` | `Device`).

> **Built, and one column smaller than planned.** `RecordedAtUtc` turned out to exist already:
> `EntityStampingInterceptor` has written `CreatedAtUtc` from `IClock` on insert, on every entity and
> both write paths, since W1. A new column would have duplicated it exactly — the "second answer that
> can disagree with the first" `TimeOnSite` is derived to avoid — so the value is exposed under the
> domain name (`recordedAtUtc`) and left where it was. `createdAtUtc` would be the honest name for an
> audit field and the wrong one on the wire: on a visit, "created" reads as check-in, and the whole
> point is the case where the two are days apart. Only `Source` is new, and only `Source` is
> nullable.

Neither touches the rep's own claim:
`checkedInAtUtc` and `checkedOutAtUtc` stay exactly as sent, which is the property
[`IVisitIngest`](../FieldKit.Modules.Visit.Contracts/IVisitIngest.cs) argues for and this does not
revisit. What they add is a second, independent timestamp beside it, and three things fall out of
having both:

- **"Captured offline" becomes visible** rather than inferred. `RecordedAtUtc − CheckedOutAtUtc` is
  the drain lag; `Source` says whether the gap means anything.
- **Clock skew is detectable.** A device visit claiming a check-out later than the moment the server
  received it is claiming the future, which no correct device does. Nothing acts on that in W9 —
  it is a signal, not a rule — but it cannot be recovered later if it is not recorded now.
- **`Source` is not derivable from `RecordedAtUtc` alone.** A device that drains the moment a rep
  leaves the shop looks exactly like a live visit, so the discriminator has to be stored rather than
  computed.

**Why it is slice 0 rather than part of slice 4.** Everything else in W9 can be added to a running
system afterwards; `Source` cannot. Every visit ingested before it exists lacks it permanently, and
there is nothing to backfill from — which is exactly why it is nullable rather than defaulted, and
why `null` is documented as "recorded before this was tracked" instead of being quietly filled with
`Live`. Today that is a handful of demo rows. After the Phase 2 demo it is the demo's whole dataset,
and after W11 it is orders too. It is also entirely server-side, so it neither blocks nor is blocked
by any client slice.

*(The `RecordedAtUtc` half of the original argument turned out to be already solved — see the note
above. The urgency was real for the half that was not.)*

**What this deliberately does not attempt.** Two device timestamps remain the only witness to an
offline visit, and `RecordedAtUtc` does not make time-on-site trustworthy — it makes the *trust
visible*. Defending it properly would mean re-deriving a rep's day from data the server does not
have, which is the same answer `BR-VIS-2` already gives about the geofence: never block the rep,
always record.

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
- **The ⚠︎ weeks** (W2, W4, W6, W7, W8, W9, W11) carry most of the schedule risk. If time is tight,
  they are where a second week gets spent — plan for it rather than compressing them.

  This line used to say "the four ⚠︎ weeks (W2, W7, W8, W11)" while the overview table marked six,
  which is the kind of drift a plan accumulates when the table is edited and the prose is not. **W9
  is the new one**, added when it was decomposed: it reads like a week of screens and is not. It
  carries a parity-engine slice of the kind W6 and W7 each spent several on, two local-store
  versions, and the first entity the device authors rather than copies — and it ends in the Phase 2
  demo, so it is also the week with the least room to quietly slip.
- **Demo-driven checkpoints** at W5, W9, W12, W15 are natural places to stop, record a GIF, and
  bank a portfolio-ready milestone even if later phases slip.
- **First portfolio-viable cut** is end of **W9** (offline field round-trip). Everything after
  deepens the story; the architecture and offline claims are already *demonstrated* by then.
- Custom-fields and i18n plumbing (W2/W4) are easy to under-scope — they touch many screens
  later; doing them early is deliberate, not gold-plating.

## How to track

Each package is a milestone; its bullets are the issues/tasks. Check items against the spec IDs
they cite so scope stays honest. Update the roadmap's phase checkboxes as packages land.
