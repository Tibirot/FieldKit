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
| | *Three of the four were **already done in W5** — `/offline` is precached per locale, it is the navigation fallback, and `requestPersistentStorage` has been called on every registration since. The row was written from the W5 plan rather than from the W5 code. Precaching field routes turned out to be the wrong thing to want: they are dynamic authenticated HTML with no build-time file to glob, and the `NetworkFirst` page cache already returns any route a rep has opened. What was genuinely missing is the **install prompt** — `beforeinstallprompt` was never captured, so the "installable" half of `OFF-10` did not exist — and all of `OFF-11`: `requestPersistentStorage` returns whether the browser agreed and **nobody read it**. The device screen now shows quota and warns on the two states a rep can act on. `persisted === null` (no such API) is deliberately not a warning, and the sabotage pass found **three of the install tests vacuous** — a `dispatchEvent` from outside React does not flush, so asserting absence straight afterwards passes whether or not the guard fired* | | |
| 12 | **The round trip against a live server** — the client stack (`db`, `manager`, `reference`) driven against a real API rather than a mocked one, and the Phase 2 demo recorded | `OFF-01`, `OFF-04` | 350 |
| | *Shipped as **shared wire vectors** rather than a live-API suite, by decision. A test pointed at a running server has the highest fidelity and runs on the day it is written; `vectors/sync/push.v1.json` runs on every push forever, and targets the exact blind spot that produced two shipped bugs in slice 9 — the client mocked the API and asserted `push` was *called*, the server serialised a constructed record which always writes `"visit": null`, and **each side tested its own idea of the contract while neither tested the contract**. The payloads are hand-authored as a device sends them for that reason: generating them by serialising a record would reproduce the blindness. Replaying both bugs fails the file. The C# reader also had to mirror `Program.cs`'s serializer options exactly — with `RespectRequiredConstructorParameters` missing it passed against the very bug it was written for, caught by the sabotage pass. The demo half is [a script](engineering/phase-2-demo.md), not a recording: what a repo can own is which steps to show and what each proves* | | |

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

#### Decomposition

**The audit is not the hard part; the score is.** Capturing availability, facings and prices is a
form over an aggregate, and this codebase has built several. What is new is that **the score is the
first rule that is both cross-language and tenant-configured** — pricing and the geofence are fixed
rules over tenant data, and a vector file pins them by naming inputs and an answer. Weights are
themselves data, so a scoring vector has to carry the weights *as input*, and "both engines agree"
becomes "both engines agree given the same weight set". Everything below is arranged around that.

**Three decisions cannot be added afterwards, and slice 0 is where they are made.**

**1. A weight set has to be immutable and versioned before the first audit is stored.**
`BR-AUD-8` says an audit records the weight-set version it was scored against, and that the server
recomputes with *those* weights. That is only possible if a published weight set can never change —
otherwise "recompute with version 3" means whatever version 3 says today, and a tenant adjusting
their weights silently rewrites last quarter's scores. There is no backfill: audits stored before
versioning exists have nothing to point at, and `Source` on `Visit` (W9 slice 0) is the precedent for
why that is a slice-0 problem rather than a later one.

**2. What a skipped pillar does to a weighted total.** `BR-AUD-2` is explicit that share-of-shelf is
*not computed* when the rep captured no category total — the pillar is skipped, not faked. But
`BR-AUD-4` says weights sum to 100%. So a score with a skipped pillar either renormalises over the
pillars that remain, or scores the missing one zero, and the two give very different numbers for the
same shelf. Whichever is chosen is a permanent property of every stored score and of every trend
built on them.

**3. Whether an audit is its own mutation or travels inside the visit.** `BR-AUD-6` says an audit
belongs to a visit and is sealed with it, which argues for one payload. The module registry names
`IAuditIngest` separately, which argues for two. This is now a *wire* decision with a test:
[`vectors/sync/push.v1.json`](../vectors/sync/push.v1.json) is the file both languages read, and
whichever way it goes, that file changes in the same slice. W9 slice 12 exists so this decision
cannot be made twice by accident.

**What W9 already paid for.** `IVisitWorkflow` is already snapshot-versioned reference config with a
pull feed and a device store — surveys and weights are the same shape, and slices 1–2 are that shape
a second and third time rather than a new mechanism. The parity harness, the vector reader check and
the `Money`/`decimal.js` pairing all exist; the score inherits them. And the `Audit` visit step is
already on the device as a labelled no-op (W9 slice 7), which is exactly the seam W11's screen fills.

| # | Slice | Requirements | ~Size |
|---|---|---|---|
| 0 | **The three the score cannot be given later** — immutable versioned weight sets, the skipped-pillar rule, and whether an audit is its own mutation. Decided in the spec and encoded in the C# types, before anything computes | `AUD-06`, `BR-AUD-4/8` | 250 |
| | *Shipped as **decisions only** — no code, and the row's "encoded in the C# types" was wrong when I wrote it. Every type that would encode these belongs to a module that does not exist yet: `IScoreWeights` is slice 1, the audit aggregate slice 3, `IPerfectStoreScore` slice 4. Creating them here would break §7's "an interface waits for its caller", a week after citing that rule to justify not doing it. **Skipped pillars renormalise** rather than scoring zero — unknown is not bad, and scoring the gap zero is the faking `BR-AUD-2` refuses; the cost is a gaming vector, made visible by recording which pillars were scored, and a score of `null` when none were. **An audit is its own mutation**, queued in the visit's own transaction: `/sync/push` answers per mutation precisely so one refusal cannot take a completed visit with it* | | |
| 1 | **Score weights are configuration** — `IScoreWeights` on Configuration: pillars, weights summing to 100, published immutably and versioned. Refuses a set that does not sum | `AUD-07`, `BR-AUD-4` | 400 |
| | *Shipped **without `IScoreWeights`**, for the reason slice 0 was shipped without types: the contract's first caller is slice 4's scorer, and an interface with no caller is a guess about a shape. What did ship is the aggregate, the schema and `/api/config/score-weights` — draft, edit-while-draft, publish-one-way. **Exactly 100 with no tolerance**: `33.33 × 3` is exactly `99.99` in `decimal`, so there is nothing for a tolerance to forgive, and admitting it would have the score renormalise against a total that is not 100. **Checked on every write** rather than at publish, which `BR-CFG-4` had said and no longer does — `BR-AUD-4` is a property of a weight set, not of a published one* | | |
| 2 | **Survey definitions are configuration** — `ISurveyForms`: typed questions, mandatory flags, snapshot-versioned exactly as the visit workflow is | `AUD-04`, `BR-AUD-7` | 400 |
| | *`ISurveyForms` **did** ship, and the difference from slice 1's absent `IScoreWeights` is worth naming: its consumer is the very next slice, which is the distance `IVisitWorkflow` was built at ahead of check-in. "An interface waits for its caller" is about designing against a consumer nobody has thought about, not one being written next. **Named forms, not one per channel**: a tenant runs a standing compliance form and a quarterly brand survey at once, so a form has an id. **An answer is filed under a question's `key`, never its id** — questions are replaced wholesale and their ids regenerated, so an id would leave `AUD-09` holding a dangling pointer after the first re-wording. **Nothing points at a form yet**: how an audit chooses one is slice 3's decision, and binding a `Survey` step to a form would mean changing `VisitStepDescriptor` — a public contract — for a consumer that does not exist* | | |
| 3 | **The Audit module** — a new module and schema: the aggregate, availability per MSL SKU, facings and the category total, observed prices, survey answers, photo *references*. Sealed with its visit | `AUD-01`, `AUD-02`, `AUD-03`, `AUD-05`, `BR-AUD-1/6` | 450 |
| 3a | **The module and the three measurements** — schema, aggregate, `IAuditIngest` + `IAuditQuery`, and `IVisitContext` on Visit | `AUD-01`, `AUD-02`, `AUD-03`, `BR-AUD-1/6` | 450 |
| 3b | **Survey answers and photo references** — the other two kinds of thing an audit holds, and the decision about how an audit names its form | `AUD-04`, `AUD-05`, `BR-AUD-7` | 300 |
| | *3b shipped: an audit gains **`SurveyFormId`, answers and photo references**. **How an audit names its form** — the decision slice 2 deferred — is a plain `SurveyFormId` on the audit, confirmed to exist and nothing more. **`BR-AUD-7` is a device rule**, not a server one: "mandatory questions must be answered before the audit step completes" is about completing a step, and re-checking it on arrival would refuse an audit for a question the form gained after the rep worked the shelf. **An answer carries the question as it was asked**, so it never needs the form to be readable. **A photo is a reference and nothing checks the object** — the upload path is W11, so every key stored today points at nothing, which is the ordinary case rather than a defect (`B5`). Slice 2's note that "refusing to delete a form in use is slice 3's rule to add" was **wrong**: Configuration may not read Audit's schema (ADR-0005), so no synchronous check can exist — it would need an integration event, and no requirement asks for one* | | |
| | *Split because 450 was optimistic and the survey half carries its own unsettled decision. **`IAuditIngest` moved forward from slice 6**, which keeps the Sync wiring, the score recompute and the wire vectors — the recompute needs slice 4 anyway, and a module whose only write path is another module's call cannot usefully exist without its contract. **No live REST capture endpoint**: the spec has audits worked at a shelf with no signal, so one would be an API no planned screen calls and a second door into an append-only record. **Reads are gated on `visit:read`** rather than a new `audit:read` — an audit* is *what happened during a visit, and a new permission would need a Keycloak change before any existing tenant could grant it* | | |
| 4 | **The score in C#** — pure, decimal, pillar by pillar, skipped pillars per slice 0. `IPerfectStoreScore` | `AUD-06`, `BR-AUD-4/5` | 400 |
| | *Shipped as a **pure static `PerfectStoreScore`**, not `IPerfectStoreScore` — the same rule slice 1 applied to `IScoreWeights`: the only C# caller is slice 6's recompute, inside this module, and a function is not a service. `Geofencing`, `PriceResolver` and `JourneyGenerator` all took this shape and slice 5 mirrors it in TypeScript, which only works if the whole thing is a function. **`ScorePillar` moved to `Configuration.Contracts`** — it shipped inside the module in slice 1, which was right while Configuration was its only consumer; Audit's scorer is the second, and AT-1 permits only contracts. Additive, ordinals unchanged (the column stores names). Two design calls that were not in the row: **share of shelf is capped at 100**, because own facings above the category total is a miscount and an uncapped pillar drags the whole score past 100; and **the total is computed from the rounded pillar percentages**, so `AUD-09`'s breakdown reconciles with the number beside it* | | |
| 5 | **The score in TypeScript** — the mirror, on `decimal.js`, with **generated vectors carrying the weight set as input** through the existing parity job | `AUD-06`, `BR-AUD-5/12` | 400 |
| | *Shipped as the row describes, and the weight-set-as-input part earned its emphasis: renormalisation only shows up when the weights vary, so the generated file crosses **six weight sets** — including one with a zero pillar and one naming a single pillar — with ten availability shapes, eleven facings shapes and seven price shapes. 400 generated cases, 16 hand-written. **The pillar breakdown is compared as well as the total**, because two engines can agree on a weighted mean while disagreeing about how they reached it. One hand-written expectation was **wrong** — I wrote a skipped price pillar as 50 — and the C# reader caught it, which is the apparatus doing its job on its first run. The parity job now runs `lib/audits` and its TypeScript floor moves 900 → 1300* | | |
| 6 | **`IAuditIngest` and the push path** — Sync applies a pushed audit through Audit, which recomputes the score from the captured entries and the audit's own weight version. The wire vectors grow a case | `OFF-04`, `BR-AUD-8` | 400 |
| | *Where W10's first five meet. `IAuditIngest` had already moved to slice 3a, so what landed here is the recompute and the routing. **`IScoreWeights` finally has its caller** — deferred in slice 1 under "an interface waits for its caller", and the shape follows from the one question Audit asks: *what did version 3 say?* Published sets only; a draft would give an audit a score that stops reproducing the moment somebody moves a slider. **The score is stored**, which reverses slice 4's comment — what would have been a second answer is the *device's* score, and the wire deliberately carries none; what is stored is the server's own recomputation over sealed inputs, on the row beside the entries and the version. The wire vectors grew **two** cases, not one, and the third had to change: `CapturedAudit` had been the file's stand-in for "a kind this server does not carry", and a test in `SyncPushJourneyTests` had the same claim. Both now say `CapturedOrder`, which expires in W11* | | |
| 7 | **Surveys and weights reach the device** — the pull feed for both, and the local stores behind them | `OFF-03` | 300 |
| | *Shipped as the row describes. The **weightings are the odd feed out**: every other reference entity has one current shape and a device only needs that, but an audit names the version it was scored against — so **every published version travels**, and a device holding last week's queued audit can still show what it scored. Cheap in the way that usually is not: a published set is immutable, so each version downloads once. **Only published ones travel** — a device scoring against a draft would have that audit refused on push, so it never sees a version it cannot legitimately name. A weight's percentage is a **string** on the wire and in IndexedDB, because `JSON.parse` would float a bare `33.34` before the device's scorer saw it. Dexie **v6** adds both stores with no `upgrade()`; the migration test asserts the negative that matters — versions 3 and 4 delete a watermark on purpose, and copying that shape here would silently re-download every device's territory* | | |
| 8 | **Authoring weights** (back office) — the screen an administrator uses, and the one that has to make "publishing is one-way" legible | `AUD-07` | 350 |
| | *Shipped as the row describes, and "legible" came down to one choice: a published version offers **no edit control at all** — not a disabled one, which is the dead control this codebase keeps rejecting — and what sits beside it instead is *start a new version from this*, pre-filled. `BR-AUD-8` expressed as a button rather than as a warning. The **nav departs from the wireframe**, which reaches this screen under a `Visits & audits` item that W9 ships: a built screen behind a disabled item is unreachable, so Admin gains **Configuration** — the drawing's own breadcrumb word — pointing at the weights exactly as `Journeys` points at frequencies. The one piece of arithmetic is the **running total**, and it exists because `BR-AUD-4` has no tolerance: a screen that disagreed with that check would refuse a set the server stores. It sums **integer hundredths, each value rounded before it is added** — the column is `numeric(5,2)`, so the screen has to total what will be *stored*, not what was typed (`33.335 × 3` is `100.02` in the row and `100.005` in the boxes). My first test of that asserted thirds drift; they do not — `33.34 + 33.33 + 33.33` is exactly 100 — so it passed against the naive sum, and it took a search to find a triple that actually does (`0.01 + 64.04 + 35.95`)* | | |
| 9 | **Authoring surveys** (back office) — typed questions, order, mandatory flags | `AUD-04`, `AUD-07` | 400 |
| 9a | **The question editor** — one form's name and its ordered, typed questions | `AUD-04`, `AUD-07` | 400 |
| 9b | **The survey list** — browse, delete, and the way into the editor | `AUD-04` | 200 |
| | *Shipped as the row describes, plus the section navigation the row implied and did not name: Configuration now has two screens and the sidebar has one entry for it, so both pages carry a link row — the shape `Outlets` and `Products` already use, rather than a second sidebar level built for one section. **The delete confirmation says what does not happen**, which is the opposite shape from the custom-field catalogue's: deleting a form stops it being asked, and the answers already given stay in Audit's rows and stay* readable*, because each carries its question's wording (slice 3b). Nothing is lost. **A nav bug came out with it**: the sidebar lit a section by `pathname.startsWith(href)`, and both `Journeys` and `Configuration` point at a screen* inside *themselves — so standing on the working calendar or on a survey, the section went dark. That was live before this slice. Items gain an optional `section`, matched on a segment boundary so a future `/journeys-archive` cannot light `Journeys` up* | | |
| | *Split because 400 was optimistic, and the seam is the server's rather than mine: **a form cannot be created empty** (`config.survey.empty`), so the editor is the irreducible unit and the list is a layer on top of it — a create-only screen could offer nothing the editor does not. 9a is reachable by URL alone until 9b ships the way in. **A question's key is fixed once saved**, which is a client-side policy and not an API rule: a `PUT` replaces the questions wholesale and would take a renamed key without complaint, but an answer is filed under it (`AUD-09`) and Configuration cannot see whether a rep has answered (ADR-0005) — so the only safe assumption about a saved question is that somebody has. **Order is edited with buttons, not by dragging**: the wireframe draws a handle, and a drag-only reorder cannot be operated from a keyboard or heard by a screen reader. **`keyFromLabel` moved to `lib/forms`** on gaining its second caller. One thing the screen does not do: `config.survey.nameTaken` renders without naming the form, because the server sends no `name` argument for that code — a placeholder would throw inside `next-intl` at render, which is exactly the coupling ADR-0012 named as its cost* | | |

**Not in W10:** the **audit capture screen** and the device-side audit store (W11, with order capture
— the engine lands here, the form there). **Photo binaries** (`OFF-08`, W11): slice 3 stores
references, and they point at nothing until the upload path exists — worth saying out loud, because
a reference with no object behind it looks like a bug to anyone who finds it first. **Conditional
survey logic** (`AUD-08`) is `Could`. **Trends and pillar breakdowns** (`AUD-09`) are W12's
dashboards, and they are the reason slice 0's weight-version boundary matters: a trend line that
crossed a re-weighting without saying so would be a chart of two different questions.

### Week 11 · Order + offline UIs + sync v2 + photos ⚠︎
**Goal:** close the golden path, offline, with binaries.
- Order: aggregate, lines, on-device pricing/promos, minimum, submit/lock, **rejected→re-open-editable** (`IOrderIngest`, BR-ORD-9) ([Order spec](product/23-order-capture.md)) — `ORD-01…07, 12`.
- Offline **Audit** + **Order capture** screens (per wireframes), fully offline.
- Sync v2: transactional conflict rules (append-only, snapshot-version flagging, as-of-capture validation); **device-swap drain-push** + local-store migration; **photo out-of-band upload** (presign + retry) ([B5](product/decisions-and-assumptions.md#b5--photo--binary-sync), [B7](product/decisions-and-assumptions.md#b7--conflict-resolution-matrix)).

**Done when:** an offline audit + order are captured, submitted, and reconciled; photos upload independently. **⚠︎ Heavy** — budget ~1.5 weeks.

#### Decomposition

**Two things are new, and neither is "another aggregate with a screen".**

**A server refusal now has to be survivable.** Every mutation the outbox has carried so far was
unconditionally acceptable: a visit happened, an audit measured a shelf. Neither can be argued with,
which is why the push protocol's whole conflict story is [B7](product/decisions-and-assumptions.md#b7--conflict-resolution-matrix)'s
"engineered out". An order can be **refused** — by the assortment, by the minimum, or by a human in
the back office — and `BR-ORD-9` says the rep's work must never be stranded when it is. That inverts
the ledger's assumption: it exists so a **retry** returns the first answer, and this is the one case
where the correct behaviour is a **new** mutation, because the first is terminally rejected rather
than merely done. The outbox has no vocabulary for that today.

**Photos are the first thing that is not JSON.** Everything queued so far is a record; a photo is
bytes, on a different transport, retried on a different schedule, referenced by a key that already
exists before the object does — W10 slice 3b stores those dangling keys deliberately. So "the audit
synced" and "the audit's photos uploaded" become two different truths, and a rep who can only see the
first will believe a half-uploaded visit is finished.

**Three decisions cannot be added afterwards, and slice 0 is where they are made.**

**1. The reprice flag has to be on the first order row ever stored.** `BR-ORD-6` says an order records
the pricing snapshot it was captured against, and that a server disagreement is *flagged, not
silently changed*. There is nothing to backfill from: an order ingested before the flag exists is
indistinguishable from one the server agreed with. This is exactly the `Source`-on-`Visit` argument
from W9 slice 0, and it is the second time the same shape has come up — worth noticing as a pattern
rather than treating as a coincidence.

**2. What a re-opened order *is*.** `BR-ORD-9` says a rejected order re-opens editable and is
resubmitted under a new mutation id, with the original terminal. That leaves one thing genuinely
open: whether the re-opened order keeps its **order id** with a second submission, or becomes a
**new order** that references the rejected one. Both satisfy the rule; they give different answers to
"how many orders did this outlet place", and the answer is permanent in the data the moment the first
rejection lands.

**3. `CapturedOrder` stops being a stand-in.** In W10 slice 6 it became the wire vectors' example of
*"a kind this server does not carry"*, in [`vectors/sync/push.v1.json`](../vectors/sync/push.v1.json)
**and** in `SyncPushJourneyTests`. That was recorded at the time as expiring in W11, and this is W11:
the moment the order arm ships, both assertions quietly stop asserting anything unless a different
unsupported kind replaces them. A negative pin has a shelf life, and this one has a date.

**The W11 row is partly already done, and the row is what is wrong.** It lists *"device-swap
drain-push + local-store migration"* under sync v2. **Both shipped in W8** — drain-push as slice 12,
local-store migration as slice 11 — because each turned out to be a property of the mechanism being
built rather than a later hardening pass. What actually remains of "sync v2" is the **transactional
conflict rules** (as-of-capture validation and the reprice flag) and **photo upload**. This is the
same error W9 slice 11 recorded: a row written from the plan rather than from the code.

**What W11 inherits, and it is a lot.** The pricing engine already exists **in TypeScript**, parity-
tested against C# on a generated corpus (W7 slices 11–15) — so `ORD-02`/`ORD-03` are a *caller* of an
engine, not a second engine, and the week's riskiest-sounding requirement is its cheapest. The
outbox, the mutation ledger, the idempotent push and the `PushedMutation` discriminator are W8's; an
order is one more arm, the shape the audit arm took in W10 slice 6. The audit **engine** — capture
model, score, ingest, recompute — is W10's, so the capture screen writes into a shape that already
exists and already checks itself. Dexie's versioned migration regime with its "what must not be
deleted" test is W8 slice 11's. And `Audit`, `Order` and `Photo` are already on the device as
**labelled no-op steps** (W9 slice 7): three seams cut to exact size, waiting.

| # | Slice | Requirements | ~Size |
|---|---|---|---|
| 0 | **The three the order cannot be given later** — the reprice flag on the row, what a re-opened order *is*, and retiring `CapturedOrder` as the vectors' unsupported-kind stand-in | `ORD-08`, `BR-ORD-6/9` | 250 |
| | *Shipped, and **one of the three was already decided** — this row overstated it. `F4` in the [order spec](product/23-order-capture.md#f4--rejected-order-remediation) settles the identity question: a rejected order re-opens, is retained server-side and pulls back to the rep's device, so it stays **one order with one id** and "orders placed" counts intent rather than attempts. What was genuinely open, and what slice 0 settles, is that **each submission is an append-only child** — without it "the original mutation id is terminal" has nothing to be terminal against, and the aggregate would hold only the latest attempt. That also reconciles the re-open with `B7`: the **history** appends while the **aggregate** re-opens. **A disagreement is stored as two numbers, not a boolean**: the device's totals stay the record and the server's recomputation sits beside them, which is deliberately the **opposite** of `BR-AUD-8` — a score is a derived measurement so the server's wins, a price is what a human agreed to pay so the device's does. **The placeholder sweep found a third copy** neither the vector file nor the journey test knew about (`SyncPushTests`), which had been asserting the server does not carry orders since W10 slice 6. All three now say `CapturedReturn`, drawn from the **Won't** list (`ORD-11`/`BR-ORD-8`) rather than the *not yet* list — twice burned is enough, and both expirations were silent. Two stale entries in the spec's open questions went with it: order-level promotions were answered by `BR-ORD-3` long ago, and back-office accept/reject is now recorded as **an API with no screen*** | | |
| 0b | **Two defects with four occurrences each** — a global `JsonStringEnumConverter` (three per-property band-aids so far, and order status would be the fourth) and an EF helper for entities reached through a navigation with a client-generated key (four, and order lines would be the fifth). Both get *cheaper* before the module and more expensive after | — | 200 |
| 0b-i | **The EF half, and it wanted no helper** | — | 250 |
| | *Shipped, and **the row prescribed the wrong shape**. A helper is something the next person has to know to call, which is what the five `db.Set<TChild>().AddRange(parent.Children)` lines already were — the defect would have survived as a better-named workaround. The **cause** is that EF's default for a `Guid` key is `ValueGenerated.OnAdd`, so a key that holds a value can only have come from the store; when a new child appears on an **already-tracked** parent, EF attaches it with `AttachGraph(entry, Added, Modified, forceStateWhenUnknownKey: true)`, reads the client-set key as proof the row exists, and issues an `UPDATE` that matches nothing. The inference is sound and its premise is false here, so `ClientGeneratedKeyConvention` withdraws the premise — one `IModelFinalizingConvention` in `ModuleDbContext`, and all five call sites were **deleted** rather than rewritten. **Finalizing, not `OnModelCreating`**: that method runs base-first, so a derived context's `modelBuilder.Entity<T>(…)` lands after any sweep, and an entity reached only through a navigation would be missed. **The count was wrong, and by the tally's own method — five, not six.** W11 slice 3's order lines were probed by deleting the workaround: 40 tests still passed. `Add` on a **new detached root** paints the whole graph `Added` whatever the keys hold, so that site never had the defect; the comment claiming it did was copied along with the code, and the tally counted the comment. **Withdrawing `OnAdd` also withdraws EF's offer to invent a key**, whose absence fails quietly — the first unnamed row stores an all-zero Guid and succeeds, the second trips the primary key in someone else's request — so `ClientGeneratedKeyGuard` refuses it at the moment of the mistake and names the property. Ten empty migrations, one per module: the metadata moved and **no SQL did**, verified by reading every generated `Up`. The `JsonStringEnumConverter` half is still open* | | |
| 0b-ii | **The JSON half — the rule moves to the enum** | — | 300 |
| | *Shipped, and **the count was understated by an order of magnitude**: the row said "three per-property band-aids", and there were **twenty-six**, across eight modules. The row also prescribed the wrong mechanism. A global converter on the host is necessary and **not sufficient** — stripping the attributes with only that in place broke hundreds of tests, because the attributes were doing a second job nobody had written down: they made the DTO self-describing under **any** serializer, not just the host's. The wire-vector readers and every `ReadFromJsonAsync` in the test suite were relying on it. Threading the host's options through 472 read sites would have moved "remember the converter" from production code into tests, which is the same defect wearing a different coat. So the attribute moved **to the enum declaration** — thirty-four of them, one per type rather than one per property. That is the durable shape: the property count grows with the API and the enum count does not. **The global converter stays, and earns its place on exactly one case**: `DayOfWeek` belongs to the BCL and cannot carry an attribute of ours, and `WorkingCalendarRequest` takes a list of them. **The sabotage pass found that case unguarded** — deleting the global registration left all 1,055 tests passing, because every calendar test round-trips through a typed client that serialises whatever the server reads. `DayOfWeek`'s ordinals start the week on Sunday, so the off-by-one this protects against was protected by nothing; the new test posts raw JSON and now fails without the registration. **No response changed shape**, which [api-contracts §1](architecture/13-api-contracts.md) had already predicted — every enum on a response either carried the attribute or was hand-rendered to a `string`. Those `string` fields stay: a DTO naming its own vocabulary is a choice, not a workaround* | | |
| 1 | **The Order module and a draft** — aggregate, lines in UoM/pack, the assortment gate, currency from the resolved price list | `ORD-01`, `BR-ORD-1/7` | 450 |
| | *Shipped as the **eleventh module**, and the row's title was wrong twice. **There is no draft here**: `B4` puts `Draft` on the device, so the first status this server writes is `Submitted` and there is no create-a-draft path — the same "no live capture endpoint" call Audit made in W10 slice 3a, for the same reason (`B7` rests on one writer). And **the assortment gate is not here**: `BR-ORD-1`'s answer is a rejection the rep can **fix**, which needs the re-open path, so it lands in slice 4 with it — a gate without somewhere to go would strand exactly the work `BR-ORD-9` protects. `IAssortmentService` and `IPricingService` are named in the spec as consumed and still do not exist; per this codebase's own rule they wait for their callers in slices 2 and 4. **`BR-ORD-7` turned out to be a modelling choice, not a check**: a line carries an amount and the order carries the currency, which makes a mixed-currency order **unexpressible** rather than refused. The schema is `ordering`, not `order` — `ORDER` is a SQL reserved word, and the alternative was quoting it forever. The context opts **out** of sync tracking for now and will opt back in at slice 4: a rejected order is the one transactional record that flows back down, but a counter no feed reads is the same waste as W8 slice 6's writerless store* | | |
| 2 | **What an order costs** — line pricing and tax through the existing C# engine, one line-level promotion plus an order-level one, and the order minimum | `ORD-02/03/06`, `BR-ORD-2/3/5` | 400 |
| 2a | **What a line costs, in C#** — the arithmetic the resolvers stop short of | `ORD-02/03`, `BR-ORD-2/3` | 400 |
| 2b | **The same in TypeScript, and the shared corpus** — the mirror, through the existing parity job | `PRD-08`, `BR-PRD-7` | 300 |
| | *Shipped, and the mirror **agreed with C# on all eleven cases first run** — which is what the corpus being written against the C# original, rather than against the mirror, is supposed to buy. The parity floor moves 1300 → 1650 (1,690 actual). The sabotage pass found **a vacuous test of mine**, the fourth in this project: I claimed the `Decimal.floor` prevents a float-coercion bug, and `Math.floor(Number(q) / g)` passes every case in the file — at realistic order quantities the two never disagree. The `Decimal` is still right as the module's own no-floats rule, but that is a consistency argument rather than a demonstrated bug, and the test now pins what is checkable: **floor, not round or ceil** — 20.3 units against "buy 2 get 1" is six free, and round or ceil would give away a seventh. Worth noting a pre-existing divergence found in passing: the C# enum says `FixedAmountOff` and the TypeScript says `AmountOff`. Nothing reads the name — both engines branch on which field is set — so it is cosmetic, and left alone rather than renamed inside a parity slice* | | |
| 2c | **What an *order* costs** — `IPricingService` on Products: gathering, the order-level promotion, and the totals | `ORD-02/03` | 400 |
| | *Shipped without the order-level promotion, which **does not exist in the model** — the third dependency this week the plan assumed and the schema does not have, after `ORD-15`'s outlet hold and `ORD-06`'s minimum. `BR-ORD-3` allows "one line-level plus an optional order-level one" and `B1` calls them "separate and additive", but a `Promotion` targets products or categories and reaches an outlet or channel; nothing marks one as applying to a total, `PRD-05` lists four line-level types and no fifth, and no requirement asks for authoring. Adding it here would have meant a new field, a screen and a stacking rule decided inside a pricing slice. **The gathering is batched and is a second implementation rather than a fourth copy**: the promotion and tax endpoints gather per product because they answer about one, and an order is tens of lines — a per-line round trip is tens of queries on a path a rep waits for. **The resolver takes an `int` quantity and a line carries a decimal**, so this truncates: a tier reading "buy 6 or more" is a promise about whole units, 5.9 kg has not reached six of anything, and rounding up would hand over a discount that then applies to the whole line. **Totals are sums of the rounded rows**, not a re-derivation — the one arithmetic error a reader always notices is a total that disagrees with the column above it* | | |
| | *Split because **the row was wrong about what existed**. "Through the existing C# engine" is true of prices and tax and false of the thing in between: `PriceResolver`, `PromotionResolver` and `TaxEngine` answer* which *price,* which *promotion,* which *rate — and **neither language had a function that turned those into money**. `BR-ORD-2`'s actual content was missing, in both, which makes it a parity problem rather than a caller. Hence 2a/2b in the C#-then-mirror rhythm W10 slices 4 and 5 used, and 2c for the gathering that feeds them.* | | |
| | *2a shipped `LinePricing`: subtotal, discount, net, tax, total. **Discount before tax** — taxing the undiscounted subtotal charges a shopkeeper for money nobody paid. **A fixed amount comes off the line, not each unit**: both readings are defensible and differ by the quantity, and "€5 off" on a line of twelve is what a shopkeeper hears. **A discount larger than its line gives the line away rather than refunding** — unclamped the total falls as the shop buys more, and refusing instead would strand an order over a promotion the rep never chose (`BR-ORD-9`). **A cross-product bundle discounts nothing here**, deliberately: "buy six of these, get one of *those*" belongs to a line this function cannot see, and crediting it here would balance the order while putting the money against the wrong product for every report downstream — 2c's to apply. **The order minimum is not in any of these**: `ORD-06` needs a configured minimum and **no such configuration exists anywhere**, the same gap `ORD-15` has. It is Configuration's to own, with its own authoring surface, and naming that is cheaper than discovering it mid-slice* | | |
| 3 | **Submit, seal, lock** — editable only while `Draft`, and `OrderSubmitted` | `ORD-05/07`, `BR-ORD-4` | 300 |
| | *Shipped, and **the lock turned out to be already broken rather than missing**. Slice 1's replay check keyed on the **order id**: a second push naming an order that already existed returned `Ok` and changed nothing, which reads as idempotency and is actually an edit after submit being silently discarded. `BR-ORD-4` was written down in slice 1 and enforced by nothing — the row's "editable only while `Draft`" was true of the code by accident, in the way a locked door is secure when nobody has tried the handle. The fix is that **the mutation id is the replay key**, recorded as the append-only `OrderSubmission` slice 0 argued for, which is what tells a retry from an edit: the same id succeeds, a different one meets `AlreadySubmitted`. It is the **same id Sync's ledger keys on**, and the unique index is `(tenant, mutation)` rather than `(tenant, order, mutation)` — the narrower one would let two orders claim one mutation, and "has this push been applied" must have a single answer. **No outcome column on the submission yet**: nothing can reject an order until slice 4, so a column with one possible value would be a schema version spent on nothing — the same call W8 slice 6 made about the writerless `blobs` store. **`OrderSubmitted` is raised inside `Order.Record`, not by the ingest service**, so there is no arrangement of the code in which a stored order is unannounced; the outbox interceptor commits it in the order's own transaction (ADR-0006). The sabotage pass confirmed both: re-keying the replay on the order id fails exactly the new lock test, and dropping the `Raise` fails exactly the announcement test. What looked like a sixth occurrence of the client-generated-key EF defect (`AddRange` on the submissions) **was not one** — slice 0b-i probed it and found the workaround inert here, because `Add` on a new root already paints the graph `Added`* | | |
| 4 | **Rejected, and re-opened** — the sole exception to the lock: whole-order reason plus the offending line, the original mutation id terminal, resubmission under a new one | `ORD-12`, `BR-ORD-9` | 400 |
| 4a | **The rejection, and the correction that follows** — the aggregate, the ingest branch, and the API with no screen | `ORD-12`, `BR-ORD-9` | 450 |
| | *Shipped. **Split because the row bundled five things**, not because it was wrong: the assortment gate (`BR-ORD-1`, deferred here from slice 1), sync tracking opting back in so a rejection can pull down, and cancellation (`Rejected → Cancelled`) are all still ahead — 4a is the half a rejection needs to exist at all. **The submission's `Outcome` column finally has two values**, which is what slice 3 said it was waiting for. **The rejection lives on the attempt, not the order**: the order carries one status, and putting the reason on the submission is what makes `BR-ORD-9`'s "the original submission's id is terminal" checkable rather than a sentence — the corrected order simply has a newer latest attempt, so the rejection stops being current without being erased. **Rejecting twice is a 409 rather than idempotent** — the second reason would replace the one the rep is already acting on, and a rep reading "off assortment" while the server holds "outlet closed" is worse than an error somebody sees. **The offending line is nullable and that is `F4`'s own reading**: an off-assortment SKU points at a line the rep can fix, a closed outlet points at nothing. **A permission was minted — `order:reject`, named for the act rather than the table**, because a holder may refuse an order and may never alter one, which is what `order:write` would have implied and what `BR-ORD-4` denies to everybody. It cost a realm change, and **a realm change is not applied by deploying** (W10's finding) — the deployed environment answers 403 until the role is added by hand. **No `OrderRejected` event**: the spec's §8 names only `OrderSubmitted`, nothing subscribes, and the rep learns through the pull feed instead. Two sabotage passes, each failing exactly its own test. Caught in passing: `OrderQueryService` did not load submissions, so the first read-back after a rejection was a 500 — `Describe()` had grown a dependency the reader had not* | | |
| 4b | **The assortment gate, which rejects rather than refuses** — `IAssortmentService`, three slices after the spec named it | `BR-ORD-1`, `ORD-12` | 400 |
| | *Shipped, and **the shape of the answer is the whole point**: an off-assortment line does not refuse the push, it **stores the order and rejects it**. A refusal would answer "no" to a device holding a sealed order the rep cannot edit — work stranded, which is precisely why slice 1 deferred this until the re-open path existed. So the push still succeeds: the server was asked to record an order and did, and the rejection travels back down the pull feed like everything else that happens to a rep's work. **`IAssortmentService` was named in the product spec in W6 and built now**, when its first caller arrived — the codebase's standing rule about contracts. **It is one implementation, not a leaner second one**: the effective assortment (a channel's list with the outlet's own additions and removals) has three edge cases, and a private copy for Order would let one path call a product orderable while the other refuses it. The computation moved out of `AssortmentEndpoints` into `AssortmentService` and the endpoint now calls it; Order pays for a sku/name join it does not need, which is the trade taken deliberately — 2c's licence for a second implementation rested on per-line round trips, and both callers here want one outlet's list in one query. **The gate runs on the resubmission path too**, because a correction is a submission and not an exemption; sabotaging just that arm fails exactly one test. **The first offending line by position**, so the same order rejects the same way twice. **The cost landed in the fixtures**: every existing order test named a freshly minted `Guid`, which is by definition unassorted — so enforcing `BR-ORD-1` turned nine passing tests red at once. They now stock the shop, which is what a rep ordering from their own catalogue actually looks like, and the fact that they did not before is worth noticing* | | |
| 5 | **The push path** — `/sync/push` grows an `order` arm and `CapturedOrder` becomes real, in the wire vectors and in C# | `OFF-04` | 350 |
| | *Shipped, and the row was right for once. **The arm that is unlike the other five**: it is the only one that hands the mutation id to the module it calls. Every other kind is idempotent on its own subject — a visit by its id, an annotation by the call it is about — so a repeat is recognisable from the payload; an order is not, because the same `orderId` arrives both when a device retries and when a rep corrects a rejection (`BR-ORD-9`). Slice 3's contract change is what pays off here, and Order recording that id is what stops the two ledgers drifting. **An order refused by `BR-ORD-1` still answers `accepted`**, which reads wrong and is not: the push asked this server to record an order and it did, so the rejection rides on the order rather than on the transport — answering `rejected` would tell the device the mutation never applied and the retry would find the order already there. Only a refusal that stored *nothing* is a rejection at this layer. **`CapturedOrder` is real in the shared corpus**, three slices after slice 0 moved the unsupported-kind placeholder onto `CapturedReturn` for exactly this reason — that swap was made because the previous two placeholders expired silently when the server learned to carry them, and this is the expiry it was predicting. Both languages read the new case: the TS suite gained two tests without a line of test code, since it is `it.each` over the file. **A finding for slices 6–8**: the vector carries the order's decimals as JSON **numbers**, because `CapturedOrderLine` uses bare `decimal` and nothing configures `AllowReadingFromString`. That is fine today and is a trap for the device store — a screen that computes in `decimal.js` and serialises to a string will meet a 400. Named here rather than fixed inside a routing slice* | | |
| 6 | **The device's order store** — Dexie v7, a draft that survives a reload and an app update | `ORD-05`, `OFF-01b/13` | 300 |
| | *Shipped, and **it is where slice 5's finding had to be settled**. The store holds every number as a decimal **string** — a quantity can be a weight, a price is money, and `0.1 + 0.2` is exactly what `Money` exists to keep out — while the wire needs JSON numbers, because `CapturedOrderLine` takes bare `decimal` and nothing configures `AllowReadingFromString`. So `captured()` is the **single place** the conversion happens: the value crosses `Number` once, already rounded, rather than an IEEE-754 float sitting between the rep's screen and the record. It is still the wrong shape long-term — `Money` crosses this API as a string by rule and these fields are not `Money`, because slice 1 chose bare decimals — and that is now written down at the one line that would change. **`Draft` is a device state and this is the first store to hold one**: the server has no create-a-draft path (`B4`, `B7`), so a draft lost before submit is work that existed nowhere else, which is what makes `ORD-05` a store question rather than an outbox one. **The seal is one transaction**, order and outbox row together, the same call `checkOut` makes. **Two sabotage passes, and the first one was a fake**: `PowerShell`'s `.Replace()` silently no-opped on indentation, the suite passed, and it took reading the file to notice nothing had been sabotaged. Redone properly, the numeric projection fails exactly its own test and dropping the `visitId` index fails all ten. Worth recording as a hazard of the method rather than a one-off — a sabotage that does not apply looks identical to a rule that is not load-bearing* | | |
| 7a | **Money stops being a float on the way down** — the pull feeds send decimal strings | `BR-PRD-8`, `OFF-03` | 300 |
| | *Shipped, and **it was not a planned slice**. Building the screen's pricing meant writing the first thing that computes from the device's own price tables, and that turned out to be impossible to do honestly: `PriceLineSnapshot.Amount` was a bare `decimal`, so `MoneyJsonConverter` did not apply, so it crossed as a JSON number — and `JSON.parse` makes an IEEE-754 float of it **before** `decimal.js` is handed anything. The whole of W7 slices 11–15 built a decimal-exact engine over a value that was already inexact. Six decimals across two feeds, all of them money or percentages. **The parity vectors could not see it**: they feed the engine strings from a file and never touch a pull feed — the same two-suites-one-seam shape as the `/sync/push` bug W9 found, arriving in the other direction. **The rule already existed and was applied inconsistently**: `ScoreWeightSnapshot.Percentage` got it right in W10 with a comment making exactly this argument, and prices shipped in W6 as numbers with a comment asserting the opposite — "`amount` arrives as a number because that is what JSON has" was the bug, stated confidently, and every clause after it was true. **The write side was already correct**, which is what made it invisible: `PriceLineRequest` takes a string, so the inconsistency was internal to one module. **Version 8 re-baselines rather than transforms** — unlike versions 3 and 4, which dropped a watermark because a field was added, this drops one because the rows are the wrong *type*, and a delta pull would only correct the prices somebody happened to edit afterwards. Rows are left in place so an upgraded device that goes offline can still show a catalogue. **Tax is still missing entirely** and is now the only known parity gap left: there is no `ref_tax_rates` store and no feed sends one, so `priceLine` gets a null rate and every device total will differ from the server's by exactly the tax* | | |
| 7b | **Tax reaches the device** — `TaxRate` becomes sync-tracked, `ref_tax_rates` exists, and the pull carries a rate | `PRD-07`, `OFF-03` | 300 |
| | *Shipped, and it is **7a's closing sentence turned into a slice**. The device has had a tax engine since W7 slice 14 and this server has had rates since W6 slice 13; nothing carried one to the other, and `TaxRate` was not even `ISyncTracked` — so there was no delta to send even in principle. The failure mode is the quiet kind: `priceLine` reads a missing rate as **unknown** and charges nothing, so the rep sees a plausible net total and the server's recomputation exceeds it by exactly the tax, on every order. **The percentage is a string**, by the rule 7a established, and the tests assert the JSON **kind** rather than the value — `GetDecimal()` passes against a number and a string alike, which is precisely how the price feed hid for five weeks. **Tombstones are the normal path here, not the rare one**: the rates PUT replaces a class's whole set, because a rate's identity is its country and start date together, so an author correcting a date deletes and recreates — and `resolveTaxRate` picks the latest `effectiveFrom` that applies, so a stale row with a later start date wins outright. **Expired rates travel**, as expired promotions do (`BR-PRD-6`). **The migration is the seventh backfill**: a zero-defaulted column is never `> 0`, so without `SyncBackfill.Sql` every existing rate would sit on the server and never move. **Five sabotage passes, one of which was a fake again** — the frozen-cursor edit failed to apply on an indentation mismatch and the suite stayed green, the same `PowerShell` `.Replace()` hazard slice 6 recorded; the fix is to compare the file before and after and say so out loud. **The device still cannot use any of this**, and that is not a gap in this slice: `OutletSnapshot` carries no `countryCode`, so the shop's half of the match is missing, and slice 7c is now a prerequisite of the capture screen rather than a nicety* | | |
| 7c | **The outlet says where it is** — `OutletSnapshot` gains `countryCode`, and the device can finally pick a rate | `PRD-07`, `OFF-03` | 250 |
| | *Shipped, and it is the join 7b could not make: rates were on the device and unusable because nothing there could name the country of the shop the rep was standing in. **The country is the shop's, not the tenant's** — a tenant selling across a border has reps who cross it, and a device reading one country from configuration would charge Romanian VAT in Sofia, which looks entirely ordinary on a screen. **Null is unknown in three places and deliberately alike**: no country on the shop, no tax class on the product, no rate authored for the pair. `priceLine` charges nothing for a null, which is the same total a genuine `"0.00"` produces — safe only because the caller keeps the distinction, and the server draws the same line. **The re-baseline is versions 3-and-4's shape, not version 8's**: a field was added rather than a type corrected, so the rows are fine and merely thin — but a delta would still only fill the country in for shops somebody happened to edit afterwards, and a shop nobody touches again would price untaxed for the life of the install. **The sabotage pass found a real gap rather than confirming the tests**: nulling the country in the feed's *delta* arm alone left all sixteen tests green, because an outlet entering a device's scope arrives through the **baseline** arm and a first pull never touches the other one. That is also the ordinary way a country appears — onboarding data is half-known (`OUT-01`), so a shop is created without an address and completed later — so it got the test it was missing. Six sabotage passes in total. **One test lost an assertion, honestly**: `leaves the other watermarks exactly where they were` was written about version 6 but opens the database through every version, so v10's deliberate reset now fires inside it; products is the surviving witness and the comment says why rather than the assertion being quietly softened* | | |
| 7d | **What the whole order costs, on the device** — the mirror of `IPricingService`: gather, resolve, sum | `ORD-02/03`, `PRD-08` | 300 |
| | *Shipped, and **split out of slice 7 because the screen cannot be written honestly without it**. W6 and W7 mirrored the three resolvers, W11 slice 2b mirrored the line arithmetic, and 7a–7c fixed the inputs — but nothing gathered candidates out of the device's store and ran them, so every rule was in place and none of them met. **Two decisions belong to the mirror rather than the original.** `PricedOrder`'s totals are **nullable**: the C# leans on `default(Money)` — a zero with an empty currency — and the TypeScript `Money` refuses to be built without one, which is the better rule, because a fabricated `"RON"` on an order that priced nothing is a number a screen renders as real. And it **retired the device's second answer to `BR-PRD-2`**: `priceListFor` picked a list by the order IndexedDB returned assignments in, which was right by accident (outlet assignments were queried first) and disagreed with `resolvePrice` the moment two lists tied — the same "one implementation, not a leaner second one" call slice 4b made server-side. Its two real cases moved rather than being deleted. **A wrong comment was caught by writing the first code that believed it**: `ReferencePromotionTarget` said an empty target list meant "everything", and the server says the opposite — an empty set is how a deal is withdrawn. Reading it the documented way would have applied every withdrawn promotion to every line; it is the same shape as slice 7a's "`amount` arrives as a number because that is what JSON has", a confident sentence with no code under it yet. **Six sabotage passes, and the first found a vacuous test**: `prefers the outlet's own list over its channel's` passed with the scope pinned to a constant, because the two lists' ids happened to break the tie the right way. Renamed `z-…`/`a-…` so the tiebreak points the wrong way and only `BR-PRD-2` can produce the answer* | | |
| 7 | **The order capture screen** — pick from the assortment, quantities in pack, totals live from the **TypeScript** engine | `ORD-01/02/03` | 450 |
| | *Shipped, and it **found the gap the model has been carrying since slice 1**: there is nowhere on the wire to put tax. `CapturedOrderLine` has `unitPrice` and `lineTotal` and nothing else, and `OrderLine.LineTotal` is documented as "what the device made of the line **after any promotion it applied**" — the net. So the screen shows the rep four numbers and sends three: the gross they read out to the shopkeeper is not the number the back office receives, and the order's total is net of VAT. `ORD-02` asks the device to price tax and `BR-ORD-6` makes the device's totals the record; between them there is no field. **Storing the net is the only safe reading available today** — putting the gross in `lineTotal` would feed tax into a column the server sums into a total that has none, so the two sides would disagree by exactly the VAT on every order, which is the failure 7b was opened to fix arriving from the other end. Named here as the next thing the captured shape needs, ahead of slice 14's as-of-capture work. **The draft is created on the first line, not on opening the screen**: `BR-ORD-7` takes the currency from the resolved price list, so an order that has never been priced has no currency to be created with — and a rep who opens the step and changes their mind leaves nothing behind for slice 8 to decide about. **`BR-ORD-1` is enforced by not offering the line**: an off-assortment product the rep could add would be stored and rejected on push (slice 4b), stranding the work, and the refusal is free while they are standing at the counter. **The order link sits beside "Mark done", not instead of it** — taking the order and ticking the step are two acts, and completing the step on submit would make an order the only way to finish it, so `BR-VIS-3` would keep a rep in a shop that had nothing to order. **`inputMode="decimal"`, never `type="number"`**: a numeric input hands back a `number` on some browsers, which is the one coercion `BR-PRD-8` forbids, on the value the whole engine exists to keep exact. Three sabotage passes. **Submitting is slice 8**, with the order minimum that still has no configuration to read* | | |
| 8a | **Submit and the outbox** — the seal, the queue, and the lock a rep can see | `ORD-07`, `BR-ORD-4` | 250 |
| | *Shipped, and **split from the minimum because `ORD-06` has no configuration to read**. `BR-ORD-5` says a minimum applies *if configured* and nothing in the system configures one — no entity, no authoring surface, no feed, no device store. Slice 2a named that gap when the line pricing was written and this is where the bill arrives: it is Configuration's to own, and a minimum invented on the device would be a rule no administrator could see, change or be held to. `ORD-07` is a **Must** and `ORD-06` a **Should**, so the Must ships. **The seal itself was already built** in slice 6 — `submit()` puts the order in the outbox in the transaction that marks it submitted — so what this slice adds is the screen: the button, the refusal a rep can read, and the lock made visible. **`orderFor` is a second query rather than a flag on `draft`**: "what may still be edited" has to keep answering nothing once the order is sealed, which is `BR-ORD-4` as the store sees it, while "show me what I sent" is a different question — and conflating them is exactly the bug the first version had, where a sealed order rendered as "nothing on this order yet" with a catalogue under it. **Queued, not sent**, in the wording: the rep is offline more often than not and the shell's pending count owns that answer (`OFF-05`). Three sabotage passes. **A cross-test leak surfaced and was fixed rather than retried**: the suite deleted each test's database while the previous test's components were still subscribed, because Testing Library registers its cleanup at import time and Vitest runs `afterEach` last-first — and `useLive` treats a failed observation as terminal, so the next screen sat on its initial value and rendered a priced line as "No price". It passed in isolation and failed in the suite, which is the signature* | | |
| 8b-i | **The order minimum, authored** — a value per channel with a per-outlet override, and the rule that picks one | `ORD-06`, `BR-ORD-5` | 350 |
| | *Shipped, and **both of the questions slice 8a said were open were already answered in the ledger**. `B1` says "optional minimum order value per channel/outlet", which settles the scope — and settles the module with it: this is commercial policy keyed on a channel, like a price list, so it belongs in **Products**. Slice 8a's note guessed Configuration and was wrong; Configuration owns *shape* — custom fields, workflows, forms, weights, all tenant-wide — and putting it there would have meant building the channel/outlet override machinery a second time in a module that has none. Third rule in Products to take that shape, so the precedence a reader knows applies again. **A genuine spec conflict was found and named rather than resolved**: `BR-ORD-5` says "value/**qty**" and `B1` assumes value alone. Value ships, because it is what the ledger actually decided and a quantity minimum needs its own decision about what it counts — units, cases or lines, none of which is written down. **The minimum carries a currency**, which no other rule in this module needs: an order's currency comes from the list that priced it (`BR-ORD-7`), and comparing 50 EUR to 200 RON by their numbers would refuse orders comfortably over the threshold while looking like the rule working — so a mismatch is a refusal to *answer*, its own verdict beside `Met` and `NotMet`. **Zero is refused, which is the opposite call to a tax rate's**: 0.00 tax means zero-rated goods, a real commercial fact, while a minimum of zero means no minimum — already expressible by having no row, and two ways to say one thing is how a rep ends up reading "minimum: 0.00" and wondering what it is for. **No date window**, unlike every other rule here: nothing in `B1` or `ORD-06` asks for one, and inventing it is a field with no requirement and a migration to remove. Three sabotage passes. **Enforcement is 8b-ii's** — "must be met to submit" has to be answered at a counter with no signal, so the resolver is pure and both sides will run it* | | |
| 8b-ii | **The minimum reaches the device, and refuses** — the feed, the store, the TypeScript mirror, and the refusal a rep meets before the server does | `ORD-06`, `OFF-03` | 350 |
| | *Shipped, and it **found a live bug in the order screen that had nothing to do with order minimums**. `priced` took its lines from a sibling live query and listed the order's `updatedAtUtc` as a dependency, so every edit tore the subscription down and built a new one — and that re-subscribe intermittently produced an observable that never emitted, with no error to log, because `useLive` swallows one. A rep would have watched a priced line render as **"No price"** permanently while the store held the right number all along. The screen now reads the order inside the query, which is what `liveQuery` is for: Dexie sees the dependency on the `orders` table and re-runs it itself. It surfaced as a flaky new suite, was misread as test-environment noise, and splitting the file made it *look* fixed — recorded because that was the wrong conclusion twice before it was the right one. **The value measured is the order's net, and `BR-ORD-5` did not say**: tax is collected for the state rather than earned, but two mechanical reasons settle it — the device reads a missing rate as *unknown* and charges nothing (`PRD-07`), so a gross threshold would make the verdict depend on how far along a tenant's tax setup is, and `BR-ORD-6` re-prices on arrival, so a threshold moving with a recomputed VAT line is one a rep meets on the device and misses on the server. **This is the only rule in the module with no server-side gate, deliberately**: a rep who learns on sync that yesterday's order was too small cannot go back and add a case to it, so a refusal arriving after the visit is worse than none — the server still resolves the same minimum through the same pure rule, so the two never disagree about *which* threshold applies. **The tombstones matter more than the row count suggests**: the authoring PUT replaces the whole set, so every edit is a delete-and-recreate, and a device that only upserted would go on refusing orders against a withdrawn threshold — silently, and looking exactly like the rule working. **Not a `disabled` button**: a control that cannot be pressed says nothing about why, and this is a rule a rep can actually satisfy. Seven sabotage passes, and **one caught a vacuous test again** — the screen's precedence test survived collapsing the scope, because the two rows' ids broke the tie the right way by luck; renamed `z-…`/`a-…`, the same fix and the same mistake as slice 7d. **The pull fixtures were three hand-maintained copies of one list** and adding the fifteenth entity broke all three at once; one now builds on another. **No server backfill and no `upgrade()`** — `OrderMinimum` was born sync-tracked one slice ago, the first store where that is true. **The shared vector corpus is the debt this slice takes on**: the rule is implemented twice, which is exactly what `vectors/pricing/` exists for, and the tests here mirror the C# clause by clause instead* | | |
| 8b-iii | **The order-minimum screen** — where Sales Ops sets one | `ORD-06` | 250 |
| | *Shipped, and it closes the loop 8b-i opened: the server could hold a minimum and the device could refuse against one, but the only way to author it was a `PUT` by hand — so the rule a rep meets at a counter came from somewhere nobody could see. **A grid of channels with an amount each, not a list of rules to add**, which is what makes "no minimum" the default rather than a state to construct: `B1` sets a minimum per channel with a per-outlet override, so the channels are shown in full and the outlets are searched for, the same asymmetry and the same argument as `PriceListScope`. **Blank is how one is withdrawn**, with no delete button — the `PUT` replaces the whole set, so a cleared amount simply is not sent, and the server's refusal of zero is what stops "none" and "a minimum of nothing" becoming two states. **The currency is suggested from the tenant's price lists, and only when there is exactly one**: `BR-ORD-7` takes an order's currency from the list that priced it, so a mismatch is a refusal the **rep** meets, at a counter, about a misconfiguration they cannot fix — and a tenant pricing in several currencies has no single right answer, so the box asks rather than guessing, because a wrong suggestion would save without complaint. **A bug was found in a browser that no unit test could see, and it is the second slice running.** The editor was keyed on the query's `dataUpdatedAt` so it would reseed after a save — which meant *any* refetch remounted it, and React Query refetches on window focus. An author who alt-tabbed mid-edit came back to an empty screen: every amount typed, every outlet searched for and added, discarded without a word. It surfaced within a minute of driving the real screen, because reading the page refocuses the tab. Fixed by mounting once and letting `seeded` flow as a prop, which is also what makes the dirty comparison track the server rather than a snapshot — and it ships with the failing-first regression test, driven through the save because that is the same refetch. Three sabotage passes. **One found a test that proved less than it claimed**: swapping the shared `looksLikeAnAmount` for a naive `Number()` check left the `"12,50"` case green — `Number("12,50")` is `NaN` too — so the case proved only that *something* rejected malformed input. Adding `"1e3"` and `"Infinity"`, which `Number()` accepts, made it name which something. **And it corrected a wrong sentence in the helper's own comment**: `looksLikeAnAmount` claimed `" 12 "` was among the values a naive check would wrongly accept, when it trims and accepts it too — found by a test that asserted the comment and went red. Fourth instance of the same class this phase, and the first where the comment was in shared code the slice merely used* | | |
| 8c | ⚠︎ **The order that syncs before its visit** — a submitted order is refused, marked failed and never retried, while the shell says *Everything synced* | `OFF-04/05`, `ORD-07` | 350 |
| | *Shipped, and **two of the four candidate rules gave**. The drain holds a `CapturedOrder` until the visit it names has reached the server — checked out, and its own mutation gone from the outbox — and the indicator counts refused work as **needs attention**, ranked above offline because it wants a person rather than a connection. **Holding beat the alternatives on their own terms**: making the order refuse to seal before check-out would change what *submitted* means to a rep standing at a counter, and distinguishing retryable refusals needs the server to say which is which. The indicator was fixed regardless — calling refused work synced is wrong whatever the ordering does. **Held, not reordered inside the batch**: the server does apply a batch in array order, so sending the visit first would also work, and would put the rule in two places and make it a property of the wire rather than of the device. **A missing visit sends rather than holds** — the device cannot reason about one it does not hold, and holding forever is a worse failure than the one being fixed. **The starvation case is real and bounded**: a batch of a hundred held orders pushes nothing, which needs a rep to submit a hundred orders without checking out once. **The sabotage pass caught a vacuous pair of tests** — the two indicator tests had never been added at all, because a scripted patch matched nothing and the run still said "10 passed"; the same class of miss as slice 6's fake sabotage, on the test side rather than the source side, and the fix is the same: verify the file, not the exit code* | | |
| | ***Found in a browser, not by a test**, on the first end-to-end run of slices 7–8a against a real server (W11). A rep submits an order at the counter and checks out afterwards; `CapturedVisit` is only enqueued at check-out, so the order's outbox row is older and is pushed first — and the server rightly refuses an order for a visit it has never seen (`order.ingest.visitUnknown`). `markRejected` writes `failed`, nothing retries a failed row, and `pendingCount` counts only `pending`, so the indicator reads **Everything synced** over a dead order. The visit is accepted a moment later; the order never is; nobody is told. **No suite could see it**: the device tests mock the sync API, and every server test pushes a visit before an order because one that wanted the order to succeed had to — the seam is the *ordering between two mutations*, which neither has a place to express. Same two-suites-one-seam shape as W9's push property-name bug and 7a's float prices. **Four rules could give and the choice is a design decision**, which is why the slice exists instead of a patch: a dependency in the drain, an order that will not seal before check-out, a rejection that may become valid later being distinguished from one that never will, or all three — and separately the indicator must stop calling a failed mutation synced, which is a bug on its own terms* | | |
| 8d | ⚠︎ **The visit that is sealed before its work** — no offline order or audit could ever be accepted, and 8c had moved the failure rather than removed it | `OFF-04`, `BR-AUD-6`, `BR-VIS-4` | 250 |
| | ***Found in a browser while verifying slice 9a**, and it is 8c's bug wearing the other face. A pushed `CapturedVisit` is created **already checked out** (`Visit.Ingest`: "sealed on arrival") and a device only enqueues one **at** check-out — so offline work has no window at all: `UnknownVisit` before the visit lands, and `Sealed` after it. 8c held the order back until the visit had been accepted, which was right about the ordering and turned *refused-because-missing* into *refused-because-sealed*. **Reproduced with an order, not inferred from the audit**: a fresh visit, an order at the counter, check out, sync — `order.ingest.visitUnknown`, alongside the audit's `audit.ingest.visitSealed`. **Nothing could have caught it**: the two tests that covered this asserted the refusal while sending a capture time *before* the check-out, so they were describing the ordinary offline round and calling it the abuse case; the device suite mocks the sync API so it never meets a real refusal. Same two-suites-one-seam shape as 8c itself, W9's push property-name bug and 7a's float prices. **The rule was always about `capturedAtUtc`, not about a flag** — "work attached to a visit already filed as done" means work *taken* after the seal, and both timestamps come from the same device's clock, so the comparison holds on a phone that is wrong about the time. **`VisitFacts.WasOpenAt` is a fact, not a decision**, which is the line `IVisitContext` already drew: it answers "was this visit open then" and each module keeps its own refusal. **The boundary is inclusive** — an order sealed in the same second the rep checks out is the ordinary end of a call. Two sabotage passes, each caught by four tests in both directions* | | |
| 9 | **The audit capture screen** — MSL availability, facings and the category total, observed prices, survey answers. W10's deferral, into a shape that already ingests | `AUD-01/02/03/05`, `OFF-01b` | 450 |
| | *Decomposed on contact, the same way 8b was. One row hid four capture surfaces, a local store, a wire payload and a drain gate — `CapturedAudit` has the identical dependency on its visit that `CapturedOrder` does, so slice 8c's bug is waiting in it. Each of 9a–9c ends with something a rep can work and the server can read* | | |
| 9a | **The audit exists, and reaches the server** — the local store, `BR-AUD-1`'s availability over the MSL, and the drain gate 8c already argued for | `AUD-01`, `OFF-01b`, `OFF-04` | 400 |
| | *Shipped, and **driving it is what found slice 8d** — until that landed no audit this screen produced could be accepted, and neither could any order. **The gate 8c argued for was extended rather than rediscovered**: `CapturedAudit` has the identical dependency on its visit that `CapturedOrder` does, so the drain now names the dependent types in a list. **The list is the MSL, not the assortment** (`BR-AUD-1`): a shop may be allowed to sell a hundred products and be required to stock twenty, and auditing the wider set would score a shop against a list it never agreed to. **Three answers, not two** — *absent* is a listing the shop never took and *out of stock* is one it cannot keep filled; they look identical from the aisle and mean opposite things, and collapsing them is most of what the availability pillar is for. **Tapping the chosen answer again un-answers the line**, because all three are assertions about the shelf and a rep who tapped the wrong row needs a way back to having said nothing. **The draft is created by the first answer, not by opening the screen** — a rep called away leaves nothing behind for `BR-AUD-6`'s one-per-visit to trip over. **The weighting is fixed when the draft starts** (`BR-AUD-8`), so a re-weighting that syncs mid-audit cannot restate what the rep was shown; a tenant that has published none is refused **before** the first tap rather than after the whole shelf is walked. Four sabotage passes. **Verified end to end in a browser**: v12 upgraded a real device in place, the draft appeared on the first tap with the weight version recorded, the seal produced the wire shape the server reads, the gate held the audit through a full sync at `attempts: 0`, and after check-out it was accepted* | | |
| 9b | **The numbers on the shelf** — facings, `BR-AUD-2`'s category total, and observed prices against what the device resolved | `AUD-02`, `AUD-03` | 400 |
| | *Shipped. **The first `upgrade()` on a store this device authors** — versions 5, 7, 9, 11 and 12 all added tables, and this adds three fields to rows that already exist. What makes it a version rather than a reader's default is `captured()`: `CapturedAudit` takes `facings` and `prices` as **required** lists, so a draft sealed with them `undefined` would send JSON missing two properties and be refused as a 400 that retries forever. **`categoryFacings` is null and never zero**, which is `BR-AUD-2`'s distinction: null skips the share-of-shelf pillar and the score renormalises over what was measured, while a zero says the shop stocks none of the category. **Prices cross as integer minor units, rounded half-up first** — `4.795` is `480`, and multiplying before rounding gives `479.5`, which `long` truncates to `479` silently and in the shop's favour. **The expected price is stored beside the observation**, resolved once when the screen loaded: `BR-AUD-3` judges against the price for that outlet and date, and a list republished mid-audit would otherwise move the number the rep is measured by. It is **shown, never pre-filled** — pre-filling would make "the rep confirmed it" and "the rep did not look" the same record. **Sealing now accepts any of the three**, where 9a required an availability answer: facings and prices are pillars in their own right, though a category total alone still refuses, being a denominator with no numerator. **Two real bugs came out of the tests, both about typing.** A field that fires per keystroke passes through `4.` on the way to `4.79`, and treating that as "clear" wiped the rep's own reading — only an empty box is an instruction now. And the per-keystroke writes **raced**: `4.7` landed after `4.79` and stood. Fixed by chaining every write through one queue rather than holding the text in React state, because this screen promises `OFF-01b` — each measurement durable as it is made — which the order screen's blur-then-write does not. Four sabotage passes; one showed a test asserting a *default* rather than a behaviour, so it now types a total and clears it* | | |
| 9c | **The questionnaire at the shelf** — the survey answers and `BR-AUD-7`'s gate, on the device where the rule lives | `AUD-04`, `BR-AUD-7` | 350 |
| | *Shipped. **The gate was written twice and wrong both times, in the same way**: it read the form the *audit names* rather than the one the *screen is showing*. An audit names no form until the rep answers something — so the one rep `BR-AUD-7` exists for, who scrolled past the questionnaire and answered nothing at all, was precisely the one both layers excused. The screen's version and the store's `unanswered` each carried a `surveyFormId === null` short-circuit; the fix is one `workingForm` resolving the form for both the section that renders the questions and the seal that gates on them, and a store rule that judges whatever questions it is handed. **A screen test found it, not a unit test** — the store's own suite passed the whole time, because every test that called `unanswered` had chosen a form first. **Which form applies is a gap in the model, stated rather than papered over**: a workflow step carries a type and a label and no form id, so one form is used without asking, several are offered, none says nothing — and until the rep picks from several, the rule has nothing to gate on. A form-per-channel configuration would close it, and it is Configuration's to make. **An answer carries the question's text**, so it survives the form being re-worded, and **the form and answers cross both-or-neither** — `CapturedAudit` takes `answers` as a required list, which is what makes this a Dexie version rather than a reader's default. **`measured()` now counts answers**, so an audit that is only a questionnaire is a real audit and finishable. Nine sabotage passes, every one caught. **Two more found in the browser**: the auto-chosen form did not appear until the rep touched the shelf (`audit?.surveyFormId === null` is `false` when there is no audit at all — so a rep working the fridge first had no questions and no way to make any), and the refusal *outlived its cause* — "answer the questions listed above" sat there after the rep answered, pointing at a list that had just vanished. **And a pre-existing flake**: seven tests waited on `db.audits.count()` and then grabbed the Finish button synchronously, one beat before the live query had rendered it. Verified end to end: authored in the back office, pulled, answered at the shelf, refused with the question named, answered, sealed, pushed, accepted — the answers read back out of `audit.audit_survey_answer` with their question text* | | |
| 10 | **The score at the shelf** — W10 slice 5's TypeScript engine, rendered while the rep is still standing there | `AUD-06` | 250 |
| | *Shipped, and the engine needed no changes — this is an **adapter and a section**. The adapter is where the risk was: `scoreInputsFor` converts prices through the *same* `minorUnits` the wire uses, because the server scores the entries it stored, and a device scoring the draft's decimal strings would round in a different place and contradict the back office. **The weighting is read by version, never "the newest"** (`BR-AUD-8`) — `scoreWeightSet`, not `currentScoreWeightSet`. **A weighting naming a pillar this build cannot compute is not scored at all**: dropping the unknown pillar would change the denominator and produce a confident number the server contradicts, and a pillar added server-side reaches devices before a build that understands it. **The availability denominator is the lines the rep answered, not the MSL** — one product answered and found reads 100%, which overstates a shelf nobody finished walking and is exactly what the server does; diverging to be more useful would break `BR-AUD-5`. Eight sabotage passes, all caught — two only after the tests were strengthened: the `BR-AUD-8` test added the second weighting *after* asserting, so it passed with the lookup swapped, and the minor-units test used `4.795`, which a hard-coded `×100` scores identically by luck. It now uses **yen**, which has no minor unit at all. **A real race came out of the suite**: sealing checked `measured()` on the *prop*, and the first tap on a shelf is two writes — draft, then answer — so a rep who tapped through the gap was told to check a product they had just checked. Full runs went from four failures in six to none in six; the regression test stages the gap with a native `.click()`, because `userEvent` flushes the live query first and would pass either way. **Verified end to end**: 100% → 70% → 56% as the rep measured, each renormalising over the pillars actually captured, and the server stored **56.00** with the identical three pillars* | | |
| 11 | **Photos: capture and downscale** — ~1600px / JPEG ~0.7, and the `blobs` store W8 deliberately did not create because it had no writer | `OFF-08`, `B5` | 350 |
| | *Shipped. **The store arrives with its writer**, which is why W8 left it out: a table nobody fills is a schema claim nobody can check, and its shape would have been guessed a phase early. **Two rows in one transaction** — the audit's reference and the blob — because a reference with no image can never be uploaded and an image with no reference is one nothing will ever ask for; removing a photograph removes both, or the device pays to upload bytes no supervisor can reach. **The key is minted on the device**, like the audit's id, so the reference and the upload agree without a round trip. **`measured()` now counts photographs**, because `Audit.Check` does — a shop that will not let a rep count the shelf still lets them photograph the display, and a device that refused that audit would refuse one the server takes. That is the third slice running where the device's idea of "empty" had to be widened to match the server's, which is worth noticing as a pattern rather than fixing a third time. **The seam is `fitWithin`**: jsdom implements no canvas, so the encode cannot be unit-tested at all — the arithmetic is pure and tested, the `drawImage`/`toBlob` around it is thin and verified in a browser, and the test file says so rather than asserting against a stub. Twelve sabotage passes, all caught. **A crash found by the tests**: `URL.createObjectURL` throws on anything that is not a real `Blob`, and an unreadable thumbnail took the whole audit screen down with it — the shelf, the questionnaire and the score. It now renders the row without its picture, because the rep still needs the button that removes it. Also **an exhaustive payload test earned its keep**: `toEqual` on the whole push object caught `photos` being added, which a partial assertion would have waved through* | | |
| 12 | **Photos: presigned upload, retried on its own schedule** — a second transport, independent of the JSON push | `OFF-08` | 400 |
| | *Split on contact, and this time **before** writing it rather than after: the three slices before this one ran 1.5–3× over budget, and 12 as planned is a storage resource, an endpoint, a device uploader and a retry schedule. **12a is the server half**, 12b the device's. Both halves of the decision were escalated rather than assumed — object storage is infrastructure and minting a URL is an authorisation rule, and `CLAUDE.md` says stop and ask for either* | | |
| 12a | **Somewhere to put a photograph** — Azurite/Blob, and a short-lived write-only URL the server scopes to the caller's tenant | `OFF-08`, `B5` | 250 |
| | *Shipped. **The tenant prefix is the server's to write, never the caller's to send** — the device asks for `audits/{auditId}/{photoId}.jpg` and the API stores it under `{tenantId}/…` from the validated token. That is the whole of the isolation: there is no request a rep can craft that produces a key outside their own tenant, because they never get to spell one. The key is anchored to two GUIDs and a `.jpg`, so traversal and absolute paths are refused rather than sanitised. **The SAS is write-only and names one blob** — not the container, which would let a device that obtained one overwrite an audit already filed — and lasts fifteen minutes, which is generous for a 22 KB JPEG and cheap to re-ask for. **It deliberately does not check the audit exists**: the push and the upload are independent transports and either can win (`B5`), so refusing a photograph whose audit has not landed would fail the exact case the split exists for. The cost is bounded and stated — a rep can mint a URL for an invented audit id and write an unreferenced JPEG in their own tenant. **Signing branches on the credential**: an account key in development, a user delegation key under managed identity, so production needs no signing secret at all. **Tested against real Azurite rather than a double**, because the feature *is* the signature — the decisive pair is that the URL accepts a `PUT` and is refused a `GET`. That immediately earned itself: SDK 12.28 speaks REST `2026-04-06` and Azurite 3.35 answers `400`, which no fake would have shown; the emulator is told to skip the check rather than pinning the shipped client down to the emulator's pace. **`IClock` again** — the gate banned `DateTimeOffset.UtcNow` for the SAS expiry in both the module and the test, and was right both times: an expiry is a decision about *when*. **`confirm` and the missing-blob flag are not here** — the sync engine's §5 includes them, but confirm's only caller is the uploader and the flag is an Audit migration; they belong to 12b/13 rather than to a slice that would have overrun again* | | |
| 12b | **The device uploads on its own schedule** — the second transport, its retries, and its place in the sync run | `OFF-08` | 300 |
| | *Shipped. **Photographs go last on a sync run** — push, pull, then images — because a JPEG is twenty times a visit's JSON and the reference data a rep needs for the *next* shop is worth more than the picture of the last one. **It runs even when the pull was interrupted**, since the two transports fail for different reasons, and it does not clear `interrupted`. **Only a sealed audit's photographs are sent**: a draft's are still the rep's to remove, and uploading one spends their data on an image that may be deleted a minute later. **Serially and oldest first**, because the connection is what the rep is short of. **Each photograph carries its own failure count** and stops being retried after eight — kept, never deleted, because it is the only copy; the count clears on success so a bad morning does not follow it forever. **Dexie version 16 indexes `uploadedAtUtc`**, and stores waiting as the **empty string rather than null** because IndexedDB will not index null — the type says `string` rather than claiming a null it never holds, which is the lie the first draft told. **The bytes are kept after upload** so a rep can still see what they photographed; pruning is `OFF-11`'s. Ten sabotage passes, all caught. **Two self-inflicted findings**: the `split/join` no-op trap struck again — a scripted edit silently changed nothing and two tests failed against a mock that was never applied, which cost three probe cycles to see; and the first fixture failed because the drain gate correctly held an audit whose visit the test never created. **`confirm` and the missing-blob flag are still not built** — the device knows what it uploaded, the server is not told, and nothing reconciles a reference whose object never arrives. That is slice 13* | | |
| 12c | **The upload the browser refused** — the three walls that made `OFF-08` impossible, and the silence that hid them | `OFF-08`, `OFF-09` | 250 |
| | *Unplanned, and the reason browser checks are in the working agreement. **12b shipped an upload path that could never work**: `B5` sends photographs straight to object storage on a presigned URL, object storage is a different origin, and `connect-src` named only this app and Keycloak — so the browser refused every `PUT` before a byte left the device. The presign succeeded, the upload never happened, and the uploader's own retry made it look like a bad network **forever**. It shipped through two green suites, ten sabotage passes and CI. **Neither suite could have caught it**: the device tests mock `fetch`, and 12a's tests PUT from .NET where there is no CSP — the failure lives exactly in the gap between them, which is the same shape as W9's push property-name bug and 7a's float prices. Fixed by naming the storage origin in `connect-src` from configuration: the AppHost hands the front end a **URL** while the server keeps the connection string, because a browser needs an origin and has no business holding an account key. **And then it failed twice more, each time one layer further in** — which is the finding worth keeping, because each wall was only reachable once the one before it came down, so no single fix could have been verified as "done" without going back to the browser. **Second: `localhost` and `127.0.0.1` are two origins.** The presigned URL comes back on one while the page is served from the other, so the policy that now named storage still did not name *that* storage; development also allows the origin's loopback sibling. **Third: object storage does not accept a browser by default.** `x-ms-blob-type` makes the `PUT` non-simple, so a preflight `OPTIONS` goes first and storage answers only if a CORS rule names the caller — the API now applies one at startup from `FIELDKIT_WEB_ORIGIN` (`PUT` and `OPTIONS`, never `GET`, which would undo the write-only SAS from a direction the signature does not control; replaced rather than appended, so restarts do not grow the list). Verified the only way it could be: an actual photograph, from a sealed audit, `OPTIONS 200` then `PUT 201`, 22,472 bytes on Azurite's disk. **And a fourth, caught by CI rather than a browser** — published, `storage` is a bicep account with no endpoint a container app can be pointed at, so the manifest job failed where every test passed; the front end takes the account's connection string there, which under managed identity *is* the service URI and carries no key, and the emulator's endpoint in development, where the connection string very much does. **Underneath all three was the silence.** The uploader caught every failure and recorded only that there had been one, so a policy refusing every request was indistinguishable from a weak signal; `lastFailure` (version 17) now keeps the reason, which is what slice 13 needs to tell a rep anything true. **An existing exhaustive assertion earned itself again** — `toEqual` on the whole `connect-src` list failed the moment a source was added, which is the third time this session that style has caught something a `toContain` would have waved through* | | |
| 12d | **Two rights nobody needed** — the container the app made for itself, and the account access the front end was handed | `OFF-08`, `B5` | 150 |
| | *Shipped, and it started as a question about one line: why is the container created at runtime? **It was, on every presign** — `CreateIfNotExists` before signing anything, which meant the identity that mints write-only single-blob URLs also had to hold the right to create containers, and paid a round trip for it. The comment above it said "the container the AppHost creates" and **the AppHost did no such thing**, which is the same wrong-comment pattern this session keeps finding. Now declared: bicep when published, Aspire's emulator hook in development — verified by declaring a name that had never existed and watching it appear in Azurite, because the dev volume persists and "it works" proved nothing. **Then the manifest showed something worse.** 12c gave the front end the storage origin by passing the resource to `WithEnvironment`, which reads as harmless and is not: a reference earns Aspire's default grant, and the published front-end identity came away with Storage **Blob, Table and Queue** Data Contributor over the whole account — to obtain a string. Confirmed as a 12c regression by generating the manifest at 12b, where the role module does not exist. It takes the bicep output now, with an empty role set, and naming the output alone was **not** enough — the reference is what earns the grant, so the empty declaration is doing the work. **Both are held by the deploy gate rather than a test**: the container's name must match `BlobPhotoStorage.ContainerName`, which the AppHost cannot reference, and the front end's role module must grant nothing. Four sabotage passes, all caught — drop the declaration, rename the container, restore the default grant, and remove the fixture's container so the upload test fails with nothing to write to. **The honest cost**: no test in the suite would now notice the AppHost dropping the declaration, and a container deleted in production heals on the next deploy rather than the next presign* | | |
| 13 | **Two truths a rep can see** — *synced* and *uploaded* are different, and a half-uploaded visit must not read as finished | `OFF-05/06/08` | 250 |
| | *Split before writing it, the habit 12 taught: 13 as planned is a module contract, a migration, an endpoint, a device call and a screen. **13a is the server half**, 13b the rep's. The contract shape and the missing-blob rule were both escalated rather than assumed — `CLAUDE.md` says stop and ask for a public module contract, and "when is a photograph missing" is a product judgement, not an implementation detail* | | |
| 13a | **The server is told the bytes arrived** — `confirm`, and *missing* as a question rather than a flag | `OFF-08`, `B5` | 250 |
| | *Shipped. **The server never sees the `PUT`** — it signs a URL and forgets, and the bytes go browser to storage — so until this existed, a reference to a photograph still on a phone and one that is never coming were the same row forever. That is the whole reason *synced* and *uploaded* could not be told apart. **`IPhotoEvidence` is a second write contract on Audit**, deliberately not a method on `IAuditIngest`: a caller that only wants to say "the bytes are there" should not thereby acquire the ability to file audits, which is the same split `IAuditQuery` makes on the read side. **Counts, never refusals** — a key naming no reference yet answers `unknown` and the batch succeeds, because the upload can beat the push and confirming a photograph whose audit is still in the device's outbox is the case `B5` exists for. **The first confirmation wins**; a repeat leaves the timestamp, so a rep driving through a tunnel does not make the evidence look like it arrived later. **Missing is derived on read**, against the audit's age and a one-week threshold, rather than stored: a flag needs a job to set it and a second rule to un-set it when a rep finds signal on Monday for Friday's photograph — and that rep is precisely who this must work for. **The cross-tenant test could not be sabotaged**, which is itself the finding: removing the filter means `IgnoreQueryFilters` and the architecture gate refuses to compile it (RS0030), so the test was strengthened instead to confirm the same key as the owner afterwards — only the tenant differed, so only the tenant explains the refusal. Two other sabotage passes caught, and one API design call worth naming: `Describe` now takes `now`, because photo state is the first thing a reader sees that is not stored* | | |
| 13b | **Two truths a rep can see** — the badge, the indicator, and the device call that feeds them | `OFF-05/06/08` | 200 |
| | *Shipped. **The chip said "Everything synced" over a photograph still on the phone**, because it read the outbox and the photographs travel on their own transport — the same shape as W11 slice 8c's refused-order bug, in the same component, found by asking what the words mean rather than by a failure. **`confirmedAtUtc` is a second state next to `uploadedAtUtc`** (version 18) because uploading and being *known* to have uploaded are different facts: the bytes go to a presigned URL the API never sees used, so an acknowledgement that never got through leaves the back office expecting a photograph forever. **One key per confirm call, though the endpoint takes a list** — the reply is counts, so a batch with one `unknown` cannot say which, and because an already-confirmed key also answers `confirmed: 0` that batch could never settle and would re-confirm everything in it on every run, forever. That was a design bug caught while writing the client, not by a test. **`storedKey` is kept** because the tenant prefix comes back from presign and the device cannot rebuild it. **`unknown` is not a failure** — it means the audit has not been pushed yet, and counting it as one would put *needs attention* in front of a rep for something that fixes itself. **Photographs rank below unsent work and above synced**, since a queued visit is the work itself and the pictures are evidence about work already delivered. **The count ignores drafts**, or the indicator would say *photos still to send* for as long as an audit is open, which is most of a rep's day. Five sabotage passes, all caught — and **the fifth found a real gap**: judging evidence by `uploadedAtUtc` instead of `confirmedAtUtc` failed only one uploader test, because no badge case covered uploaded-but-unacknowledged, which is the distinction the whole slice is about. That test now exists. **The migration tells one small lie deliberately**: photographs uploaded before version 18 are marked confirmed, because their `storedKey` was never kept and they can never be confirmed — the alternative is a row retrying a call it has no arguments for on every sync, forever, and the cost is that the server reads them as missing after a week* | | |
| 14 | **As-of-capture, and a flag that is not a silent change** — the server re-prices, disagrees, and says so | `ORD-08`, `BR-ORD-6`, `B7` | 350 |
| | *Shipped, **as one PR and over budget**, which was asked for after the split was offered — worth recording that way round rather than as an accident. It carries three things: the missing tax field, the snapshot capture, and the re-price. **The tax field was a debt three slices old** — `lineTotal` is the net and `CapturedOrderLine` had nowhere else to put anything, so the back office received every order short of its VAT; the screen had been showing a gross it then threw away. It waited for this slice because it only earns its keep beside the comparison that needs it: the server's recomputation includes tax, and without the field it would have been measured against a number that never did. **The snapshot is the device's six pricing watermarks**, not the pull's `snapshotVersion` — that string names a timestamp and the outlet cursor and its own comment says the device must not parse it, so it can report that a disagreement happened and nothing about why. Six numbers because the sync engine calls its own snapshot "a patchwork, not a point in time". **The re-price runs as of the day the order was taken**, which is the entire reason `IPricingService` takes a date and refuses to read a clock — a server using *today* would report an ordinary mid-week price rise as the rep having got it wrong, on every order captured before it. That is the one sabotage nothing else would have caught. **An unpriced line means no annotation at all**: totalling around it would compare the device's whole order against the server's partial one and report a difference the exact size of the missing line, which reads as "the rep overcharged" and is nothing of the kind. **The agreement is derived, never stored** — a stored flag can contradict the numbers beside it. Four server sabotages and one device sabotage, all caught; the `Total = net` one is the important one, because it is the failure the whole rule exists to prevent and the test names it. **A test premise was wrong before the code was**: the "agrees" cases sent tax the fixtures had no rate for, so the server got zero and flagged a disagreement correctly — the fix was to the test, plus a new case where the tax alone differs, which is what proves tax is in the comparison at all* | | |

**This is two weeks, not 1.5, and the row's own ⚠︎ was closer than the estimate.** Sixteen slices at
~5,400 lines against a ~400-line PR budget. The fault line is **after slice 8**: orders exist end to
end, server and device, and a rep can place one offline. Slices 9–14 are a different subject — the
audit screen, a binary transport, and the conflict rule that needs both. Splitting there gives two
weeks that are each demonstrable, rather than one that is neither.

**Not in W11.** `ORD-09` (back-office accept/reject) is `Could`/Phase 4 — but `ORD-12` is a `Must`
and needs *something* to do the rejecting, so **rejection ships as an API with no screen**, driven in
the demo by an `.http` request. Saying that out loud because an endpoint with no caller is exactly
what the Phase 1 review found five of. `ORD-04` (suggested list) and `ORD-13` (custom fields on
orders) are `Should`s deferred to keep the split above honest; `ORD-13` in particular is the
custom-field catalogue a fourth time and carries no new decision. `ORD-15` (block submission on
credit hold) needs an **order-hold flag on the outlet that does not exist** — an Outlets-module
change, not an Order one, and naming that dependency is cheaper than discovering it mid-slice.
`ORD-10`/`ORD-11` stay `Could`/`Won't`. Background sync (`OFF-07`) is Phase 3's own thing and a third
transport.

### Week 11½ · Regression remediation
**Goal:** clear what the [post-W11 regression](engineering/regression-2026-08-13.md) found, before
W12 builds a dashboard on top of it.

Seven findings, none of them a break in shipped behaviour. What makes them worth a week's slot rather
than a backlog is that **two of them are load-bearing for W12**: the demo is the full loop, and a rep
who cannot start a call cannot walk it (R4), while a dashboard that reports order values inherits
whatever the two sides disagree about (R6).

**Done when:** a rep with no planned call can work a shop; a refused mutation says why; and the
device and the server agree what day it is.

#### Decomposition

**The ordering is by dependency, not by size.** R1–R3 are minutes each and clear the noise so the two
real slices are reviewed on their own. R4 comes before R6 because it is what makes R6 testable by
hand — pricing across a day boundary needs a call you can actually start.

**Two of these are the same shape as the bugs W11 spent itself on**: a value written and never read
(R2), and two implementations of one rule with nothing comparing them (R6). That is not a coincidence
worth restating in every row, so it is said once here.

| # | Slice | Requirements | ~Size |
|---|---|---|---|
| R1 | **The registry catches up** — `IOrderMinimumChangeFeed` joins the module registry (regression F2) | — | 10 → 200 |
| | *One table row. It is here rather than folded into another slice because the registry is a deliverable, and a doc fix that rides along with code is a doc fix nobody reviews.* | | |
| | *Shipped at twenty times the estimate, and the overrun is the finding. **The regression checked one direction only** — built contracts against the table — and the table's own convention is that **bold means built, plain means planned**. Checking the other direction found three built contracts still shown as planned: `IReferenceChangeFeed`, `IAssortmentService` and `IPricingService`, the last two of which Order already consumes. Understating is the mirror of the error the pre-Phase-2 audit fixed and misleads the same reader the opposite way — they conclude they may not depend on something that shipped weeks ago. **So the slice grew a gate.** `ModuleRegistryTests` checks both directions on every build; the registry had now drifted twice, and a convention only a person can enforce decays between audits. **Its first draft passed vacuously** — `AppDomain.GetAssemblies()` returns only what has been touched, so the built set came back empty and "every built contract is listed" was checked against nothing. Its sibling caught it by reporting all thirty-three entries as unbuilt, and the fix loads the contracts assemblies from the output directory and asserts the set is non-empty before asserting anything about it. Three sabotage passes, all caught with the message that names the offending contract* | | |
| R2 | **Three tests stop racing** — wait for the DOM, not the store (regression F4) | — | 20 |
| | *`audit.test.tsx:627`, `:751` and `order-minimum.test.tsx:220` wait on `db.outbox.count()` and then assert on the screen. The store write and the re-render are two moments; the gap is what failed CI during slice 14. Third sighting of the pattern, so this slice also adds the helper — `expectEventually` or equivalent — rather than a fourth hand-rolled `waitFor`.* | | |
| R3 | **A rep can tell two Marias apart** — the picker shows the email beside the name (regression F5) | `JRN-03` | 30 → 120 |
| | *`UserResponse` already carries `Email`; the picker renders `displayName` alone. One line of JSX and a test that two candidates with the same name are distinguishable.* | | |
| | *Three pickers, not one. The regression named the journey one; `assignment-form.tsx` and `working-calendars.tsx` have the same line, and the choice they make is a territory and a working week. Fixing the named one only would have left the finding true in two places. **The label is a helper, not a message key** — both catalogues would carry the identical `{name} — {email}`, and a key that never differs between locales is one that drifts out of a locale; an `<option>` holds no elements, so "secondary text" is not on offer inside a `<select>` and the separator does the work. **The slice's real finding is what it nearly broke:** two existing tests asserted `option.textContent` equals a bare name, including two `not.toContain` exclusions. With the email appended, those exclusions would have passed for the wrong reason forever — no option's text is a bare name any more, offered or not. Both now assert on `option.value`, the subject id, which is what "offered" actually means. Sabotage: reverting the helper to the name alone fails three tests and leaves those two green, which is the intended split.* | | |
| R4 | **A call you can start anywhere** — the device half of the unplanned visit (regression F7) | `JRN-06`, `BR-JRN-4` | 300 → 400 |
| | *The finding: `JRN-06` is a Phase-2 **Must** that names the unplanned visit, and every layer is built except the one that starts it — `AddUnplannedAsync`, `TryAddUnplanned`, the `unplanned` wire slot, the sync manager's mapping and the back office's badge all exist, while the only mention of `UnplannedCall` in the front end is the slot mapping. A route for a mutation the device cannot produce. **What it needs:** an `addUnplanned` writer in `lib/visits/` beside `markNotVisited` (its closest sibling, 92 lines), an entry point on the journey screen that lists in-scope outlets, and the check-in path reached from it. **Scope discipline:** an unplanned call belongs to no cycle and cannot be rescheduled — the journey spec settles that, so this slice does not reopen it. **It also unblocks manual testing**: without it, a rep with no planned call has Sync now and This device and nothing else, which is why the regression's rep-side sweep could not reach check-in, the audit or order capture.* | | |
| | *Shipped. **Not a deferral** — the plan's W7 slice 5 row carried no note and `JRN-06` is a Must, so the device half was never built. **The design question the slice actually had was *when* to queue it**: on the tap that picks the shop, or at check-in. Check-in, because that is the moment the call is a fact — queuing from the picker would tell a supervisor a call happened at every shop a rep opened and thought better of, and coverage is a number supervisors act on. **The date is `todayOn`, not `toISOString`**, and the test pins it at 00:30 Bucharest — 16 March at Greenwich, 17 March to the rep — because the wrong one dates the call to a day the published round may not cover, which the server answers `NoPlanForDate` to. **One existing test had to be narrowed rather than relaxed**: "captures nothing in the outbox" was broader than its own claim once a second, unrelated mutation could be queued at check-in, so it now names `CapturedVisit`. **Reading the code to build it found [F8](engineering/regression-2026-08-13.md)** — a second call at a worked shop is routed with the planned call id, contradicting `destinationOf`'s own comment — left out of this slice on purpose, since it changes the planned path and this one was already at budget.* | | |
| | *Walking it in a browser found **[F9](engineering/regression-2026-08-13.md)**, which qualifies what this slice achieved: the server refuses an unplanned call with `journey.plan.noneForDate` unless a **published round covers the day**. The visit is captured and reaches the back office either way — so check-in, the audit and order capture are genuinely unblocked for a rep with no plan, which was the point — but the call does not join a round, and with **F1** still open the rep sees *Needs attention* and no reason, while the outbox holds the sentence unread. **That is F1 observed rather than reasoned about, and it makes R5 the next slice by argument as well as by order.** Third slice running where the browser found what neither suite could.* | | |
| R5 | **A refusal says what it was** — the offline path renders the code it already stores (regression F1) | `OFF-09`, [ADR-0012](architecture/adr/0012-server-message-localization.md) | 150 → 260 |
| | *`markRejected` stores `errorCode` and `errorDetail` under a comment saying the UI translates them, and nothing reads either — six references in the whole front end, all of them the declaration or the write. A rep whose order was refused sees **"Needs attention"** and cannot find out why, on the one surface where it matters: the work is done and only a person can unstick it. **`refusalText` already exists** and does exactly this on the back-office path, with an English fallback by design — so this is wiring, not new machinery, and the `Refusals` catalogue's four entries do not have to grow first. **Where it surfaces** is the design question: the badge is an annotation and has no room for a sentence, so the reason belongs on the visit or order the badge is attached to.* | | |
| | *Shipped as wiring, as planned — a `refusalOf` reader, a `storedRefusalText` beside `refusalText`, a `RefusedReason` beside every `SyncBadge`, and the catalogue untouched. **The overrun is one discovery.** `refusalText` is safe because the server's `args` travel with the problem; `markRejected` never stored any, and `t.has` cannot tell an entry with placeholders from one without. **`next-intl` does not throw on a missing ICU value — it returns the key path** — so the obvious `try`/`catch` is not a guard at all, and a rep would have been shown `Refusals.journey.plan.windowTooLong`: the exact failure ADR-0012 exists to prevent, reintroduced by the slice meant to honour it. Caught by a test written expecting a throw, which failed with the raw key instead. The template is inspected for a brace now. **Two surfaces**: the round's stop rows, and the unplanned picker — which is the **only** place an unplanned call's refusal can appear, since it is queued under the shop and the round has no row for a shop it never planned. That is [F9](engineering/regression-2026-08-13.md)'s live case, now answered.* | | |
| R6 | **One answer to "what day is it"** — the outlet's zone decides, on both sides (regression F6) | `BR-PRD-6`, `ORD-08` | 350 |
| | *Bigger than the regression wrote it up, and the write-up understated it. The **device** prices against its local day (`businessDay` in `order.tsx`/`audit.tsx` reads `getFullYear/getMonth/getDate`); the **server** re-prices against the UTC date. For a rep in Bucharest before 03:00 those are different days, so slice 14's comparison will flag a disagreement the rep did nothing to cause. **The fix is the outlet's own zone, used by both**: `OutletSnapshot` grows `TimeZoneId` (a public contract change — escalate), the device stores it (local store version 20) and dates its pricing by it, and the re-price resolves the same value server-side. **`businessDay` is duplicated** in `order.tsx:701` and `audit.tsx:1340`; this slice collapses it into one function that takes a zone. **Vectors:** the day-boundary rule is exactly the kind of thing the parity corpus exists for, and a small vector file is cheaper than discovering the two sides disagree in production.* | | |
| | ***Written up in full before starting** — [r6-business-day.md](engineering/r6-business-day.md) — because it needs two decisions that are not an implementer's to take: widening `OutletSnapshot`, and how Order learns an outlet's zone at all (it has no dependency on Outlets today). Reading the code for that write-up moved two things. **`Outlet.TimeZoneId` already exists** — required, IANA, validated, populated, and its W1 doc comment already names `BR-PRD-6` as the reason — so there is **no migration and no new admin field**; what is missing is only plumbing. And there are **two day-rules, not one**: `todayOn` is the *rep's* day and is correct for the round and for R4's unplanned call, while `businessDay` is the *shop's* day and is the only one that moves. A fix applied mechanically would have merged them.* | | |
| | *Shipped as **R6a** (the zone reaches the device; no behaviour change) and **R6b** (both sides date by it). `IOutletCalendar` is a **new narrow contract** rather than a wider `OutletSummary`, on the test `10-module-boundaries.md` now records: not "is this a fact about the outlet" but "do the existing consumers of that record decide with it" — channel, country and segment passed, a time zone does not. It returns the **day**, so Order never learns what a time zone is. **`BusinessDay` lives in SharedKernel beside `Money`**, because a rule with a mirror belongs where the mirror can be held to it: `vectors/pricing/business-day.v1.json` is the first vector file whose two engines share **no library at all** — `TimeZoneInfo` against `Intl` — and all fifteen cases agreed first time, which is worth recording as plainly as R7's catch. **The R1 registry gate caught the new contract** before the doc did. **Two guards were corrected after measuring**: the empty-string zone throws on both runtimes, so its guard is unfalsifiable — what is load-bearing is the neighbouring value that does *not* throw (`undefined` in `Intl` silently uses the host zone; `null` in .NET throws past the narrow catch), and the comments now say that rather than overselling.* | | |
| R7 | **The rule both sides share** — an order-minimum vector corpus (regression F3) | `BR-ORD-5`, `PRD-08` | 200 |
| | *`OrderMinimumResolver` and `lib/pricing/order-minimum.ts` resolve the same rule independently and nothing compares them. `BR-ORD-5` is the only rule in the module with **no server-side gate** — the device is where it can still be acted on — which makes agreement more important than usual, not less: nothing downstream will catch a divergence, because nothing downstream checks. A `vectors/pricing/order-minimum.v1.json` and a reader on each side, matching the five that already exist.* | | |
| | *Shipped, and **the file found a divergence on its first run** — which is the only evidence a vector file is worth anything. The two engines agreed about everything the rule is *about*: precedence, ties, the comparison, the currency refusal. They disagreed about **what counts as a number**. `.NET` parses the stored amount with `AllowDecimalPoint \| AllowLeadingSign`, which excludes exponents and hexadecimal; `decimal.js` reads `"1e2"` as 100 and `"0x10"` as 16, so a phone would have called an order **Met** against a minimum the server cannot read — with no server-side gate to catch it. **Unreachable today**, because the write path validates with the identical styles; that is the point rather than a mitigation, since the agreement was inherited from two validators that happen to match and nothing recorded it. The device now refuses the same shapes and takes the stricter side deliberately: `Unreadable` stops a submission, `Met` lets one through. **The lesson for the next file** is in `vectors/README.md`: every hand-written case about the rule itself passed on both sides first time, and what diverged was the handling of input the rule was never meant to receive. 24 C# cases, 34 TypeScript; the reader gate now counts 13 shared files.* | | |

**Not in W11½.** The four **non-findings** the regression recorded stay recorded and unfixed on
purpose — storage rounding to whole megabytes, no service worker under `next dev`, shops with no
workflow, and the duplicate seed users. Each looks like a defect and is not, and the regression
document explains why so nobody re-opens them.

**Two process items, sized here because they cost the next pass more than they cost now.**

- ~~**A published plan for today in the dev seed.**~~ **Done in W12** — `JourneyRoundSeeder`, and it
  cost more than the ~60 lines estimated here. It needed a tenant seam (`TenantScope`) before a
  hosted service could reach `IRepScope` at all, and building it found a real bug in its own first
  version: the window opened a week back, `JourneyPlanner` reads coverage on the window's *first*
  day, and the rep assignment starts today — so the seeded plan came back with no calls on it and
  logged success.
- ~~**A production build in the CI loop or the runbook.**~~ **Done in W12**, and the framing was
  slightly wrong: CI has run `npm run build` since the beginning, so the production build was never
  missing — **nothing looked at what it produced**. `scripts/check-service-worker.mjs` inspects the
  artefact after the build, and the sabotage that justifies it is the clearest in the project:
  deleting `app/[locale]/offline/page.tsx` leaves a clean build **succeeding**, lint passing and all
  2,854 tests green, while `public/sw.js` goes on promising `/en/offline` to every device. Running
  the worker for real is still `Week 14` E2E, and the script says so in its own header.

> **W11½ closed, and re-checked by a second full pass** —
> [regression-2026-08-14.md](engineering/regression-2026-08-14.md). All seven slices verified,
> five of them in a browser: the whole rep loop now runs end to end, which it could not the day
> before. Seven of the previous sweep's nine findings are closed; F8 and F9 stay open by record (**F8 closed in W12**).
>
> **It found five more, and they are all one shape** — something built and reachable only through a
> door nobody opened. The largest is **F1: order and audit capture are linked only from a workflow
> step**, so a channel with no workflow can be visited and nothing can be done in it. That is why two
> consecutive sweeps could not test order capture by hand — R4 removed the first wall and this one
> stood behind it. `ORD-01` and `AUD-01` are Musts reachable only through optional configuration.
>
> The sweep's own recommendation is **not a tenth finding of the same kind but a gate**: a
> reachability scan over mutation types and field routes, in the shape of R1's registry check and
> `check-vector-readers.mjs`. Either would have failed on F1 and F2 the day they were written.
>
> **All five are closed, and so is the gate** — F1, F4, F3, F2 (in two: the rule had to reach the
> device before a writer could exist), the reachability job, and F5 (in two: the wire, then the
> device). **F8 went with them**, so both sweeps are now clear apart from F9, which is correct server
> behaviour recorded as a non-finding.
>
> Three of the six turned out not to be the shape the sweep assigned them. **F2** was not a forgotten
> writer but an unbuildable one — `BR-JRN-4` could not be evaluated from anything the round carried.
> **F3** was an edge one layer below any route or mutation, which the new gate would have passed.
> **F8**'s route was covered by a test that asserted the bug. The pattern behind the pattern is that
> a finding's *stated cause* is the least reliable part of it, and reading the code for the fix is
> where the real one turns up — which is the argument for building each of these rather than
> batching them.

### Week 12 · Dashboards + config-builder UI
**Goal:** the Phase 3 demo — the full loop, both sides.
- Supervisor **dashboard** (coverage, strike rate, perfect-store, order value) from module query contracts ([reporting](product/00-product-overview.md#reporting--kpis-cross-cutting-read-side)).
- **Config-driven builder UI** (workflow steps, perfect-store weight sliders, survey questions) — the customization showcase (per [wireframes](ux/README.md)).

**Done when:** the dashboard reflects field activity; editing a workflow/weights/form flows to the field app on next sync. **▶ Phase 3 demo — full golden path.**

#### Decomposition

**Half of this week already shipped, three weeks early.** The config-driven builder UI is the
perfect-store weights screen (W10 slice 8) and the survey editor and list (W10 slices 9a/9b). What is
left of that bullet is nothing.

**And the other half is not a week of screens** — the same trap W9 carried a warning about, arriving
for a different reason. The dashboard is one screen and it cannot be built, because the read side it
composes does not exist yet.

**The reporting spec describes a read side that is not there.** It says dashboards are *"composed
from the query contracts each module already exposes (`IVisitQuery`, `IAuditQuery`, `IOrderQuery`,
journey coverage, etc.)"* — and the module registry is more honest than the spec: `IVisitQuery` is
listed **in plain type, meaning planned**, and it has never been built. Visit exposes `IVisitIngest`
and `IVisitContext` and nothing that reads.

**The three that do exist answer the wrong shape of question.** `IOrderQuery`, `IAuditQuery` and
`IJourneyQuery` each answer about **one** record — `ForVisitAsync`, `ForOutletAsync`. Every KPI on
the dashboard is an aggregate over a *territory* and a *period*: coverage is planned-versus-actual
across a cycle, strike rate is a ratio over a set of visits, order value is a sum. Composing those
from per-record reads would mean the endpoint fetching every visit in a month and adding them up in
memory — which works for a demo tenant and is the wrong shape to write down.

So the week is: **one contract that does not exist, four that need an aggregate question, a place to
compose them, and then the screen.** The two review screens the rail has been advertising since W9
come with it, because a dashboard reporting order value with no way to open an order is half a
console — and because their badges are now lying.

#### Where the composition lives

**Not a reporting module** — the product spec rules that out and is right to: reporting owns no
writes, no schema and no invariants, so a module would be a package with a namespace and nothing to
protect.

**Not in a module either.** Sync composes across modules today (`PushEndpoints` calls Visit, Order
and Audit ingests) and that is the precedent to *not* follow here: Sync is a module because it owns
the idempotency ledger and the device registry. A dashboard owns nothing.

So the endpoint lives in **`FieldKit.Server`**, beside `AuthEndpoints` — the host that already
composes what modules expose without being one. That is a boundary decision rather than a filing
one, and the architecture tests should be read against it before slice 3 is written.

| # | Slice | Requirements | ~Size |
|---|---|---|---|
| 0 | **This decomposition, and the badges that expired** | — | 150 |
| | *The rail advertises `Visits & audits · W9` and `Orders · W11`, and both weeks are done — the disabled-item design is honest only while the week badge is true, so a badge naming a date that has passed is the dead control the design exists to avoid. Re-badged with the week that will really ship them, and `Dashboard · W12` with it.* | | |
| 1 | **`IVisitQuery`** — the contract the registry has listed as planned since W7 | `VIS-10` | 250 |
| | *Visit exposes no read at all. The shape follows the callers rather than the noun: a visit by id for the review screen, and a **count** of visits by outcome over a territory and a window for the dashboard — not a list the caller reduces. The registry row goes bold, which `ModuleRegistryTests` will check on the next build.* | | |
| | **Shipped** *with **one** method, not two. `CountByOutcomeAsync` took **outlet ids** rather than a territory — a `Visit` carries an outlet and a user and knows nothing about org structure, so resolving a territory to its shops stays `IRepScope`'s and `ITerritoryDirectory`'s job and the caller narrows first. The visit-by-id read was **dropped**: its caller is slice 5, so writing it here would be the guess this table's own rule is about, and `IVisitContext.FindAsync` already answers the thin version. `Open` is reported beside the two outcomes rather than folded in, and `StrikeRate` is **null** rather than zero when nothing has finished — a fresh tenant and a bad week must not look alike. The tenant-context harness `AuditIngestTests` wrote and `OrderIngestTests` copied was extracted to `AsTenant` at this, its third caller, exactly as that file's note said it should be.* | | |
| | *This row cited `VIS-06` when it was written, which is **"notes & photos as visit steps"** — the wrong requirement. The read-side one is `VIS-10`, supervisor visit review, and slice 5 was mis-cited the same way. A spec ID that points at the wrong requirement is worse than none: it survives review by looking like a citation.* | | |
| 2 | **The four aggregate questions** — one per KPI, each answered by the module that owns it | Reporting | 400 |
| | *Coverage from Journey, strike rate from Visit and Order, perfect store from Audit, order value from Order — the split the reporting table already dictates. **Each returns a computed figure rather than rows**: the module that owns the data owns the arithmetic, and an endpoint that sums someone else's records has taken their invariant home with it. Integration tests over a seeded month, because a KPI whose only test is a unit test on an empty set is a KPI nobody has seen work.* | | |
| | **Split into 2a / 2b / 2c, one KPI each.** *Slice 1 came in at 640 lines for a single module's aggregate, so three more in one PR would be three times over the ~400 budget with no way to review any of them properly. The split is **by KPI rather than by module**, because that is the unit that is shippable: coverage needs a number from Journey and a number from Visit, and half of a ratio is not worth merging.* | | |
| 2a | **Coverage** — the denominator from Journey, the numerator from Visit | `JRN-04`, `BR-JRN-6`, `VIS-10` | 400 |
| | *`IJourneyQuery.CountPlannedAsync` counts what a **published** round promised, split into calls still standing and calls the rep declined; `IVisitQuery.CountFulfilledCallsAsync` counts the **distinct planned calls** visits claimed.* | | |
| | **Found while building it:** *a planned call **never learns it was visited**. There is no `Visited` status and nothing in Journey subscribes to `VisitCompleted`, so `PlannedVisitStatus.Planned` means "not declined", not "not done" — a name that reads as the opposite of what it holds. That is why coverage is a composition rather than a Journey number, and why the numerator is counted **distinct**: two check-ins against one call would otherwise push coverage above the round, and an unplanned visit would push it above 100%. Both are states the system permits — `BR-VIS-2` records rather than refuses — so neither is hypothetical.* | | |
| 2b | **Perfect store** — the scored average from Audit | `AUD-09` | 300 |
| | *`IAuditQuery.SummariseAsync` — the average score, the per-pillar average with its **skipped** count, and the **weight-set versions** the window mixed. `BR-AUD-8` stores the version each audit was scored against; an average across two of them is an average of two rulers, so the contract names them rather than refusing the number, and `Comparable` is the question a caller has.* | | |
| | **Found while building it:** *(1) an audit carrying **nothing at all** never reaches the schema — `AuditRefusal.Empty` turns it away — so "unscorable" is narrower than it looks: it has to record something that scores nothing. (2) An unrounded aggregate is **not reproducible**. Postgres returned `66.8366666666666667` where C# computed `66.836666666666666666666666667` for the same mean; `avg(numeric)` works at the engine's scale. The average is now rounded half-up to two places — `BR-PRD-9`, the policy the scores being averaged already carry.* | | |
| 2c | **Order value** — value, lines and promotion usage from Order | `ORD-09` | 300 |
| | *`IOrderQuery.SummariseAsync` — standing orders and their lines, the rejected and cancelled counts, the orders the server disputes, and the value **per currency**. Money is split by currency because adding two of them is not arithmetic, and the value is the **device's** total: `BR-ORD-2` re-prices and flags, never applies, so the server's figure is one nobody at the counter agreed to.* | | |
| | **Found while building it — three, and one changes the KPI table:** *(1) **promotion usage cannot be reported at all.** An `OrderLine` records what it cost and not which promotion made it cost that; inferring it from `quantity × unit price` exceeding the line total fails because both are rounded independently, so a line rounded down would report a discount nobody gave. The schema has to carry the promotion first. (2) **`Accepted` and `Cancelled` are unreachable** — rejection is the server's only transition — so those two counts ship untested, deliberately, rather than silently dropping a state that arrives with slice 6. (3) **The `AsTenant` extraction in slice 1 was incomplete**: it followed `OrderIngestTests`' comment instead of searching, and five more copies exist (`OrderRejectionTests`, `OrderRepriceTests`, `PhotoConfirmTests`, `PricingServiceTests`, `SyncPullOrderTests`). Recorded in the file; folding them in is a mechanical PR of its own.* | | |
| 3 | **`GET /api/reporting/summary`** — the composition, in the host | Reporting | 300 |
| | *One request, one period, one territory scope, four contracts. Tenant-scoped and rep-scoped like everything else; `IRepScope` decides what a supervisor may total, and a supervisor who may read one territory must not learn another's numbers by asking for a wider window.* | | |
| | **Shipped, and the sentence above was wrong about the scoping.** *`IRepScope` answers about a **rep's** assignments on one day, so an administrator — or any supervisor not assigned as a rep — resolves to no shops and gets an empty dashboard. The visibility scope that would answer it (`BR-ORG-4`) is returned as **data** by `/api/org/users/{id}/scope` and explicitly not enforced; its own note says enforcement lands with **`ORG-09`**, which is unbuilt. Asked and decided: the endpoint is scoped exactly as every other back-office read is — tenant-isolated, permission-gated (`visit:read` **and** `journey:read`), with an optional `?territoryId=`. Making reporting the one enforced read in the system would be inconsistent as well as incomplete.* | | |
| | **Also found:** *(1) **nothing could enumerate outlets.** All four aggregates take outlet ids and no contract could produce that list — `IOutletCatalog` resolves ids it is given, `ITerritoryDirectory` mapped only outlets→territory, `IRepScope` answers per rep per day. `ITerritoryDirectory.OutletsInAsync` closes it; null means every **territory**, so a shop in none is outside every scope and the per-territory figures still add up to the unfiltered one. (2) **The fan-out over the four contracts had a concurrency bug.** Visit answers two of the five questions from one `DbContext`, and EF Core refuses a second operation on a context while the first runs — started together they threw. The tests passed one at a time and failed the moment the class ran as a class. Concurrency is now per **module**, because the schema-per-module boundary is the only thing that makes it safe.* | | |
| 4 | **The dashboard screen** | Reporting, `JRN-10`, `AUD-09` | 350 |
| | *Coverage, strike rate, perfect store, order value; coverage by territory beneath. It is the first back-office screen that reads across every module, which makes it the honest test of whether the contracts are usable — and the first that has to say **"no activity yet"** without looking broken, since a fresh tenant is the state a reader most often meets it in.* | | |
| | **Shipped, with "coverage by territory" as a selector rather than a table.** *The endpoint answers **one** scope per request, so a per-territory breakdown means either a request per territory — the N+1-over-HTTP shape `OutletsInAsync` exists to prevent, and doing it in the browser one slice later would be inconsistent — or a change to the response. It is a response-shape change and belongs in its own slice.* | | |
| | **Found while building it:** *(1) the window was **the browser's** in the first draft and wrong twice over — `useBusinessDay` is a formatter rather than a clock, and a browser deciding "this month" does it in a timezone the data is not stored in (in Bucharest at 02:00 on the 1st, a window built through `toISOString()` starts in the previous month). Sending no dates makes the server answer for its own UTC month, which is the clock every aggregate already dates by. (2) Three sidebar tests used the **Dashboard** as their example of a scheduled item and all three broke at once — the trap that file had already escaped for the badge test in W7. An assertion about "an item not built yet" cannot name one, because building it is the plan; all four now derive it.* | | |
| | **Verified in the browser** *(admin, 44 outlets): coverage 5.17%, strike rate 97.37%, perfect store 21.67%, order value 34.00 EUR, pillars with their skipped counts. On a territory with planned calls but no audits, coverage reads a real **0.00%** beside a **—** for perfect store and order value — the distinction the screen exists to keep, both visible at once. Light and dark both paint from tokens.* | | |
| 5 | **Visits & audits** (back office) — the rail's `W9` badge, four weeks late | `VIS-10`, `AUD-09` | 350 |
| | *A supervisor can read what a rep recorded: the visit, its steps, the audit beneath it and the perfect-store score with its pillars. Read-only — nothing here writes, because a sealed visit is sealed (`VIS-05`) and an audit's score reproduces only against the weight version it was scored with.* | | |
| | **Split into 5a / 5b**, *the list and the detail. Two screens' worth of component, tests and copy in two locales does not fit one reviewable PR, and the list is shippable on its own — it is the screen a supervisor opens.* | | |
| 5a | **The visits list**, and the ceiling the read never had | `VIS-10` | 350 |
| | *Shop, rep, day and outcome, filtered by outlet or rep. The two lines that earn the screen are exceptions rather than decoration: a visit worked **away from the shop** prints the sentence the rep typed (`BR-VIS-2` records rather than refuses, and that sentence is the whole return on the rule), and a visit **captured offline** shows when it was worked beside when it arrived.* | | |
| | **Found while building it:** *`GET /api/visits` had **no ceiling**. Its only caller was a rep's device, which always passes an outlet or a user — so the unbounded, tenant-wide case never ran until a back-office list asked for it. A read whose cost grows with how long the tenant has existed is the shape of an outage development never sees. Bounded at 200, newest first, and the screen says when the cut is biting; paging stays out, because the honest answer to "I need last March" is a date window and no screen has asked for one.* | | |
| 5b | **The visit detail**, its steps and the audit beneath it | `VIS-10`, `AUD-09` | 350 |
| | *What the rep was asked to do, what they did, and the audit with its score and pillar breakdown. Read-only, and the server agrees: a checked-out visit is sealed (`BR-VIS-4`) and an audit is append-only (`BR-AUD-6`), so neither module has a write path a screen could offer.* | | |
| | **Two things it refuses to flatten.** *A step the rep never completed is **shown as pending** rather than dropped — six steps with two untouched is a different visit from four, and only the first says what was skipped. A **skipped pillar** reads "not measured", never 0%: `BR-AUD-2` renormalises it out of the score rather than counting it against the shop, so a zero would misstate the shelf **and** disagree with the total above it. The weighting version travels with every score, because `BR-AUD-8` records it precisely so two audits under different weights are not compared.* | | |
| | **Verified in the browser** *(admin, after a false start: Docker's daemon was down at first, so the check was deferred rather than claimed). The list shows 38 visits, 12 carrying an override reason and 20 marked as captured offline. One audit renders **Availability "Not measured"** beside **Price compliance "0.00%"** — the distinction the slice exists for, on one screen. Its arithmetic confirms `BR-AUD-2`: measured pillars are share of shelf (15% × 30) and price (0% × 20), giving 450/50 = **9.00%**, which is the score shown; folding the skipped pillar in as zero would have read 4.50%. An audit with all three pillars skipped shows **"—"**, and a visit with no audit shows the sentence — the endpoint's 404, translated.* | | |
| | **Found in the dev data, not the code:** *one visit's stored `GeofenceOverrideReason` is the doubled string `dev session, no locationdev session, no location`. The screen renders it faithfully; whatever pushed that visit wrote it twice. A dev-seed artefact rather than a product defect, recorded here so the next reader does not chase it as a rendering bug.* | | |
| 6 | **Orders** (back office) — the rail's `W11` badge | `ORD-09`, `BR-ORD-9` | 400 |
| | *The queue a supervisor works: submitted orders, their lines and totals, and the **rejection** path that already exists end to end on the server and has never had a screen. `order:reject` was minted in W11 slice 4a and no human has ever held it — which also means the realm needs the role before this is testable, and **a realm change is not applied by deploying** (W10's finding).* | | |
| | **The blocker above does not exist, and checking cost two minutes.** *`admin` holds `order:reject` in the dev realm JSON **and** in the running Keycloak — read back from the app's own `/api/auth/whoami`, not from the file, because W10's finding is exactly that the two can disagree. Whenever the role was added, the realm was re-imported since. No realm change is needed and the rejection path is testable as it stands. A stale warning in a plan is worth as much as a stale badge on the rail: it makes somebody plan around a problem they do not have.* | | |
| | **Split into 6a / 6b**, *the queue and the rejection control. The read behind the queue did not exist either — `IOrderQuery` had `ForVisitAsync` and `ForOutletAsync` and nothing that answers "what is waiting" — so 6a is a contract, an endpoint and a screen, which is a PR on its own.* | | |
| 6a | **The orders queue** | `ORD-09` | 400 |
| | *`IOrderQuery.RecentAsync` — the orders a supervisor works through, filtered by status, newest first, **bounded from the first line**. W12 slice 5a found `GET /api/visits` unbounded because its only caller always passed a filter; this read exists **for** the tenant-wide question, so it carries its ceiling rather than acquiring one after an outage. The screen opens on `Submitted`, because that is the job, and offers `Rejected` so "where did that order go" is answered on the screen.* | | |
| | **The last badge came off the rail here**, *and it broke five tests that assumed one was still on it. `navigation.test.ts` required *some* item to be scheduled — true from W1 until this slice — which is now a test requiring the product to be unfinished; it pins the built-or-scheduled invariant instead. Four `sidebar.test.tsx` tests looked for a scheduled item to render and found none; they moved to `sidebar-scheduled.test.tsx` and run against a mocked nav. Slice 4 had already made them derive their subject rather than name it, noting it "survives every screen landing except the last" — this was the last, and the behaviour is a promise about future weeks rather than a fact about this one.* | | |
| | **Verified in the browser** *(admin): the queue opens on Waiting with two orders, newest first; the filter drives the query string (`?status=Submitted`, `?status=Rejected`, bare for all) and all three answer 200; the empty state says **"No orders here."** for a filter that found nothing and **"Nothing is waiting on a decision."** for an empty queue. The rail now shows eight links and no disabled items. **Not exercised against live data:** a rejected order's row and the disputed-price flag — the dev tenant holds two orders, both submitted, neither disputed. Both are covered by component tests; seeing them live needs a write to dev data.* | | |
| 6b | **Rejecting from the queue** | `ORD-12`, `BR-ORD-9` | 300 |
| | *The refusal path has existed end to end on the server since W11 slice 4a and has never had a control. **Whole-order, never per line, and the form's shape says so:** `BR-ORD-4` denies everybody — supervisor included — the right to change what a rep captured at a counter, so a supervisor picks a reason and may **point at** a line without editing it. The reason is required and the note is not — half of `F4`'s own examples need no sentence, and forcing prose there produces "n/a" in a field a rep is meant to read. **Not a confirmation dialog:** rejection is reversible in the sense that matters (`BR-ORD-9`), and "are you sure" would be ceremony over a decision the form already makes explicit by asking for a reason. **No new endpoint, so no new `.http` request** — W11 slice 4a shipped five, and this slice is the caller they were waiting for.* | | |
| | **Verified in the browser** *(admin), **by rejecting a real order** — the first write this back office has made to dev data, and the screen's own function rather than an unasked-for mutation. `POST /api/orders/{id}/rejection` answered **200**, the order left the Waiting queue without a reload (the mutation invalidates the whole `["orders"]` prefix, not one filter's key), and the Rejected filter shows it as **"Rejected — off assortment. Delisted in this banner since June — please re-take without it."** with **no Reject button**, which is the submitted-only gate seen from the other side. That also closes the path slice 6a could not exercise: **a rejected row against live data**. The disputed-price flag stays unexercised — neither of the dev tenant's orders is `Differs`, and inventing one would mean writing a price the seed does not have.* | | |
| | **Two things the walk surfaced, neither a defect of this slice.** *The line picker labels an option `{quantity} {unit}` — "4 CS" — because the queue never fetches products and a **product name** would mean a second query for a control most rejections leave empty. Enough to point at the only line on a one-line order; **ambiguous on an order with two lines of the same quantity and unit**, and worth a name once the queue reads products for anything else. Separately, the **preview pane was not compositing frames**, so this walk is evidenced by page text and the network log rather than a screenshot; `computer` clicks were inert and the interaction went through `form_input` and DOM clicks.* | | |
| | **The catalogue named a placeholder the server never sends.** *`Refusals.order.rejection.noteTooLong` read "at most `{max}` characters", but `POST /api/orders/{id}/rejection` interpolates the limit into its English sentence and sends **no `args`** — unlike its sibling `notSubmitted`, which does. next-intl then reports the error and returns **the key path**, so the alert read `Refusals.order.rejection.noteTooLong`: exactly the failure [ADR-0012](architecture/adr/0012-server-message-localization.md) exists to prevent, and exactly what `refusals.ts` warns is the cost of that coupling. Fixed on the catalogue side rather than by changing a shipped endpoint's contract for a copy decision.* | | |

**Not in W12.** Richer metrics, custom KPIs and anything resembling a warehouse stay out — the
product spec calls operational dashboards the scope and OLAP a non-goal. Date-range pickers beyond
"this cycle / this month" are the same call: a period selector is cheap to add once the aggregates
take a window, and expensive to design before anyone has read the numbers once.

**After W12 · the `AsTenant` debt, paid.** Slice 1 extracted the tenant-context test harness at its
third caller and slice 2c corrected itself: there were five more copies, not none, because slice 1
had followed a comment instead of searching. This is the mechanical PR that folds them in —
`OrderRejectionTests`, `OrderRepriceTests`, `PhotoConfirmTests`, `PricingServiceTests` and
`SyncPullOrderTests`, and **a sixth the 2c note also missed**: `RepScopeTests`, whose copy was
written differently enough (`JwtSecurityTokenHandler`, a different authentication type, a
`RequestServices` nothing reads) that searching for the *name* would have walked past it twice.
Searching for what the harness *does* found it. Its principal was genuinely not identical to the
shared one, so the difference was checked rather than assumed: only the two claims
`KeycloakTenantContext` reads decide anything, and its own "a plain scope has no tenant" test is
what makes that checkable. **Non-vacuity:** falsifying the tenant claim inside `AsTenant` fails
**48 of the 53** tests in those six files at once — the five survivors never touch it.

### Week 12½ · Navigation & theme redesign

**Goal:** every screen has a navigation item, and a person can choose the theme.

**Nothing in the back office is unreachable.** All 28 routes are linked from somewhere; each was
checked. The problem is **depth**: the sidebar has one level, and of the seventeen screens that
should be navigation items, six are. Eleven are full workspaces — price lists, promotions,
assortments, the working calendar, surveys — reachable only by landing on a section index and
spotting the right button in a row of outline links. The eleven record-detail screens below them
(promotion tiers, price-list scope, tax rates) are left with the browser's back button.

> **Correction, from walking it in a browser during slice 3.** The audit said those eleven have *no
> breadcrumb*. They have one — a `crumb` string per screen in the message catalog, rendering
> `Master data / Products / Price lists / Scope`. It is a `<p>` containing **zero links**, so the
> trail is printed and cannot be followed, which is why the symptom looked like absence. That
> changes what slice 5 is: not adding breadcrumbs but making the existing one navigable, and
> deriving it from the model rather than maintaining twenty-one literal paths across two locales.

**Done when:** the rail and its section panel reach all seventeen; a back-office route with no
navigation item fails CI; and the theme is a choice, defaulting to light.

#### The decision this reverses

W10 slice 9b took the opposite view and [ux/README.md](ux/README.md#navigation--the-second-level)
records it: a **link row** between the two Configuration pages rather than "a second sidebar level
built for one section with two pages". That was right for one section with two pages. There are now
nine sections, seventeen screens, four near-identical `*-actions.tsx` rows — and two navigation items
that point *into* themselves because their sections have no index worth landing on, `Journeys` at
call frequency and `Configuration` at the weights, each carrying a comment that **a nav item should
go somewhere real**. Those comments are the missing second level, worked around three times. The
reversal is recorded rather than performed silently, because the original reasoning was sound and
what changed is the size of the thing it was reasoning about.

#### The shape chosen

**A 68px icon rail plus a section panel**, one of three prototyped
([redesign wireframes](https://claude.ai/code/artifact/725d5e98-2292-4639-a9c7-40e015c39628)) — and
not the one recommended. An
expanding sidebar is a smaller change and puts the same seventeen screens in the same navigation.
Both costs that argued against a rail turned out smaller than they looked once measured: the nine
section icons **already exist** in `sidebar.tsx`'s `ICONS` map, and every sub-screen already has a
Lucide icon chosen for it in the action row it lives in, so the icon work is re-use rather than a
design exercise. What remains is 254px of chrome before content — on a 1280px laptop, 1026px for the
widest table in the app. Slice 3 measures the outlet list against that number rather than slice 6
discovering it.

#### Decomposition

**Slice 2 lands before any of the UI.** The gate goes in while the model is fresh and the screens
that will consume it do not exist yet, so the new navigation cannot ship with a gap in it — the same
ordering argument as W11½'s R1 registry check, and the reason "every screen has a navigation item"
becomes a property of the build rather than of somebody's memory.

**Slices 7 and 8 are independent of 1–6** and of each other. They are numbered last because the
navigation is what was asked for, not because anything blocks them.

| # | Slice | Requirements | ~Size |
|---|---|---|---|
| 0 | **The decomposition and what it reverses** — this section, the UX direction, the theme decision | — | 200 |
| 1 | **The navigation model grows a second level** — `NavItem.screens`, and the seventeen move in | — | 250 |
| | *Pure TypeScript, no visual change: the sidebar goes on rendering the top level and the action rows go on holding the links, so the model can be reviewed as a model. The union in `navigation.ts` already refuses an item that is neither built nor scheduled; this is what lets it refuse a **screen** with no home. Section visibility becomes any-of over its screens' permissions — someone who may read price lists but not promotions still has a reason to see Products.* | | |
| | *Shipped, and **the permission shape sketched here was wrong**. Any-of is what a section needs; reading the action rows to move their links in found two screens it cannot express — assortments and order minimums are gated on `product:read` **and** `channel:read`, because each is organised by channel and a reader without the channel list gets a selector with nothing in it. So `Requirement` is a conjunction of disjunctions, kept as data rather than a predicate so a test can enumerate it. **Outlets is the section that earns the model**: four screens on four different permissions where the sidebar asks one question, which means slice 4 will show it to someone holding `channel:read` and not `outlet:read` — who has no route to the channel list at all today. Recorded in the type, not fixed here, because the slice promised no visible change.* | | |
| 2 | **The gate learns about the back office** — a route with no navigation item fails CI | — | 150 → 240 |
| | *`scripts/check-reachability.mjs` checks mutation types and field routes today. It gains a third question: every `(back-office)` route is either a screen in the model or a child of one. Proven by sabotage, as the other two were — and the vacuity guard matters here for the reason it did in R1 and in `check-service-worker.mjs`, since a scan whose input goes quiet passes.* | | |
| | *Shipped, and it grew a **second direction** on the way in: a navigation item whose route does not exist is a live link to a 404, quieter than a missing item because the nav still looks whole. That is the half `R1` left out of the module registry and paid for over three drifts, so leaving it out here twice would have been a choice. **It is also the gate's one exception to being a text scan** — `NAVIGATION` is plain data in a module with no React in it, so Node's type stripping imports it as a model, and a regex over `href:` could not have named the section a missing screen belongs to. The equivalent assertion added to `navigation.test.ts` in slice 1 was **deleted rather than kept**: two implementations of one rule drift toward whichever was edited last, which is this gate's own failure mode arriving through the gate. Five sabotage passes; `28` routes across `17` screens.* | | |
| 3 | **The section panel** — the second column, beside the sidebar that still exists | — | 220 |
| | *Additive on purpose: the action rows stay, briefly duplicating their own links, so nothing in the stack is unreachable between two PRs. Also where the 1026px number is measured against the outlet table rather than assumed.* | | |
| | *Shipped, and **the measurement clears concept B**. The widest table in the app is the outlet list: `864px` at max-content against `963px` available once the finished 254px of chrome and the scrollbar are taken off a 1280 laptop — about 100px of headroom, and it never has to wrap. Nothing overflowed on any screen at the **wider** interim chrome either, which is the stricter test. The journey grid was not measurable as a rep and does not need to be: it is `overflow-x-auto` by design, because a three-week plan fits at no chrome width, so the rail costs it one visible day rather than correctness.* | | |
| | ***Building it found a bug in slice 1**, which is why the panel rendered blank on first run. `permits` passed its predicate straight to `some`, which supplies `(element, index, array)` — harmless for the one-parameter arrows every fake in `navigation.test.ts` uses, and wrong for the predicate that ships: `usePermissions().has` is **variadic and means all-of**, so it was asked whether the caller holds `product:read` *and* `0` *and* `["product:read"]`. Always false, every screen hidden from everyone, under nineteen green assertions. The regression test is variadic on purpose. Second time in three slices a convenience fake was less demanding than the real collaborator.* | | |
| 4 | **The sidebar becomes the rail** — and the action rows are deleted | — | 300 |
| | *The layout switch, and a net-negative diff: the components go, their links having moved into the model in slice 1. `Journeys` and `Configuration` stop pointing into themselves — the rail selects a section, the panel's first screen is where it lands, and the `section` field that exists only to fix highlighting can go with them.* | | |
| | *Shipped. **`href`, `section` and `permissions` all went**, not just `section`: each was a second copy of something the screens already said, and the derivation is not always the same answer — `landingFor` sends somebody holding `channel:read` alone to Channels rather than to an outlet list they cannot open. The chrome measured **275px** live (68 + 192 + scrollbar), leaving **957px** for the outlet table's 864 — 93px of headroom against the ~100 slice 3 predicted. **There were four rows, not five**, as this plan and three PRs before it said; corrected above. **None of the four had a single test**, which reframes the risk the plan recorded: there were no assertions to re-home, and the eleven screens' only door had no coverage at all — the reachability gate from slice 2 is what would have caught its removal, which is why it landed first.* | | |
| | *Two things went wrong worth recording. A regex that stripped the three fields also ate `href` from the **three screen entries formatted across multiple lines** — lint passed, and it was slice 1's *distinct route* uniqueness test that caught it, reporting 15 routes where there should be 17. The same regex's `\s*` consumed the previous line's newline in nine page files, leaving `</div>` and `</header>` spliced. Both were mechanical edits to a file whose formatting was not uniform, and the lesson is that neither lint nor eslint sees a deleted field — only a test that counts.* | | |
| 5 | **A breadcrumb you can follow** — the eleven detail screens stop being dead ends | — | 120 → 200 |
| | *Re-scoped by the correction above. The breadcrumb exists; it is a `<p>` of literal text, so the trail is printed and cannot be walked. The work is making it navigable and **deriving it from `NAVIGATION`** rather than from twenty-one hand-written `crumb` strings in each of two locales — the same one-source argument as slices 1 and 2, and the reason a screen renamed in the model would otherwise keep its old name in the trail.* | | |
| | *Shipped, and **the crumbs had already drifted** — which is the argument for deriving rather than correcting them. Before a line was written they disagreed with the navigation in four places: `Field ops` is a group this nav has never had, `Journey planning` is `Journeys`, `Products` is `Products & pricing`, `Perfect store` is `Perfect-store weights`, and Configuration's crumbs dropped the `Admin` group entirely. Two names for one place, in both languages, with nothing able to compare them. **19 full paths deleted per locale, 8 reshaped into leaves.** Two mistakes: the mechanical replacement matched the crumb's *shape* rather than its meaning and silently dropped five leaves (`Scope`, `Scope`, `Targets`, `Tiers`, `Tax rates`), caught by reading the catalog back; and the first tests read the links synchronously and saw none, because the trail paints from the path while the section **link** waits on the API — pending counts as denied, so for one render the section is text.* | | |
| 6 | **Responsive** — rail and panel collapse to one drawer under `md` | — | 150 |
| | *The back office is desktop-first ([ADR-0004](architecture/adr/0004-nextjs-offline-first-frontend.md)), so this is the "does not break" bar rather than a second design. Two columns of chrome is the concept's stated cost and this is where it is paid.* | | |
| | *Shipped. **Mobile chrome went from 165px to 45px**, because the columns lost their second layout entirely — they were a horizontal strip below `md` and a column above, and the drawer means the column is the only shape now. One instance, not one per breakpoint: rendering them twice would put two `Back office` landmarks in the document. Three things a backdrop does not do — **`inert` on the content** (an overlay hides the page from the eye, not from the tab key), Escape with focus returned, and shutting when the viewport stops being mobile. **Closing on arrival is adjusted during render**, since React 19's `set-state-in-effect` refuses the obvious version and it also paints the drawer over the screen it just reached. My first fix for that derived `open` from `openedAt === pathname` — wrong the moment you navigate away and back, and caught by the test named after that case.* | | |
| | ***And the mistake worth keeping.** `components/back-office/shell.test.tsx` already existed and I overwrote it, deleting seven session-lifecycle tests — restoring, anonymous, expired, silent renewal, sign-out. **The suite total did not move**, because I had added exactly seven of my own; the only signal was `git status` reporting `M` on a file I believed I had created. Restored in full, drawer tests split into `shell-drawer.test.tsx`. A stable test count is not evidence that nothing was lost.* | | |
| 7 | **The theme is a choice** — light by default, dark and system offered | `CFG-08` (adjacent) | 260 |
| | *Both token sets already ship and `globals.css` already honours `.dark` / `.light` on `<html>`; nothing sets it. So this is a provider, `localStorage`, and a three-state control — plus **the sharp edge**: the class has to be set by an inline script before first paint, or a cold offline start flashes the wrong theme, and that script has to survive the service worker serving the shell from cache. Per-**tenant** theme tokens stay a separate concern.* | | |
| | *Shipped, and **the sharp edge was sharper than written**: the pre-paint script has to carry the **CSP nonce**. `script-src` is `'self' 'nonce-…' 'strict-dynamic'`, so an un-nonced inline script is refused silently and the theme falls back to the device with nothing in the UI to say why. The layout reads it off the `x-nonce` header the proxy already sets, and the `dynamic = "force-dynamic"` note that exists for Next's own bootstrap turns out to cover this too — the worker caches a document with its headers, so a cached page keeps both the script and the nonce that authorises it. No provider was needed.* | | |
| | *Two hydration mismatches, both real rather than noise. **The control** was `useState(storedTheme)`, which renders the default on the server and the stored choice in the browser — it hydrated with `aria-checked` on the wrong option, which is the whole of what a screen reader is told. `useSyncExternalStore` models that honestly (server snapshot, client snapshot, a re-render between them) where suppressing the warning would have hidden a disagreement rather than described one. **The script tag** needs suppression for the opposite reason: React deliberately does not carry `nonce` into the client tree, so it is present on the server and empty on the client every time, by design.* | | |
| | *Verified in a browser across all three states. `System` is the one worth naming: it removes **both** classes, and on a machine whose OS is set to dark the page resolves to dark — while `Light` overrides that same OS preference, which is what light-by-default has to mean. The pre-paint script and `applyTheme` are two implementations of one rule, so `theme.test.ts` runs the script string against a real DOM and compares — the pricing-vector argument, applied to a `<script>`.* | | |
| 8a | **The field app gets a navigation** — a bottom tab bar | `JRN-05`, `OFF-05` | 180 |
| | *Six screens today and two links in the header. Everything else is a linear flow leaving through `router.replace`, which serves the golden path and strands anyone who steps off it. Sync earns a permanent place rather than a badge that appears when something is already wrong.* | | |
| | *Shipped, **and it absorbed 8b** — because reading the code to build the bar found that the bar alone changes no reachability at all. `/field` and `/field/device` are already linked from the header on every field screen, so three tabs over two destinations is a restyling that would have shipped under a commit claiming the field app got a navigation. The route came with it and the bar has somewhere to go. **Sync is not a tab**: the wireframe drew it as the fourth and it is a status and a button, so it would navigate nowhere — it stays in the header. **`/field` matches exactly and the others by prefix**, because every field route begins with `/field` and a uniform prefix lights Today on the outlet list, the device screen and every visit at once; nothing is lit on a visit, an audit or an order, since the bar is a way *out* of those rather than a claim about them. The shell became one viewport with the middle scrolling, as the back office had, so the bar sits on the bottom edge with no `position: fixed`.* | | |
| ~~8b~~ | ~~**`/field/outlets`** — the rep's territory, not just today's list~~ — **folded into 8a** | `JRN-06`, `A4` | — |

**One fix that was not a slice.** Between 7 and 8a the navigation was reported as *"the last row seems floating and has odd scrolling behaviour, I do not know if it is the browser"*. It was not the browser. The shell was `min-h-dvh` and the **page** scrolled while the rail and the panel were `h-dvh` and `static`, so on the 2,402px outlet list the navigation ended at y=800 and scrolled away — leaving the left 260px empty and the row at that boundary apparently floating.

`position: sticky` is the obvious answer and does nothing here: `globals.css` sets `overflow-x: hidden` on `html, body`, which makes `body` a scroll container that never scrolls, so a sticky descendant sticks to it. Tried in the browser first — the rail sat at −1400px on a 1400px scroll. The fix is the app-shell shape both shells now use.

**It predates the redesign.** The single sidebar has carried `md:h-dvh` since W5, so the navigation has always stopped at one viewport; one narrow grey column ending is far less legible than two, one of them the colour of the page. The redesign did not cause it — it made it reportable.

Its regression test reads **source** rather than a render, in the shape `globals.test.ts` already uses and for the reason it gives: jsdom has no layout, no scrollports and no `sticky`, so a rendered test cannot tell a scrolling page from a scrolling column. That is now the third defect in this repository of the species *shipped, and invisible to a build, a type-check and a lint run*.

> **W12½ closed.** Nine slices, eight PRs, and the two questions it was opened on are both answered: every screen has a navigation item, and the theme is a choice with light as its default.
>
> **What the numbers moved.** Screens reachable from a navigation item, 6 → 17. Back-office chrome, 224px of sidebar → 254px of rail and panel, with 963px still clear for the widest table. Mobile chrome, 165px → 45px. Hand-written crumb paths, 27 per locale → 8 leaves. Net for slices 3–5 combined: fewer lines than before.
>
> **Three findings the audit did not predict, all from reading or looking rather than reasoning.** The permission model needed **all-of as well as any-of** — two screens are organised by channel and a `product:read` reader would get an empty selector. The breadcrumbs had **already drifted** into a second vocabulary, calling the journeys block *Field ops*, a group this navigation has never had. And the field tab bar, as specified, **changed no reachability at all**, because both of its destinations were already linked from the header — which is why 8a absorbed 8b rather than shipping a restyling under a navigation's name.
>
> **The recurring shape appeared twice more**: `outlets(db)` and its comment "what the outlet list reads", a reader waiting since W8 for a screen; and the four action rows, which held the only door to eleven screens and had **no test between them**. The slice-2 gate is the only thing that would have noticed their removal, which is the evidence for the ordering argument rather than the argument itself.
>
> **And a class of mistake worth naming, because it was mine four times.** A mechanical edit that is uniform in *form* over inputs that differ in *meaning*: a regex ate `href` from three multi-line screen entries and lint passed; the same regex ate nine newlines; a shape-matching replacement dropped five breadcrumb leaves; and a `Write` overwrote a test file that already existed, deleting seven session tests while the suite total stayed put because I had added exactly seven. The habit that catches all four is the same — **assert the expected count before touching the file**, and never read a stable total as evidence.
>
> **Open.** One full-suite run reported a single failure whose name was not captured; fifteen runs since are clean. Recorded rather than closed. The redesign's own claim — that it *looks* right — is still only checked by eye: `shell-layout.test.ts` guards the rule and not the rendering, and `Week 14`'s Playwright work is where that gets an engine underneath it.
| | *~~Smaller than it reads: `unplanned-call.tsx` already renders a searchable list of every in-scope outlet.~~ **The estimate was wrong, and the reason matters more than the estimate.** That picker calls `callableOutlets`, which deliberately **excludes** every shop already on today's round — it answers "where could I add a call?", which is the wrong question for "where is that shop?". So the screen needed a reader of its own rather than a promotion. It happened to already have one: `outlets(db)`, shipped in W8 slice 6, whose own comment calls it "what the outlet list reads" — a reader written for a screen nobody built. Luck, not the promotion I described.* | | |

---

## Phase 4 — Production polish

### Week 13 · Observability + security hardening

**Goal:** the system says what it is doing, and the things the security doc claims are things the
code performs.

- Custom OTel metrics (sync push latency/size, outbox backlog, visits/orders, pricing duration) + dashboards; extended health checks ([observability](architecture/15-observability.md)).
- Rate limiting (`/sync`, auth), security headers, CORS, secrets; verify tenant-isolation tests + the bypass ban; threat-model pass ([security](architecture/16-security.md)).

**Done when:** domain metrics visible; security checklist + isolation tests green.

#### What the audit found, before a line was written

Both docs are marked *✅ Baseline*, which is accurate: the baseline is what `ServiceDefaults` ships
and nothing more. `FieldKit.Server/Extensions.cs` is **the Aspire template, unedited** — ASP.NET,
HttpClient and runtime instrumentation, one `self` liveness check. Every row of
[observability §2](architecture/15-observability.md#2-domain-metrics-custom)'s nine domain metrics is
unbuilt, and so is every "FieldKit adds" cell in §1. That is the week, and it was expected.

Four things were not.

**1. The outbox is written to and never drained.** `OutboxProcessor` claims rows with `FOR UPDATE
SKIP LOCKED`, dispatches, marks processed — and **nothing calls it** outside
`OutboxIntegrationTests`. It is registered as a singleton in `PersistenceExtensions` and injected
nowhere. [ADR-0006](architecture/adr/0006-in-process-messaging-and-outbox.md) says "a background
outbox dispatcher polls unpublished rows and invokes in-process handlers"; there is no
`BackgroundService` in the solution. Nine integration event types are published — `VisitCompleted`,
`OrderSubmitted`, `JourneyPublished`, `PriceListPublished`, `PromotionActivated` among them — into
outbox tables that grow forever. **Nothing is visibly broken**, because no module implements
`IIntegrationEventHandler` yet: outside `BuildingBlocks`, `Infrastructure` and their tests there is
not one handler, so nothing is waiting on delivery. The absence is invisible for exactly as long as
nobody writes the first handler, and then it is a silent no-op rather than an error. This is the CSP
finding's species again — *a control that exists in the prose justifying something else* — and it is
why slice 3 builds the dispatcher rather than shipping a backlog gauge over a queue nothing drains.

**2. There are no health endpoints outside development.** `MapDefaultEndpoints` wraps both
`MapHealthChecks` calls in `if (app.Environment.IsDevelopment())`, with the template's own comment
explaining why. The reasoning is sound and the consequence has not been faced: **W15 deploys to
Container Apps**, which probes an endpoint that will not exist. Slice 5 owns the decision rather than
discovering it during a deploy.

**3. `traceId` is claimed and appears nowhere.**
[Observability §4](architecture/15-observability.md#4-correlation) says one `traceId` "is returned in
every `ProblemDetails`", and `ProblemDetailsExtensions` says "the trace id already points" there.
Whatever the framework emits by default, **no test asserts it, no `.http` request shows it, and no
client reads it** — the string does not occur in this repository. A correlation id nobody has looked
at is a correlation id nobody can quote down the phone.

**4. "CORS locked to known origins" is not a thing this server does.** There is no `AddCors` and no
`UseCors` anywhere. It is very likely *correct* — the API is same-origin behind the front end's
proxy, so a browser never makes a cross-origin call to it — but then the claim is wrong rather than
the code, and the only CORS in the codebase (`PhotoStorageCors`) governs the **storage account**,
which is a different control on a different origin. Slice 7 settles which it is.

#### Decomposition

**Slice 1 lands the meter before any metric needs it**, for the reason W12½ slice 2 landed its gate
first: the naming, the tenant dimension and the cardinality rules are one decision, and nine metrics
authored against nine ad-hoc `Meter` instances is the shape that cannot be corrected later.

**Slices 6–9 are independent of 1–5** and of each other. The security half does not wait on the
observability half; it is numbered second because the metrics are what the week is named for.

**Two slices need explicit sign-off before they start**, per `CLAUDE.md`: slice 6 puts a limiter in
front of the auth paths, and slice 9's isolation pass reads tenant-isolation code. Neither changes an
authorization rule, and both will say so in their PR rather than assuming the exemption.

| # | Slice | Source | ~Size |
|---|---|---|---|
| 0 | **The decomposition and the audit behind it** — this section | — | 250 |
| 1 | **One meter, and the sync signals under it** — `push.batch_size`, `push.latency`, `mutations.rejected` | obs §2 | 280 |
| | *The naming decision and the first three metrics together, so the convention is reviewable against something real. **Tenant is a dimension, not a metric per tenant** — `visits.completed` is documented "by tenant" and an unbounded label is how a metrics backend falls over, so the cardinality argument is made here once and cited afterwards. `/sync/push` is where the doc's own budgets live (§6), which makes it the honest first subject.* | | |
| 2 | **A span a tenant can be found in, and a `traceId` a caller can quote** | obs §1, §4 | 220 |
| | *Spans for sync pull/push, outbox dispatch and pricing resolution, with `tenantId` and `mutationId` as attributes — and the correlation claim made true rather than assumed, with a test that reads the id out of an actual refusal. Finding 3 above is the reason this is a slice and not a line in slice 1.* | | |
| 3 | **The outbox gets the dispatcher ADR-0006 describes** | ADR-0006, obs §2 | 300 |
| | *A hosted service per module context, polling on an interval, claiming with the `SKIP LOCKED` the processor already uses so replicas do not collide. Carries `outbox.backlog` and `dispatch.latency` with it, because a gauge whose alert has no fix is worse than no gauge. **The first real handler will be the test** — at-least-once delivery is a property, and asserting it needs something on the other end.* | | |
| 4 | **The business metrics** — visits, order value, pricing duration, photo backlog | obs §2 | 250 |
| | *The four that make the dashboards in `00-product-overview.md` measurable, emitted from the paths that already exist. `pricing.resolve.duration` is the one with a stated budget (sub-ms on device) and therefore the one worth a threshold.* | | |
| 5 | **Health checks that check something, and exist where they are probed** | obs §3 | 220 |
| | *Postgres, Keycloak reachability and outbox liveness behind `/health`; `self` stays on `/alive`. And finding 2: what non-dev exposure looks like, decided here rather than during a W15 deploy.* | | |
| 6 | **Rate limiting on `/sync` and the auth paths** | sec §6, §7 | 220 |
| | *The DoS row of the threat model, which currently cites a mitigation that does not exist. A reconnect burst of 200 reps at shift start is a **documented normal** (obs §6), so the limit has to be shaped around it rather than tuned against an idle dev box — the interesting test is that a legitimate burst is not refused.* | | |
| 7 | **The API's own headers, and the CORS claim settled** | sec §6 | 160 |
| | *`nosniff` and a referrer policy on API responses, and finding 4 resolved in whichever direction the evidence points: a policy if the API is reachable cross-origin, a corrected sentence and a test pinning same-origin if it is not.* | | |
| 8 | **What a field device says when it is failing quietly** | obs §5 | 320 |
| | *Batched client telemetry — unhandled errors, service-worker failures, storage-eviction and quota events, failed-sync reasons — posted on reconnect with `deviceId`. The argument is the doc's own: there is no SSH into a field fleet, and a device that has silently stopped syncing looks identical to a rep having a quiet week. **No location, ever** (§5), which is a rule the endpoint enforces rather than a promise the client keeps.* | | |
| 9 | **The threat-model pass, and the isolation claims re-proven** | sec §3, §7 | 200 |
| | *Every row of STRIDE-lite walked against the code that is supposed to mitigate it — the exercise that produced findings 1 and 4 above, done deliberately and written down. The bypass ban and the tenant filter have tests; what they lack is a statement of which threat each one answers.* | | |

**Not in W13.** Formal pen-test, SSO/SCIM and field-level encryption stay out, as
[security §8](architecture/16-security.md#8-out-of-scope-v1-stated-honestly) already says. Dashboards
are exported *definitions* rather than a hosted Grafana — the Aspire dashboard renders these in dev,
and a real backend arrives with W15's deploy.

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
