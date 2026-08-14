# FieldKit — Documentation

FieldKit is a **Sales Force Automation (SFA) platform** for field sales teams in the
FMCG/CPG trade channel. It is built as a **modular monolith** on **.NET Aspire** with a
**Next.js** offline-first front end.

This folder is the single source of truth for *what* FieldKit does (functional docs) and
*how* it is built and why (technical docs + decision records). It is written to be read by
two audiences at once:

- an **engineer** who needs to build, extend, or review the system, and
- a **reviewer / hiring manager** who wants to understand the thinking without reading code.

> New here? Read the [case study](../README.md) first for the 5-minute narrative, then come
> back for depth.

---

## How the docs are organised

```
docs/
├─ README.md                     ← you are here (index + roadmap)
├─ product/                      ← functional documentation (the "what")
│  ├─ 00-product-overview.md     ← vision, personas, capability map, scope, glossary
│  ├─ decisions-and-assumptions.md ← resolved forks + drafted domain mechanics (read early)
│  ├─ 10-identity-and-access.md
│  ├─ 11-organization-and-territory.md
│  ├─ 12-outlets-master-data.md
│  ├─ 13-products-and-pricing.md
│  ├─ 14-configuration.md         ← per-tenant customization (fields, workflows, forms, weights)
│  ├─ 20-journey-planning.md
│  ├─ 21-visit-execution.md
│  ├─ 22-merchandising-and-audits.md
│  ├─ 23-order-capture.md
│  └─ 30-offline-behavior.md     ← offline UX & rules from the user's point of view
│
├─ architecture/                 ← technical documentation (the "how" and "why")
│  ├─ 00-architecture-overview.md ← C4 context/container, module map, stack, topology
│  ├─ 10-module-boundaries.md     ← how modules are isolated and communicate
│  ├─ 11-domain-model.md          ← aggregates, entities, key invariants per module
│  ├─ 12-offline-sync-engine.md   ← the sync protocol & conflict resolution (deep dive)
│  ├─ 13-api-contracts.md         ← REST/RPC surface, versioning, error model
│  ├─ 14-data-and-persistence.md  ← schema-per-module, migrations, multi-tenancy
│  ├─ 15-observability.md         ← OpenTelemetry, health, the Aspire dashboard
│  ├─ 16-security.md              ← authn/authz, tenant isolation, threat model
│  ├─ 17-testing-strategy.md      ← unit / integration / arch-tests / E2E
│  └─ adr/                        ← Architecture Decision Records
│     ├─ README.md                ← ADR index
│     ├─ 0001-record-architecture-decisions.md
│     ├─ 0002-modular-monolith.md
│     ├─ 0003-adopt-dotnet-aspire.md
│     ├─ 0004-nextjs-offline-first-frontend.md
│     ├─ 0005-postgres-schema-per-module.md
│     ├─ 0006-in-process-messaging-and-outbox.md
│     ├─ 0007-offline-sync-strategy.md
│     ├─ 0008-authentication-and-multitenancy.md
│     ├─ 0009-config-driven-customization.md
│     ├─ 0010-internationalization.md
│     ├─ 0011-deployment-azure-container-apps.md
│     └─ 0012-server-message-localization.md
│
├─ engineering/                  ← how we work
│  ├─ pull-requests.md           ← PR rules for humans & agents (small · tested · docs-in-lockstep)
│  ├─ frontend-toolchain.md      ← Node/npm pinning + why the lockfile is generated on Linux
│  ├─ deploying.md               ← the ACA runbook: prerequisites, what gets created, what to check
│  ├─ phase-2-demo.md            ← the offline field round, scripted: what to show and what it proves
│  ├─ regression-2026-08-13.md   ← the post-W11 whole-app pass: every gate, and the nine gaps it found
│  ├─ regression-2026-08-14.md   ← the post-W11½ pass: W11½ verified, five new gaps, all the same shape
│  └─ r6-business-day.md         ← the outlet's zone decides the pricing day: the two decisions, and what shipped
│
├─ ux/                           ← wireframes & design direction
│  ├─ README.md                  ← screen inventory + spec traceability + Artifact link
│  └─ wireframes.html            ← self-contained mockups (source)
│
├─ roadmap.md                    ← phase-level plan (spec-complete → built incrementally)
└─ delivery-plan.md              ← execution view: ~1-week work packages, sized & sequenced
```

**Conventions**

- Diagrams are [Mermaid](https://mermaid.js.org/) so they render on GitHub and stay in git.
- Architecture decisions are captured as [ADRs](architecture/adr/README.md); when a decision
  changes, we add a new ADR that supersedes the old one rather than editing history.
- Requirements use MoSCoW (**Must / Should / Could / Won't**) and are tagged with the
  delivery phase from [roadmap.md](roadmap.md).

---

## Documentation roadmap

Legend: ✅ written · 🚧 draft in progress · ⬜ planned

### Functional (product/)
- ✅ Product overview — vision, personas, capability map, scope & non-goals, glossary
- ✅ Decisions & assumptions — resolved forks (customization, audit, i18n, offline, auth, hosting, UI, sync) + drafted domain mechanics
- ✅ Identity & access — users, roles, permissions
- ✅ Organization & territory — org hierarchy, territories, route assignment
- ✅ Outlets master data — the trade universe (stores/POS)
- ✅ Products & pricing — catalog, assortments, price lists, promotions
- ✅ Configuration — per-tenant custom fields, visit workflows, survey forms, perfect-store weights
- ✅ Journey planning — call schedules, visit frequency, calendar
- ✅ Visit execution — the in-store visit lifecycle
- ✅ Merchandising & audits — perfect store, share-of-shelf, surveys, photos
- ✅ Order capture — order taking, promotions, order lifecycle
- ✅ Offline behavior — what works offline and how it reconciles

### Technical (architecture/)
- ✅ Architecture overview — C4, module map, stack, deployment topology
- ✅ Module boundaries — isolation rules, contracts, in-process messaging, arch-tests
- ✅ Domain model — aggregates & invariants per module
- ✅ Offline sync engine — protocol, watermarks, outbox, conflict resolution
- ✅ API contracts — surface, versioning, error model, idempotency
- ✅ Data & persistence — schema-per-module, migrations, tenancy
- ✅ Observability — OpenTelemetry, custom metrics, health, dashboard
- ✅ Security — authn/authz, tenant isolation, threat model
- ✅ Testing strategy — the test pyramid + architecture tests

### Decision records (architecture/adr/)
- ✅ 0001 Record architecture decisions
- ✅ 0002 Modular monolith
- ✅ 0003 Adopt .NET Aspire (+ Azure Container Apps deploy target)
- ✅ 0004 Next.js offline-first front end (+ shadcn/Tailwind, PWA, i18n)
- ✅ 0005 PostgreSQL, schema-per-module
- ✅ 0006 In-process messaging & transactional outbox
- ✅ 0007 Offline sync strategy (territory-scoped, one-device, conflict matrix)
- ✅ 0008 Authentication & multi-tenancy (Keycloak, realm-per-tenant)
- ✅ 0009 Config-driven customization model
- ✅ 0010 Internationalization (currency & language)
- ✅ 0011 Deployment target: Azure Container Apps
- ✅ 0012 Server message localization (refusal codes resolved client-side)

### UX (ux/)
- ✅ Wireframes — field-app golden path (5 screens) + back office (7 views: dashboard, outlets, products/pricing, territories, journey planning, users/roles, workflow builder), with an interactive Artifact

### Delivery
- ✅ Roadmap — phase-level plan mapping features to build increments
- ✅ Delivery plan — 15 week-sized work packages (~12–15h each), sequenced with demos
