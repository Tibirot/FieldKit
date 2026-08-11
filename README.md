# FieldKit

**A Sales Force Automation (SFA) platform for FMCG field sales — built as a modular monolith
on .NET Aspire with an offline-first Next.js front end.**

> Field sales reps walk into stores with no signal, check the shelf, fix what's wrong, and
> take the next order. FieldKit is the tool in their hand and the platform their managers run
> the operation from — and it keeps working when the network doesn't.

<!-- badges: build · coverage · license — add once CI is wired -->

**[Live demo](https://webfrontend.jollysmoke-c6d79515.swedencentral.azurecontainerapps.io)** — Azure
Container Apps, Sweden Central. Sign in with workspace `fieldkit-dev`. It scales to zero when idle,
so the first request after a quiet spell takes a few seconds.

> The demo database is **empty on purpose**: schema comes from migrations and the only seeded rows
> are the tenants and their role templates. Nothing seeds outlets or products, so the screens show
> their empty states rather than a fixture nobody chose.

---

## Why this project

I build SFA software professionally for FMCG multinationals. FieldKit is a from-scratch take
on that domain, built to demonstrate **architecture and full-stack engineering** end to end,
and to go deep on three things that make SFA genuinely hard and genuinely interesting:

- **Modular monolith** — ten domain modules with enforced boundaries, own schemas, and a
  microservices-ready seam, kept honest by architecture tests.
- **Offline-first sync** — a purpose-built sync engine: local store, outbox, delta pull,
  idempotent push, documented conflict resolution. Reps work fully offline; the platform
  reconciles on reconnect.
- **Cloud-native .NET with Aspire** — the API, front end, PostgreSQL and Keycloak composed and
  observed through .NET Aspire, with OpenTelemetry traces/metrics/logs out of the box, and deployed
  to Azure Container Apps from the same AppHost.

The domain isn't decoration: field sales is offline, multi-tenant, and rules-heavy, so it
*earns* this architecture instead of over-engineering a toy.

## Architecture at a glance

```mermaid
flowchart TB
  subgraph client["Next.js — installable PWA (field app + back office)"]
    ui["React 19 · App Router"]
    sw["Service worker · IndexedDB<br/>local store · outbox"]
  end

  subgraph server["FieldKit.Server — modular monolith (ASP.NET Core, .NET 10)"]
    mods["IAM · Organization · Outlets · Products · Configuration<br/>Journey · Visit · Audit · Order · Sync"]
    bus["In-process bus + transactional outbox"]
  end

  pg[("PostgreSQL<br/>schema-per-module")]
  kc["Keycloak<br/>realm-per-tenant"]
  host["FieldKit.AppHost — .NET Aspire"]

  ui <-->|HTTPS/JSON| mods
  ui <-->|OIDC| kc
  sw <-->|delta pull · outbox push| mods
  mods --> bus --> pg
  mods --> pg
  mods -->|validates JWT| kc
  host -.->|orchestrates + observes| server
  host -.->|orchestrates| client
  host -.->|provisions| pg
  host -.->|provisions| kc
```

> **No cache tier, deliberately.** Redis was here from W1 backing an output cache nothing ever opted
> into, and it was removed rather than deployed — a container app costing more per month than the
> database it fronted, serving no reader ([ADR-0011](docs/architecture/adr/0011-deployment-azure-container-apps.md)).
> Idempotency lives in Postgres, where the mutation ledger it guards already is.

Full write-up: **[docs/architecture/00-architecture-overview.md](docs/architecture/00-architecture-overview.md)**.

## Tech stack

| Area | Stack |
|---|---|
| Orchestration | .NET Aspire (AppHost) |
| Backend | ASP.NET Core Minimal APIs · .NET 10 · EF Core · FluentValidation |
| Data | PostgreSQL (schema-per-module) |
| Identity | Keycloak — OIDC, realm-per-tenant |
| Messaging | In-process bus + transactional outbox |
| Frontend | Next.js (App Router) · React 19 · TypeScript · TanStack Query |
| Offline | Service worker (Workbox) · IndexedDB (Dexie) · PWA |
| Observability | OpenTelemetry → Aspire dashboard |
| Testing | xUnit · Testcontainers · NetArchTest · Playwright · Vitest |
| CI/CD | GitHub Actions |

## Documentation

FieldKit is documented as a first-class deliverable — functional specs, technical design,
and decision records:

- 📘 **[Documentation index](docs/README.md)** — start here
- 🎯 **[Product overview](docs/product/00-product-overview.md)** — vision, personas, capabilities, scope
- 🏛️ **[Architecture overview](docs/architecture/00-architecture-overview.md)** — C4, module map, stack
- 🧭 **[Decision records (ADRs)](docs/architecture/adr/README.md)** — the "why" behind every big call
- 🖼️ **[Wireframes](docs/ux/README.md)** — the field-app golden path + back office ([interactive](https://claude.ai/code/artifact/e97b6c9d-43bb-4631-aae9-3c95104a12d0))
- 🗺️ **[Roadmap](docs/roadmap.md)** — what's built and what's next

## Repository layout

```
FieldKit/
├─ FieldKit.AppHost/         # .NET Aspire orchestration (composition root)
├─ FieldKit.Server/          # the host — composes modules, no domain logic
├─ FieldKit.Web/             # module-hosting abstraction (IModule)
├─ FieldKit.SharedKernel/    # value objects (Money, GeoPoint, IClock, Result, TenantId)
├─ FieldKit.BuildingBlocks/  # pure abstractions (messaging, tenancy, AggregateRoot)
├─ FieldKit.Infrastructure/  # EF base (schema-per-module), interceptors, outbox
├─ FieldKit.Modules.*/       # the domain modules — Iam, Org, Outlets, Products, Configuration,
│                            # Journey, Visit, Audit, Sync (+ Order, W11). Each with a
│                            # `.Contracts` sibling: the only thing another module may reference
├─ frontend/                 # Next.js field app + back office
├─ vectors/                  # cross-language parity fixtures (C# ↔ TypeScript)
├─ docs/                     # functional + technical documentation
└─ FieldKit.slnx
```

## Running it (dev)

> The system is orchestrated by .NET Aspire — one command brings up the API, front end,
> PostgreSQL, Keycloak, and the telemetry dashboard.

```bash
dotnet run --project FieldKit.AppHost
```

Then open the **Aspire dashboard** URL printed in the console to see every service, its logs,
and live traces. Deploying the same AppHost to Azure Container Apps is one command and its own
runbook: **[docs/engineering/deploying.md](docs/engineering/deploying.md)**.

## Status

🚧 **Phase 3 — in-store depth.** Phases 0 through 2 are complete and the system is deployed.

**What works today**, end to end:

- **Back office** — identity and permissions (Keycloak, realm-per-tenant), org and territories,
  outlets with a tenant-defined custom-field catalogue and bulk import, the product catalogue with
  assortments/MSL, price lists, promotions and tax, journey planning, and the config builders for
  perfect-store weights and survey forms.
- **The field app** — an installable PWA that pulls a rep's journey, workflows, catalogue, prices and
  promotions into IndexedDB, then works **fully offline**: geofenced check-in, config-driven visit
  steps, check-out, and an outbox that pushes idempotently on reconnect.
- **A pricing engine written twice** — the resolver for prices, promotions and tax exists in C# for
  the server and TypeScript for the device, and CI refuses a build where the two disagree on a
  shared corpus of generated vectors. The perfect-store score is held to the same standard.
- **Ten modules** with enforced boundaries, a schema each, a transactional outbox, and architecture
  tests that fail the build on a cross-module reference — all verified against real Postgres in CI.

**In flight (W11):** order capture, the offline audit and order screens, sync v2's conflict rules,
and out-of-band photo upload.

Progress is tracked week by week in the **[delivery plan](docs/delivery-plan.md)**, which records what
each slice actually shipped — including where it departed from the plan and why. The
**[roadmap](docs/roadmap.md)** is the phase-level view.

## About

Built by **Vasile-Tiberiu Socea** — Senior Full-Stack Developer (.NET & modern JS/TS).
[LinkedIn](https://linkedin.com/in/socea-tiberiu)
