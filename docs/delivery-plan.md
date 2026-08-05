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
  (`Catalog`, `POST/GET /api/products`) replacing `WeatherForecast`; end-to-end verified with
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
