# Data & Persistence

> **Status:** ✅ Baseline · **Last updated:** 2026-08
> **Decisions:** [ADR-0005](adr/0005-postgres-schema-per-module.md) · [ADR-0008](adr/0008-authentication-and-multitenancy.md) · [ADR-0009](adr/0009-config-driven-customization.md)

How FieldKit stores data: schema-per-module physical layout, multi-tenancy enforcement, the custom-
fields mechanism, auditing, migrations, and the outbox tables.

## 1. Physical layout

One PostgreSQL database; **one schema per module** ([ADR-0005](adr/0005-postgres-schema-per-module.md)).

```
fieldkit (database)
├─ iam.*        ├─ journey.*
├─ org.*        ├─ visit.*
├─ outlets.*    ├─ audit.*
├─ products.*   ├─ order.*
├─ config.*     ├─ sync.*
└─ shared.*     (reference lookups; BuildingBlocks primitives)
```

Ten module schemas (`iam`, `org`, `outlets`, `products`, `config`, `journey`, `visit`, `audit`,
`order`, `sync`) plus `shared`. The **Configuration** module owns `config.*` (field-definition
catalog, visit-workflow / survey / weight definitions) and **retains historical versions** of each
definition/set so as-of-capture validation & scoring can reference the version a mutation was
captured under ([BR-CFG-1](../product/14-configuration.md#5-business-rules)) — [ADR-0009](adr/0009-config-driven-customization.md).
`shared.*` holds shared primitives + cross-tenant reference lookups; it has an explicit owner — a
**shared migrator** in `Infrastructure`, not a domain module — so "each module owns its migrations"
still holds and no module reaches into another's schema. The EF base (`ModuleDbContext`,
schema-per-module, the tenant query filter and stamping interceptors) lives in `Infrastructure`;
the marker interfaces (`ITenantOwned`, `IAuditable`) are pure abstractions in `BuildingBlocks`.

- Each module's `DbContext` sets `HasDefaultSchema("<module>")`, maps only its tables, owns its
  migrations. **No cross-schema FKs; no cross-schema queries** — references are by id value.
- **Prod defence in depth:** a per-module DB role granted only its own schema, so a stray
  cross-schema query fails at the database, not just in review.

## 2. Multi-tenancy

Row-level tenancy in shared schemas (**not** schema-per-tenant) — [ADR-0008](adr/0008-authentication-and-multitenancy.md).

- Every tenant-owned table has a **`TenantId`** column, indexed and **first in composite indexes**
  (most queries filter by tenant).
- EF Core **global query filter** appends `TenantId = current` automatically; a **save
  interceptor** stamps `TenantId` on insert. Developers never write the tenant predicate.
- `IgnoreQueryFilters()` and raw SQL that bypass the filter are **banned by
  [architecture test](17-testing-strategy.md)**.

## 3. Custom fields (JSONB)

Per [ADR-0009](adr/0009-config-driven-customization.md):

- Customizable entities carry a `custom_fields jsonb` column.
- A **field-definition catalog** owned by the **Configuration** module in `config`
  (`field_definition`: tenant, entity, key, type, required, validation, options) governs what's
  valid; the owning module **validates values against `IFieldDefinitionCatalog` on write** (JSONB is
  schemaless on its own). Definitions are **snapshot-versioned reference config** that syncs to the
  device like any other reference data — so a value captured offline against an old definition and
  the server's validation reconcile the same way pricing does ([ADR-0009](adr/0009-config-driven-customization.md)).
- **Indexing:** a GIN index on `custom_fields` for containment queries; **expression indexes** on
  specific hot paths (e.g. `((custom_fields->>'loyaltyTier'))`) where a tenant reports on a field.

## 4. Keys, concurrency, timestamps

| Concern | Approach |
|---|---|
| Primary keys | `Guid` (v7/sequential) — client-generatable offline, index-friendly |
| Concurrency | `xmin`/rowversion optimistic concurrency on mutable back-office aggregates |
| Change tracking (sync) | Per-tenant monotonic **`row_version bigint`** stamped on syncable reference rows ([sync engine](12-offline-sync-engine.md)) |
| Timestamps | `timestamptz`, **UTC**, written via `IClock` ([ADR-0010](adr/0010-internationalization.md)) |
| Money | `numeric` amount + `char(3)` currency (a `Money` VO) — never float |
| Geo | `geography(Point)` (PostGIS) for outlet location / geofence |
| Soft delete / tombstones | Reference data uses tombstones for sync; transactional data is append-only |

## 5. Auditing

- A save interceptor stamps **created/modified at + actor** on auditable entities (actor from
  `ITenantContext`).
- Meaningful changes raise **domain events**; a subset become integration events via the
  [outbox](adr/0006-in-process-messaging-and-outbox.md).
- This satisfies the "who changed what" needs behind the [security](16-security.md) and GDPR
  posture ([B8](../product/decisions-and-assumptions.md#b8--privacy--gdpr-posture)).

## 6. Migrations

- **Per-module migrations** (each `DbContext` independently). No cross-module ordering coupling.
- Each module keeps its **`__EFMigrationsHistory` in its own schema** (`MigrationsHistoryTable(…, schema)`),
  so contexts sharing the one database never collide — a `ModuleMigrator<TContext>` applies each
  module's migrations on startup (`MigrateAsync`). A design-time `IDesignTimeDbContextFactory` lets
  `dotnet ef` build the model without booting the host.
- Applied at startup in dev (Aspire) and via a migration step in the deploy pipeline
  ([ADR-0011](adr/0011-deployment-azure-container-apps.md)); each schema migrates independently.
- **Seed/demo data** (a believable tenant: a brand, outlets, products, journeys) is a dedicated
  seeding step so the app is demoable out of the box ([B6](../product/decisions-and-assumptions.md#b6--scale-assumptions-representative-not-limits)).

## 7. Outbox & idempotency tables

| Table | Schema | Purpose |
|---|---|---|
| `outbox_message` | per module | Integration events written in the same TX as domain change ([ADR-0006](adr/0006-in-process-messaging-and-outbox.md)) |
| `inbox_processed` | per module | Idempotency ledger for event handlers (at-least-once safety) |
| `sync.idempotency` | `sync` | Client `mutationId` → recorded result for push dedup ([sync engine](12-offline-sync-engine.md)) |
| `sync.device` | `sync` | Device registry + per-entity watermarks |

## 8. Testing persistence

Integration tests run against **real PostgreSQL via Testcontainers** (not in-memory) so query
filters, JSONB, migrations, and PostGIS behave as in prod ([testing strategy](17-testing-strategy.md)).
