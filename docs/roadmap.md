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

## Phase 0 — Foundation *(in progress)*

Turn the scaffold into a clean skeleton the rest hangs off.

- [x] Aspire solution scaffolded (AppHost + Server + Redis)
- [x] **Documentation & design complete** — product specs, [decisions & assumptions](product/decisions-and-assumptions.md),
  full architecture + 11 ADRs, [wireframes](ux/README.md) (12 screens)
- [ ] Module hosting pattern: empty-but-real modules + **`SharedKernel`** (Money, GeoPoint,
  `IClock`, Result, typed ids) and **`BuildingBlocks`** (in-process bus, transactional outbox,
  **per-tenant row-version stamping**, tenancy filter, audit interceptor) ([ADR-0006](architecture/adr/0006-in-process-messaging-and-outbox.md)) —
  the row-version primitive lands here so modules built later carry it natively, not as a retrofit
- [ ] Add **PostgreSQL** to the AppHost; EF Core base, migrations, **schema-per-module** wiring
  ([ADR-0005](architecture/adr/0005-postgres-schema-per-module.md))
- [ ] **Migrate the front end Vite → Next.js** (App Router) + **shadcn/ui & design tokens**
  ([ADR-0004](architecture/adr/0004-nextjs-offline-first-frontend.md)); **next-intl** i18n scaffold
  ([ADR-0010](architecture/adr/0010-internationalization.md)); PWA shell; re-wire in the AppHost
- [ ] Add **Keycloak** container to the AppHost ([ADR-0008](architecture/adr/0008-authentication-and-multitenancy.md))
- [ ] **Architecture-test** project (NetArchTest) enforcing empty-but-real boundaries
- [ ] **Seed/demo-data** harness (a believable tenant) so every phase is demoable
- [ ] CI: build + test + arch-test on GitHub Actions

**Demo:** the app boots via Aspire, one health-checked module answers, dashboard shows traces.

## Phase 1 — Admin core: identity, org, outlets

The back office comes first because the field app has nothing to show without master data.

- [ ] **IAM** — tenants, users, roles, permissions; **Keycloak (OIDC, realm-per-tenant)** login;
  JWT to the API ([ADR-0008](architecture/adr/0008-authentication-and-multitenancy.md))
- [ ] **Multi-tenancy** enforced end to end (global query filter + `ITenantContext` + arch-test ban on bypass)
- [ ] **Organization** — org hierarchy, territories, assign reps to territories
- [ ] **Outlets** — CRUD the retail universe: channels, segments, geo, contacts
- [ ] **Configuration module** (10th) — field-definition catalog + JSONB values + validation;
  owns visit-workflow / survey / weight definitions as snapshot-versioned reference config
  ([ADR-0009](architecture/adr/0009-config-driven-customization.md))
- [ ] Back-office **UI shell** (shadcn) with Outlets, Territories, Users & Roles screens

**Demo:** an admin logs in, models a small org, and loads a set of outlets — all tenant-scoped.

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
- [ ] Deploy to **Azure Container Apps** via `aspire deploy` ([ADR-0011](architecture/adr/0011-deployment-azure-container-apps.md))
- [ ] Case-study polish: screenshots/GIFs in the README

## Out of scope

See [product overview §6](product/00-product-overview.md#6-scope--non-goals). Notably: native
mobile, ERP/fulfillment, route optimization, BI/warehouse, payments.
