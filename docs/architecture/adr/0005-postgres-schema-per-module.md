# ADR-0005: PostgreSQL with schema-per-module

- **Status:** Accepted
- **Date:** 2026-08
- **Deciders:** Tiberiu Socea
- **Related:** [ADR-0002](0002-modular-monolith.md), [ADR-0006](0006-in-process-messaging-and-outbox.md),
  [data & persistence](../14-data-and-persistence.md)

## Context

The [modular monolith](0002-modular-monolith.md) needs a persistence model that gives each
module **real data isolation** — a module must not read or write another module's tables — while
keeping the operational simplicity that made us choose a monolith in the first place. The
options span a spectrum from "one shared schema, discipline only" to "a physical database per
module."

We also have concrete pulls from the domain: multi-tenant SaaS ([A5](../../product/decisions-and-assumptions.md#a5--authentication-keycloak-oidc-via-aspire-realm-per-tenant)),
JSONB custom fields ([A1](../../product/decisions-and-assumptions.md#a1--per-tenant-customization-config-driven-moderate)),
geospatial outlet data, and a transactional outbox that must commit **in the same local
transaction** as a module's writes ([ADR-0006](0006-in-process-messaging-and-outbox.md)).

## Decision

Use **one PostgreSQL database** with **one schema per module** (`iam`, `org`, `outlets`,
`products`, `config`, `journey`, `visit`, `audit`, `order`, `sync` — ten module schemas — plus
`shared`).

- Each module's EF Core `DbContext` is **pinned to its own schema** (`HasDefaultSchema`), owns
  its own migrations, and maps only its own tables.
- **No cross-schema foreign keys.** References across modules are by **id value only**
  (e.g. `visit.OutletId` holds an outlet id but has no FK to `outlets.outlet`); integrity across
  boundaries is a domain concern, enforced through contracts/events, not the database.
- **No cross-schema queries or joins.** A module reads another module's data only through that
  module's contract ([module boundaries](../10-module-boundaries.md)).
- One database means **one local ACID transaction** spans a module's writes *and* its outbox
  insert — the property the whole reliability story depends on.

**Why PostgreSQL specifically:** first-class `JSONB` (custom fields), `citext`/rich types,
PostGIS-ready geospatial (outlet geo/geofence), excellent EF Core/Npgsql support, and it runs
trivially as an Aspire-provisioned container in dev and a managed service in prod
([A6](../../product/decisions-and-assumptions.md#a6--hosting--live-demo-azure-container-apps-via-aspire-deploy)).

## Options considered

| Option | Verdict | Why |
|---|---|---|
| Single shared schema | Rejected | No physical boundary; nothing stops cross-module coupling — the [layered-monolith failure mode](0002-modular-monolith.md) at the data tier. |
| **Schema-per-module, one DB** | **Chosen** | Real isolation (separate schemas, own migrations, no cross-FKs) **and** one local transaction + one thing to run. |
| Database-per-module (one server) | Rejected | Loses single-transaction outbox; adds connection/migration overhead for no benefit at this scale. |
| Database-per-module (separate servers) | Rejected | This is just microservices persistence — the cost [ADR-0002](0002-modular-monolith.md) explicitly declined. |

## Consequences

**Positive**
- Boundaries are visible in the database itself; a stray cross-schema query is easy to spot and
  can be **denied by role** (each module's DB role granted only its own schema — defence in depth).
- Independent migrations per module; no migration-ordering coupling between teams/modules.
- One local transaction across writes + outbox → the outbox pattern is simple and correct.
- Clean extraction path: a schema is a natural seam to lift into its own database later.

**Negative / costs**
- **No database-enforced referential integrity across modules.** Orphan-reference risk is real
  and must be handled in the domain (validate via contract on write; react to `…Deleted`/
  `…Closed` events). This is a deliberate trade — the same one microservices make.
- **No cross-module joins** for reporting → reporting composes module **query contracts** and
  events instead ([product overview → Reporting](../../product/00-product-overview.md#reporting--kpis-cross-cutting-read-side)).
- Multi-schema migration orchestration at startup needs wiring (each `DbContext` migrates its
  own schema).

**Enforcement**
- [Architecture tests](../17-testing-strategy.md) assert each `DbContext` maps only its schema.
- Per-module DB roles (prod) scope grants to a single schema.
- Tenancy is orthogonal: every tenant-owned table carries `TenantId` with a global query filter
  ([data & persistence](../14-data-and-persistence.md)), **not** a schema-per-tenant explosion.
