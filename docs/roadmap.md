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

## Phase 0 — Foundation *(complete)*

Turn the scaffold into a clean skeleton the rest hangs off.

**Row-version stamping** was the one box left open here, and it was deferred rather than outstanding:
its only consumer is the sync engine, so building it in Phase 0 would have meant shipping a primitive
designed against a protocol that did not exist yet. It landed with the W8 sync slices, and the shape
it took — a transactional counter rather than a sequence — needed a decision of its own
([ADR-0013](architecture/adr/0013-sync-row-version.md)).

- [x] Aspire solution scaffolded (AppHost + Server + Redis — *Redis was removed before deploying: it
  backed an output cache nothing ever opted into, and would have cost more per month than the
  database it fronted*)
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
  (`Catalog`, renamed `Products` before W6 — see
  [module boundaries §7](architecture/10-module-boundaries.md#7-module-registry))** replacing the
  sample `WeatherForecast`. The AppHost boots the whole thing on
  Postgres and `POST/GET /api/products` answers from the module — verified end-to-end with
  `WebApplicationFactory<Program>` + real Postgres. A temporary `DevTenantContext` stood in until
  Keycloak landed (below). — *[module-hosting slice]* **← the modular monolith now runs.**
- [x] **Per-tenant row-version stamping** (the `IReferenceChangeFeed` primitive) — landed with the W8
  sync slices as the deferral above intended; what the counter means is
  [ADR-0013](architecture/adr/0013-sync-row-version.md)
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

## Phase 2 — Field ops online: journeys, visits, and the sync engine *(complete)*

The heart of the product. Build the field flow *and* the sync engine together.

- [x] **Products & Pricing** — catalog, assortments/MSL, price lists, promotions *(W6)*
- [x] **Shared pricing engine** — deterministic resolver in C# **and** its TypeScript device
  mirror, pinned by cross-language parity tests ([BR-PRD-7](product/13-products-and-pricing.md#5-business-rules)) *(W7)*
- [x] **Journey** — generate a rep's daily journey from frequency + territory *(W7)*
- [x] **Visit** — check-in/out, geofence, config-driven guided steps *(W7)*
- [x] **Sync engine v1** — row-version delta pull of reference data; idempotent outbox push of
  visits; device registry ([ADR-0007](architecture/adr/0007-offline-sync-strategy.md),
  [sync deep dive](architecture/12-offline-sync-engine.md)) *(W8)*
- [x] **Field PWA** — installable, service worker, IndexedDB local store, sync manager *(W9)*

**Demo:** a rep syncs a journey, goes **offline**, completes visits, reconnects, and the back
office sees the results. Walked online at the end of W8; the **offline half is verifiable only on a
real device over HTTPS** — geolocation, the install prompt and the service worker all are — which is
part of why the deployed environment matters and is queued against it.

The engineering lesson of the phase was **writing the same rules twice on purpose**. The pricing
resolver exists in C# and TypeScript because a rep pricing an order offline must reach the number the
server would, and no amount of care makes two independent implementations agree by inspection. The
answer was a shared corpus of vectors — hand-written for the arguable cases, generated for volume —
that both languages read and CI compares. It has since caught real divergence rather than
hypothetical: a wrong hand-written expectation, and a serialization bug the first time the wire
vectors ran.

## Phase 3 — In-store depth: audits and orders *(complete, with one requirement open — `CFG-03`)*

- [x] **Audit** — structured shelf capture: MSL availability, **share-of-shelf**, price check,
  photos, and the configurable **weighted perfect-store score**
  ([A2](product/decisions-and-assumptions.md#a2--audit--perfect-store-structured-checks--share-of-shelf--photo)) —
  *not* coordinate planograms ([`AUD-10 = Won't v1`](product/22-merchandising-and-audits.md#6-requirements)) *(W10 — the
  engine, the score in both languages, and the push path; the **capture screen** is W11)*
- [x] **Order** — capture against assortment & price, on-device promotion application, lifecycle *(W11)*
- [ ] **Config-driven builder** — visit-workflow steps, perfect-store weights, survey forms
  ([ADR-0009](architecture/adr/0009-config-driven-customization.md)) — *weights (W10 slice 8) and
  survey forms (W10 slices 9a/9b) shipped as their own screens; the **per-channel visit-workflow step
  builder** (`CFG-03`) has an API and no screen, and is **[W14](delivery-plan.md#week-14--e2e--seed-data--the-workflow-builder-)*** — see below
- [x] **Sync engine v2** — conflict rules for transactional data; out-of-band photo upload *(W11)*
- [x] **Supervisor dashboards** — coverage & compliance (reporting read-side) *(W12 — the four
  aggregate reads, the composition in the host, the dashboard, and the visits/orders review screens)*

**W10's shape is worth recording**, because it is the one the plan got wrong twice. The audit
*engine* and the audit *screen* are different weeks: the score, its weighting, the parity vectors and
the push path all landed without a single pixel, and the form a rep fills in at a shelf lands with
order capture in W11 — the two are the same offline-screen problem and sharing a week would have
meant solving it twice. Both surviving W10 slices split (3a/3b, 9a/9b) split on a rule the *server*
imposed rather than on size.

**`CFG-03` is the one requirement this phase did not close, and two documents disagreed about it.**
This page said the visit-workflow step builder was W12; W12's own decomposition said the
config-builder bullet had nothing left in it, counting the weights screen and the survey editor as the
whole of it. Both were written down, neither was reconciled, and W12 closed without the screen.
What exists is the server half — `GET/PUT/DELETE /api/config/visit-workflows/{channelId}` since W7
slice 6, row-versioned and on the change feed — so a tenant's workflows are authored by request and
not by anybody in the back office
([configuration §6.5](product/14-configuration.md#65-authoring-visit-workflows-not-yet-built)).
**It is now [W14](delivery-plan.md#week-14--e2e--seed-data--the-workflow-builder-)**, and the way that
date was set is the point. This page originally proposed leaving it unscheduled — a `Must` that has
slipped once is not made safer by a second date written into the same document that got the first one
wrong. The counter-argument won: *unscheduled* is how a `Must` becomes a `Won't` by attrition, and the
gap here is one screen against an API that has been complete since W7. A date somebody chose, knowing
the first one failed, is a different object from a date nobody revisited.

The mechanism that catches this class of thing does not reach it. The reachability gate
([`check-reachability.mjs`](../scripts/check-reachability.mjs)) checks that every back-office *route*
has a navigation item and every navigation item a route — it cannot see an **endpoint** with no route
at all, which is the shape of this gap.

**Demo:** the full golden path from the [product overview](product/00-product-overview.md#5-a-day-in-the-life-the-golden-path),
offline, reconciled on reconnect.

## Phase 4 — Production polish *(in progress — W13 landed; W14 and W15 remain)*

- [x] Observability: custom domain metrics + dashboards (visits synced, sync latency, order value)
  ([observability](architecture/15-observability.md)) — *(W13 — eleven instruments under one meter, the
  outbox dispatcher `ADR-0006` describes, spans carrying tenant and mutation, dependency health checks,
  and batched device telemetry)*
- [x] Security hardening + threat-model verification ([security](architecture/16-security.md)) —
  *(W13 — a per-rep rate limit on `/sync`, the API's own security headers, the CORS claim settled as a
  corrected sentence rather than a policy, and a STRIDE-lite table `ThreatModelTests` parses so a
  mitigation cannot be renamed while this doc goes on asserting it)*
- [ ] E2E suite (Playwright) covering the golden path online **and** offline *(W14 — not started;
  there is no Playwright suite and no E2E job in CI today)*
- [ ] **Seed/demo-data** harness (a believable tenant) so the live demo has something to show —
  scheduled with the E2E suite in [W14](delivery-plan.md), which is what needs a deterministic
  fixture. It sat under Phase 0 until the pre-Phase-2 audit found it listed in two phases at once;
  every phase through 3 has been demoed on hand-entered data, so it was never the blocker Phase 0
  implied it was.
- [x] Deploy to **Azure Container Apps** via `aspire deploy` ([ADR-0011](architecture/adr/0011-deployment-azure-container-apps.md)) —
  *done early, in the middle of Phase 2, and that was the right call: it turned "publishes cleanly"
  from a claim into a fact and found three things a manifest review had not (a bind-mounted realm
  directory naming a Windows path, every OIDC origin hardcoded to `localhost:3000`, and a Keycloak
  that could not see its own public address). Redeployed with W9+W10 on 2026-08-11 —
  see the [runbook](engineering/deploying.md)*
- [ ] Case-study polish: screenshots/GIFs in the README *(W15)*

## Out of scope

See [product overview §6](product/00-product-overview.md#6-scope--non-goals). Notably: native
mobile, ERP/fulfillment, route optimization, BI/warehouse, payments.
