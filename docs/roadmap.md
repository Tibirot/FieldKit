# Roadmap

> **Status:** ✅ Baseline · **Last updated:** 2026-08
> **Execution detail:** week-sized work packages in [delivery-plan.md](delivery-plan.md).

FieldKit is **spec-complete by design, built incrementally by phase.** The documentation
describes the full platform (field ops + admin); the *build* walks a sequence of thin,
demoable vertical slices so there is always something running end to end. Each phase leaves
the system deployable.

Phase tags (`Phase 1`, etc.) referenced in the functional docs' MoSCoW tables map here. This
is the **phase-level** view; the **[delivery plan](delivery-plan.md)** breaks each phase into
~1-week work packages.

## Guiding principles

- **Vertical slices, not horizontal layers.** Each phase ships a capability all the way from
  Next.js UI through a module to Postgres — never "all the backend, then all the frontend".
- **Offline early.** The sync engine lands in Phase 2, not bolted on at the end, because it
  shapes every field module's data model.
- **Boundaries from day one.** Architecture tests exist before there are two modules to
  keep apart.

## Phase 0 — Foundation *(complete, one item deferred)*

Turn the scaffold into a clean skeleton the rest hangs off.

The one unticked box is **row-version stamping**, and it is deferred rather than outstanding: its
only consumer is the sync engine, so building it now would mean shipping a primitive designed
against a protocol that does not exist yet. It lands with the W8 sync slices.

- [x] Aspire solution scaffolded (AppHost + Server + Redis)
- [x] **Documentation & design complete** — product specs, [decisions & assumptions](product/decisions-and-assumptions.md),
  full architecture + 11 ADRs, [wireframes](ux/README.md) (12 screens)
- [x] **`SharedKernel`** (Money, GeoPoint, `IClock`, Result, TenantId) + **`BuildingBlocks`**
  (pure abstractions: messaging contracts, `ITenantContext`, `ITenantOwned`/`IAuditable`) +
  **NetArchTest** harness with **AT-7** enforced at compile time (banned-API analyzer) — *[PR #2]*.
  That analyzer initially reached only these two projects; it is now applied solution-wide alongside
  **AT-9** (no `IgnoreQueryFilters` / `ExecuteSqlRaw` in production code) via `Directory.Build.props`.
- [x] Add **PostgreSQL** to the AppHost + **`Infrastructure`**: EF Core base `ModuleDbContext`
  (**schema-per-module**, ADR-0005), the tenant query filter + stamping interceptors, verified on
  real Postgres (Testcontainers) — *[this slice]*
- [x] **Transactional outbox + in-process dispatch** ([ADR-0006](architecture/adr/0006-in-process-messaging-and-outbox.md)):
  `AggregateRoot` raises integration events → written to a per-module `outbox_message` table in the
  same transaction (interceptor) → `OutboxProcessor` claims with `FOR UPDATE SKIP LOCKED` and
  delivers to handlers, idempotently. Verified on real Postgres — *[messaging slice]*
- [x] **Module hosting** (`IModule` self-registration in `FieldKit.Web`) + the **first real module
  (`Catalog`)** replacing the sample `WeatherForecast`. The AppHost boots the whole thing on
  Postgres and `POST/GET /api/products` answers from the module — verified end-to-end with
  `WebApplicationFactory<Program>` + real Postgres. A temporary `DevTenantContext` stood in until
  Keycloak landed (below). — *[module-hosting slice]* **← the modular monolith now runs.**
- [ ] **Per-tenant row-version stamping** (the `IReferenceChangeFeed` primitive) — lands with the sync slices
- [x] **Per-module EF migrations** — `ModuleMigrator<TContext>` applies each module's migrations on
  startup (`MigrateAsync`); each keeps its own `__EFMigrationsHistory` in its own schema, so contexts
  sharing the database don't collide. Replaces the temporary `EnsureCreated`; verified end-to-end.
- [x] **Front end Vite → Next.js** (App Router, TS, ESLint) scaffolded + **re-wired in the AppHost**
  (`AddJavaScriptApp` runs Next as its own app; `output: "standalone"` for containers) — replaces the
  Vite SPA + wwwroot publish ([ADR-0004](architecture/adr/0004-nextjs-offline-first-frontend.md)) — *[this slice]*
- [x] **shadcn/ui + Tailwind v4 + FieldKit design tokens** (teal accent, cool-slate neutrals,
  light/dark; Button/Card/Badge) — the design system from the [wireframes](ux/README.md) is now real
  ([A7](product/decisions-and-assumptions.md#a7--ui-toolkit-shadcnui--tailwind)) — *[this slice]*
- [x] **next-intl** i18n scaffold ([ADR-0010](architecture/adr/0010-internationalization.md)) —
  `en` + `ro` catalogs, always-prefixed locale routing (`/en`, `/ro`) via the Next proxy, locale
  switcher, localized 404, and a **catalog-parity test** (keys, ICU arguments, CLDR plural
  categories) that fails the build on drift — *[this slice]*
- [x] **Offline PWA shell** — installable and offline-bootable (`OFF-10`): a **per-locale web-app
  manifest**, app icons (incl. maskable), and a **Workbox service worker** built by a post-`next build`
  step rather than a webpack plugin, so the app keeps Turbopack builds
  ([ADR-0004](architecture/adr/0004-nextjs-offline-first-frontend.md#building-the-service-worker-phase-0)).
  Precaches the hashed build output plus one **offline shell per locale**; navigations are
  network-first and fall back to the shell **in the right language**. Requests persistent storage
  ([offline behavior §2](product/30-offline-behavior.md)). The API is never cached — offline *data*
  is the Phase-2 sync engine's job. — *[this slice]*
- [x] Add **Keycloak** container to the AppHost ([ADR-0008](architecture/adr/0008-authentication-and-multitenancy.md)) —
  the container runs with the **dev tenant realm imported from source** (a tenant *is* a realm under
  realm-per-tenant), and the API **validates the JWT bearer** it issues: signature, issuer, audience
  and lifetime, against the real realm in the integration tests. `GET /api/auth/whoami` is the one
  endpoint that requires a token today. **`ITenantContext` is now derived from the token** — the
  tenant comes from the `tenant` claim and nowhere else, a token without a usable one is rejected at
  validation, and business endpoints require a `resource:action` permission. `DevTenantContext` and
  its `X-Tenant-Id` header override are gone.
- [x] **Architecture-test** project (NetArchTest) enforcing empty-but-real boundaries —
  `FieldKit.ArchitectureTests` runs the **foundation subset**: dependencies point inward
  (`SharedKernel` knows nothing of `BuildingBlocks`), and neither the kernel nor the building blocks
  may reference ASP.NET Core or EF Core (AT-8). **AT-7** ("no static time; `IClock` only") is enforced
  a step earlier still, at *compile* time, by the banned-API analyzer. The module-boundary rules
  **AT-1…AT-6** land with the second module — there is nothing to keep apart until then
  ([module boundaries §5](architecture/10-module-boundaries.md#5-enforcement--architecture-tests)).
- [x] CI: build + test + arch-test on GitHub Actions — [`ci.yml`](../.github/workflows/ci.yml) runs
  two jobs on every PR and push to `main`: **dotnet** (restore → build → `dotnet test`, which covers
  unit, architecture and the Testcontainers integration suite on real Postgres) and **frontend**
  (`npm ci` → lint → build → Vitest). Both are **required status checks** in branch protection with
  *require branches to be up to date* on, so the gate is enforced rather than merely present.

**Demo:** the app boots via Aspire, one health-checked module answers, dashboard shows traces.

## Phase 1 — Admin core: identity, org, outlets *(complete)*

The back office comes first because the field app has nothing to show without master data.

- [x] **IAM** — tenants, users, roles, permissions; **Keycloak (OIDC, realm-per-tenant)** login;
  JWT to the API ([ADR-0008](architecture/adr/0008-authentication-and-multitenancy.md))
- [x] **Multi-tenancy** enforced end to end (global query filter + `ITenantContext` + arch-test ban on bypass)
- [x] **Organization** — org hierarchy, territories, assign reps to territories
- [x] **Outlets** — CRUD the retail universe: channels, segments, geo, contacts
- [x] **Configuration module** (10th) — field-definition catalog + JSONB values + validation;
  owns visit-workflow / survey / weight definitions as snapshot-versioned reference config
  ([ADR-0009](architecture/adr/0009-config-driven-customization.md))
- [x] Back-office **UI shell** (shadcn) with Outlets, Territories, Users & Roles screens

**Demo:** an admin logs in, models a small org, and loads a set of outlets — all tenant-scoped.

Demoed, and the demo is what completed it. Walking the phase end to end turned up **five screens
that did not exist behind endpoints that did** — org units, outlet↔territory membership, trade
channels, the custom-field catalogue and the outlet lifecycle. Each was built, tested and reachable
by `.http` request, and each was invisible in review because nothing in the codebase points from an
endpoint to the screen that ought to call it. The habit that found them — asking what an admin would
press, in a tenant with no test leftovers — is worth carrying into every phase after this one.

## Phase 2 — Field ops online: journeys, visits, and the sync engine

The heart of the product. Build the field flow *and* the sync engine together.

- [ ] **Products & Pricing** — catalog, assortments/MSL, price lists, promotions
- [ ] **Shared pricing engine** — deterministic resolver in C# **and** its TypeScript device
  mirror, pinned by cross-language parity tests ([BR-PRD-7](product/13-products-and-pricing.md#5-business-rules))
- [ ] **Journey** — generate a rep's daily journey from frequency + territory
- [ ] **Visit** — check-in/out, geofence, config-driven guided steps
- [ ] **Sync engine v1** — row-version delta pull of reference data; idempotent outbox push of
  visits; device registry ([ADR-0007](architecture/adr/0007-offline-sync-strategy.md),
  [sync deep dive](architecture/12-offline-sync-engine.md))
- [ ] **Field PWA** — installable, service worker, IndexedDB local store, sync manager

**Demo:** a rep syncs a journey, goes **offline**, completes visits, reconnects, and the back
office sees the results.

## Phase 3 — In-store depth: audits and orders

- [ ] **Audit** — structured shelf capture: MSL availability, **share-of-shelf**, price check,
  photos, and the configurable **weighted perfect-store score**
  ([A2](product/decisions-and-assumptions.md#a2--audit--perfect-store-structured-checks--share-of-shelf--photo)) —
  *not* coordinate planograms ([`AUD-10 = Won't v1`](product/22-merchandising-and-audits.md#6-requirements))
- [ ] **Order** — capture against assortment & price, on-device promotion application, lifecycle
- [ ] **Config-driven builder** — visit-workflow steps, perfect-store weights, survey forms
  ([ADR-0009](architecture/adr/0009-config-driven-customization.md))
- [ ] **Sync engine v2** — conflict rules for transactional data; out-of-band photo upload
- [ ] **Supervisor dashboards** — coverage & compliance (reporting read-side)

**Demo:** the full golden path from the [product overview](product/00-product-overview.md#5-a-day-in-the-life-the-golden-path),
offline, reconciled on reconnect.

## Phase 4 — Production polish *(stretch)*

- [ ] Observability: custom domain metrics + dashboards (visits synced, sync latency, order value)
  ([observability](architecture/15-observability.md))
- [ ] Security hardening + threat-model verification ([security](architecture/16-security.md))
- [ ] E2E suite (Playwright) covering the golden path online **and** offline
- [ ] **Seed/demo-data** harness (a believable tenant) so the live demo has something to show —
  scheduled with the E2E suite in [W14](delivery-plan.md), which is what needs a deterministic
  fixture. It sat under Phase 0 until the pre-Phase-2 audit found it listed in two phases at once;
  every phase through 3 has been demoed on hand-entered data, so it was never the blocker Phase 0
  implied it was.
- [ ] Deploy to **Azure Container Apps** via `aspire deploy` ([ADR-0011](architecture/adr/0011-deployment-azure-container-apps.md))
- [ ] Case-study polish: screenshots/GIFs in the README

## Out of scope

See [product overview §6](product/00-product-overview.md#6-scope--non-goals). Notably: native
mobile, ERP/fulfillment, route optimization, BI/warehouse, payments.
