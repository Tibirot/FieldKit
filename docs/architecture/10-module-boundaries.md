# Module Boundaries

> **Status:** ✅ Baseline · **Showcase:** modular monolith · **Last updated:** 2026-08
> **Decisions:** [ADR-0002](adr/0002-modular-monolith.md) · [ADR-0005](adr/0005-postgres-schema-per-module.md) · [ADR-0006](adr/0006-in-process-messaging-and-outbox.md)

A modular monolith is only "modular" if the boundaries are **real and enforced**. This document
turns the principle from [ADR-0002](adr/0002-modular-monolith.md) into something concrete: the
project layout, the visibility rules, the exact shape of a module and its contract, the two ways
modules communicate, and the **architecture-test suite** that fails the build when a boundary is
crossed. This is the difference between "we intend to keep modules separate" and "the compiler
and CI won't let us not."

## 1. Anatomy of a module

Every domain module has the same internal shape. Only the **Contracts** are visible to the
outside world; everything else is `internal`.

```
FieldKit.Modules.Orders/
├─ Contracts/                 ← PUBLIC. The only thing other modules may reference.
│  ├─ IOrderQuery.cs
│  ├─ Dtos/…                  ← plain data shapes crossing the boundary
│  └─ IntegrationEvents/      ← events this module publishes (OrderSubmitted, …)
│
├─ Domain/                    ← internal. Aggregates, entities, value objects, invariants.
│  ├─ Order.cs                (internal sealed)
│  ├─ OrderLine.cs
│  └─ …
├─ Application/               ← internal. Use-case handlers (commands/queries), validation.
├─ Infrastructure/            ← internal. EF Core DbContext (schema "order"), repositories.
├─ Api/                       ← internal. Minimal-API endpoint mappings for this module.
└─ OrdersModule.cs            ← the module's composition root (DI + endpoint registration)
```

**Rules that make this a boundary, not a folder:**

1. **One assembly (project) per module.** Boundaries are assembly boundaries — the strongest
   enforcement .NET gives, and what architecture tests inspect.
2. **`Contracts/` is the public API.** Types outside `Contracts/` are `internal` — physically
   unreferenceable from other modules.
3. **Contracts expose behavior and DTOs, never domain entities.** An aggregate (`Order`) never
   leaves its module; a `OrderSummaryDto` does. Prevents another module binding to your internals.
4. **Own schema, own `DbContext`** (`order` schema, [ADR-0005](adr/0005-postgres-schema-per-module.md)).
5. **Self-registration.** `OrdersModule` wires its own DI and endpoints; the host just calls
   `AddOrdersModule()` / `MapOrdersModule()` — the host knows modules exist, not how they work.

## 2. The dependency rule

```mermaid
flowchart TB
  subgraph host["Host (composition root only)"]
    H["FieldKit.Server"]
  end
  subgraph mods["Modules — depend ONLY on other modules' Contracts"]
    O["Orders"]
    P["Products.Contracts"]
    V["Visit.Contracts"]
  end
  subgraph bb["Shared, non-module layers"]
    SK["SharedKernel (value objects)"]
    BB["BuildingBlocks (abstractions)"]
    INF["Infrastructure (EF base · interceptors · bus · outbox)"]
  end

  H --> O
  O -->|references| P
  O -->|references| V
  O --> SK
  O --> BB
  O -->|impl only| INF
  P --> SK
  P --> BB
  V --> SK
```

- A module may reference **another module's `Contracts` project only** — never its
  implementation assembly.
- **Three shared layers, split by purity so `Contracts` stay lightweight:**
  **`SharedKernel`** (dependency-free value objects: `Money`, `GeoPoint`, strongly-typed ids,
  `Result`, `IClock`); **`BuildingBlocks`** (pure *abstractions*: messaging contracts,
  `ITenantContext`, `ITenantOwned`/`IAuditable`, the `IReferenceChangeFeed`/`I…Ingest` shapes); and
  **`Infrastructure`** (EF Core base `ModuleDbContext`, stamping interceptors, and — as they land —
  the bus/outbox implementations). A module's **`Contracts`** reference only `SharedKernel` +
  `BuildingBlocks`; its **implementation** may also reference `Infrastructure`.
- **Building blocks never reference a module.** Dependencies point inward/toward shared, never
  from infrastructure to domain.
- The **host references every module** (to compose them) and nothing references the host.

To keep even the `Contracts` reference honest, contracts are split into a separate small project
(`Orders.Contracts`) so a consumer takes a dependency on the **interface, not the code behind
it** — the seam that lets a module later become a remote service.

## 3. How modules communicate

Per [ADR-0006](adr/0006-in-process-messaging-and-outbox.md), **"call for answers, publish for
facts."**

### 3a. Synchronous — a contract call
Order needs a price *now*:

```csharp
// in Orders (Application) — depends on Products.Contracts only
var price = await _pricing.ResolvePriceAsync(outletId, productId, qty, date, ct);
```

`IPricingService` is implemented inside Products; DI binds it; the call is in-process and joins
the current transaction. Order never sees a Products entity — only the DTO the contract returns.

### 3b. Asynchronous — an integration event
Outlets closes an outlet; Journey must react, but Outlets must not know Journey exists:

```mermaid
sequenceDiagram
  participant OUT as Outlets
  participant OB as Outbox (outlets schema)
  participant DISP as Outbox dispatcher
  participant JRN as Journey (handler)
  OUT->>OB: write OutletClosed + state change (one TX)
  Note over OUT,OB: atomic commit — no dual-write
  DISP->>OB: poll unpublished
  DISP->>JRN: dispatch OutletClosed (own TX, idempotent)
  JRN-->>DISP: handled → mark processed
```

The event type lives in `Outlets.Contracts/IntegrationEvents`; Journey references it and
registers a handler. Delivery is **at-least-once**; the handler is idempotent.

## 4. What crosses a boundary (and what never does)

| Crosses the boundary | Never crosses |
|---|---|
| Contract **interfaces** (`IOrderQuery`) | Domain **entities/aggregates** (`Order`, `OrderLine`) |
| **DTOs** in `Contracts/Dtos` | EF Core `DbContext` / repositories |
| **Integration events** in `Contracts/IntegrationEvents` | Another module's tables (no cross-schema SQL) |
| Strongly-typed **ids** & `SharedKernel` value objects | Internal services, handlers, validators |

## 5. Enforcement — architecture tests

Boundaries that rely on goodwill rot. FieldKit encodes them as **executable tests**
(NetArchTest / ArchUnitNET) that run in CI and fail the build on violation. Representative rules:

| # | Rule | Intent |
|---|---|---|
| AT-1 | No module implementation assembly may reference another module's **implementation** assembly (only its `.Contracts`). | The core boundary. |
| AT-2 | Types outside a module's `Contracts` namespace are `internal`. | Contracts are the only public surface. |
| AT-3 | No type in `Contracts` exposes a `Domain` type (return/param). | Entities never leak. |
| AT-4 | `BuildingBlocks`/`SharedKernel` reference **no** module. | Dependencies point inward. |
| AT-5 | Each module's `DbContext` maps only its own schema. | Data isolation ([ADR-0005](adr/0005-postgres-schema-per-module.md)). |
| AT-6 | Integration-event handlers are idempotent-by-construction (registered via the bus, not called directly). | At-least-once safety. |
| AT-7 | No `DateTime.Now`/`DateTimeOffset.Now`; only an injected `IClock` (UTC). | Testability + [i18n/timezone](adr/0010-internationalization.md). |
| AT-8 | Domain layer references no EF Core / ASP.NET types. | Keep the domain pure. |
| AT-9 | No `IgnoreQueryFilters` or `ExecuteSqlRaw` in production code. | The tenant filter is the isolation guarantee ([ADR-0008](adr/0008-authentication-and-multitenancy.md), BR-IAM-1). |
| AT-10 | The graph of **contract implementations depending on other modules' contracts** is acyclic. | Two modules may reference each other's contracts; their *implementations* may not call in a circle. |

**Two enforcement mechanisms, not one.** AT-1…AT-6, AT-8 and AT-10 are *tests* — they inspect assemblies,
so they need the assemblies to exist. **AT-7 and AT-9 are compile-time**, via the banned-API
analyzer: banning a symbol outright is stronger than asserting nobody used it, because the failure
lands on the developer who typed it rather than on CI minutes later.

Both are wired in [`Directory.Build.props`](../../Directory.Build.props), so a new module inherits
them at creation instead of when someone remembers to copy a `csproj` fragment. `RS0030` is escalated
to an **error**; left as a warning these would be suggestions, and one of them is the tenant-isolation
bypass.

**Test projects are exempt from AT-9**, deliberately: proving the tenant filter works means being
able to look past it. A test that could only query *through* the filter would be asserting the filter
against itself — so `PersistenceIntegrationTests` uses `IgnoreQueryFilters` to show the hidden row is
physically present and correctly stamped.

```csharp
// Example (NetArchTest): AT-1
var result = Types.InAssembly(typeof(OrdersModule).Assembly)
    .That().ResideInNamespace("FieldKit.Modules.Orders")
    .ShouldNot().HaveDependencyOnAny(
        "FieldKit.Modules.Products.Domain",
        "FieldKit.Modules.Products.Infrastructure")   // …every other module's internals
    .GetResult();
Assert.True(result.IsSuccessful, string.Join("\n", result.FailingTypeNames));
```

These tests are part of the [testing strategy](17-testing-strategy.md) and gate every PR.

## 6. The extraction path (why this buys optionality)

If a genuine driver ever demanded splitting a module into its own service, the boundary is
already the seam:

1. Consumers already depend on `X.Contracts` (an interface), not on X's code.
2. The interface implementation swaps from an in-process call to a remote client (HTTP/gRPC).
3. Async events already flow through an outbox — repoint it at a real broker
   ([ADR-0006](adr/0006-in-process-messaging-and-outbox.md)).
4. The module already owns its schema — lift it to its own database
   ([ADR-0005](adr/0005-postgres-schema-per-module.md)).

Nothing in the domain changes. This is the payoff of "microservices-ready, not microservices":
we hold the option without paying to exercise it.

## 7. Module registry

**Built contracts are in bold; the rest are planned** — the shape the design intends, not something
another module can reference today. The distinction is not cosmetic: this table read as a
description of what exists until the pre-Phase-2 audit checked it against the assemblies and found
four of its entries had no interface behind them. A registry that overstates its surface is exactly
the document a module author trusts when deciding what to depend on.

| Module | Assembly | Schema | Key contracts | Publishes |
|---|---|---|---|---|
| IAM | `…Modules.Iam` (+ `.Contracts`) | `iam` | **`IUserDirectory`**, **`ITenantRegistry`** | `UserDeactivated` |
| Organization | `…Modules.Org` (+ `.Contracts`) | `org` | **`ITerritoryDirectory`**, `IRepScope`, `IOrgHierarchy` | `RepAssignmentChanged` |
| Outlets | `…Modules.Outlets` (+ `.Contracts`) | `outlets` | **`IOutletCatalog`**, `IOutletClassification`, `IReferenceChangeFeed`, `IOutletProposalIngest` | `OutletChanged`, `OutletClosed` |
| Products & Pricing | `…Modules.Products` | `products` | `IProductCatalog`, `IAssortmentService`, `IPricingService`, `IReferenceChangeFeed` | `PriceListPublished`, `PromotionActivated` |
| Configuration | `…Modules.Configuration` (+ `.Contracts`) | `config` | **`IFieldDefinitionCatalog`**, `IVisitWorkflow`, `ISurveyForms`, `IScoreWeights`, `IReferenceChangeFeed` | `ConfigurationPublished` |
| Journey | `…Modules.Journey` | `journey` | `IJourneyQuery`, `IReferenceChangeFeed`, `IJourneyIngest` | `JourneyPublished`, `PlannedVisitMarkedNotVisited` |
| Visit | `…Modules.Visit` | `visit` | `IVisitContext`, `IVisitQuery`, `IVisitIngest` | `VisitCompleted` |
| Audit | `…Modules.Audit` | `audit` | `IAuditQuery`, `IPerfectStoreScore`, `IAuditIngest` | `AuditCompleted` |
| Order | `…Modules.Orders` | `order` | `IOrderQuery`, `IOrderIngest` | `OrderSubmitted` |
| Sync | `…Modules.Sync` | `sync` | `ISyncEndpoints` (pull/push) | `DeviceRegistered` |

**Ten modules.** Contracts and events map 1:1 to the [functional specs'](../product/00-product-overview.md)
module contract sections — the functional "what" and the technical "how" stay in lockstep.

**Each module is two assemblies:** `FieldKit.Modules.X` (implementation, private) and
`FieldKit.Modules.X.Contracts` (its only public surface). IAM is the first built this way and sets
the pattern; **Products is still a single assembly**, for the same reason Organization was until W5
— it has no consumer yet, and `IProductCatalog` designed before Journey, Visit or Order asks for
anything is a guess three modules would have to live with.

> **`IOutletClassification` grew a second dimension in W6, and the record shape is why that was
> free.** Tax (`PRD-07`) keys a rate by `(tax class, country)`, and the country lives on the outlet's
> address where `AT-1` forbids Products reading it. The contract's own doc had anticipated this — "a
> record rather than a bare `Guid` return, so the shape survives a second classification dimension" —
> so `CountryCode` was an added property rather than a third Outlets contract or a fourth method.
> Country qualifies on the same test channel did: something another module *decides with*, not a
> detail of the outlet.
>
> **It did not grow its `.Contracts` in W6, as this line used to promise.** W6 built the things those
> contracts would wrap — assortments (`PRD-02`), price lists (`PRD-03`), price resolution
> (`PRD-04`) — and each time, the consumer that would shape the contract turned out to be Order or
> Sync, in Phase 3. `IAssortmentService`, `IPricingService` and `IProductCatalog` are therefore still
> unbuilt, and the resolver is a pure static function its own endpoint calls directly. That is this
> section's own rule applied to itself rather than an omission: the week that was supposed to add
> them is exactly the week that showed nobody was asking. The contracts land with their first real
> caller, as `IOutletCatalog` and `ITerritoryDirectory` did.

**Products was called `Catalog` until it wasn't.** It was W1's proof that the modular monolith runs
— the module that replaced the Aspire template's `WeatherForecast` — and it was never in this
registry. That was survivable while it stayed scaffolding, but it was scaffolding *standing on W6's
ground*: it already owned `/api/products`, already declared `product:read` and `product:write`, and
the permission catalogue is built from `IModule.Permissions`, so a second module declaring those
names is not a merge conflict but a startup failure. W6 would have opened with that collision and
resolved it while someone was mid-pricing-engine, which is how the wrong answer gets picked. It was
renamed instead, on its own, before the week began: same route, same permission strings, `catalog`
schema → `products`. What is behind it is still a stub — an SKU and a name — and the shape a product
actually has arrives with `PRD-01`.

The permission *strings* did not change, and that is the load-bearing part. `product:read` and
`product:write` are named for the resource, not for whichever module introduced them; they are
already written into `SystemRoleTemplates`, both dev realms, and any role a tenant has composed
since. Renaming the C# constants cost nothing. Renaming the permissions would have been a migration
across all three. **The schema move leaves the old `catalog` schema behind** in any database that
predates it — the migration creates `products` and deliberately does not drop anything, because a
migration that drops a schema is a data-loss statement running unattended at startup. Aspire's
Postgres keeps a data volume, so clear it once, by hand: `DROP SCHEMA catalog CASCADE;`
**Organization's `.Contracts` assembly arrived with its first real contract**, `ITerritoryDirectory`
(W5), exactly as planned — it stayed single-assembly until then because an interface designed before
its consumer is a guess other modules have to live with. `IRepScope` and `IOrgHierarchy` are still
in that state and still unbuilt: the capability behind them exists and is reachable over HTTP
(`GET /api/org/users/{id}/scope`, backed by an internal `OrgHierarchy` helper), but the in-process
contract Journey and Visit are specified to consume has no consumer yet. It lands with them in
Phase 2, shaped by what they actually ask for.

**Outlets grew its `.Contracts` assembly when territories needed it** — `IOutletCatalog`, designed
against Organization as an actual caller rather than guessed at when the module was created. That is
the sequencing this note describes working as intended: the interface exposes exactly what a
territory needs to validate and label an outlet, and nothing about address, coordinates, contacts or
channel, because no consumer asked for those and a consumer that could read them would soon be making
decisions with a stale copy.
**Configuration is the first module born with its `.Contracts` assembly**, because it is the first
whose *reason to exist* is being called by other modules: a catalogue nobody reads configures nothing.
`IFieldDefinitionCatalog` ships with Outlets as its caller, and its shape follows from that — it hands
back **descriptors, not a validator**. Configuration owns what a tenant may record; deciding whether a
given write satisfies it is the owning module's job, on the owning module's request path, where the
rest of that entity's invariants already run. The alternative — a `ValidateAsync` on the contract —
would have put Outlets' 400s inside Configuration and made every future consumer's error shape
Configuration's problem.

The split is what lets AT-1 be a real reference check rather than a naming convention, and it makes
AT-3 structural: a contracts assembly that cannot see the implementation cannot name a domain type
in a signature.

### Two modules may point at each other

**Organization and Outlets reference each other's contracts**, and that is allowed rather than
tolerated. Organization asks `IOutletCatalog` whether a shop exists; Outlets asks
`ITerritoryDirectory` which territory covers it, because `BR-OUT-1` says an outlet *has* one.

It cannot cycle at build time. **Every `.Contracts` assembly is a leaf** — they reference only
`SharedKernel` and `BuildingBlocks` — so the assembly graph stays acyclic however many modules point
at each other, and AT-1 still forbids the coupling that would actually break a build. Insisting on a
one-way arrow would have invented a hierarchy the domain does not have: Outlets owns *what a shop is*,
Organization owns *who covers it*, and neither sits above the other.

The alternatives are the ones that cause damage. A client-side join puts a domain relationship in the
browser for every future consumer — an export, a report, the sync feed — to re-implement. Copying the
territory onto the outlet buys a second source of truth plus an integration event to keep it aligned,
which is a real tangle rather than a notional one.

**What can still go wrong is a cycle at runtime**, and AT-1 cannot see it: if the class behind
`ITerritoryDirectory` took a dependency on `IOutletCatalog` while the class behind `IOutletCatalog`
took one on `ITerritoryDirectory`, a single call would re-enter through the other module — mutual
recursion wearing two sets of perfectly legal references. **AT-10** is the gate: it builds the graph
of contract *implementations* depending on other modules' contracts and asserts it is acyclic.

The rule is deliberately about implementations only. An endpoint may depend on any module's contract,
because nothing calls back into an endpoint; a contract implementation is the re-enterable surface,
so it is the one constrained.

> Two registry entries are not where an earlier draft placed them, and the difference is deliberate.
> **`ITenantContext` lives in `BuildingBlocks`**, not `Iam.Contracts`: every module needs it on every
> request, and routing that through a module contract would make the most cross-cutting primitive in
> the system depend on one module's assembly. **`IAuthorizationService`** is not a FieldKit interface
> at all — permission checks are `ITenantContext.Has` plus the `RequirePermission` endpoint
> convention, both fed from the token, so introducing a service that could answer differently from
> the request's own token would be a hazard rather than a feature.

**Two sync-specific contract families** keep [ADR-0002](adr/0002-modular-monolith.md)'s "no cross-
schema access" honest (resolves finding S3):
- **`IReferenceChangeFeed`** — implemented by every reference module (Outlets, Products,
  Configuration, Journey). Returns a **territory-scoped, `rowVersion > cursor` delta with
  tombstones and a new high-water mark**. Sync composes these; it never reads another schema. The
  row-version **stamping interceptor** lives in `Infrastructure`; the version *columns* live in
  each module's own schema ([data & persistence](14-data-and-persistence.md#4-keys-concurrency-timestamps)).
- **`I…Ingest`** — the **push** path, one per module that receives device-created mutations:
  `IVisitIngest`, `IAuditIngest`, `IOrderIngest`, **`IJourneyIngest`** (not-visited / unplanned /
  reschedule annotations, [Journey §7](../product/20-journey-planning.md#7-offline-behavior)), and
  **`IOutletProposalIngest`** (field-originated outlet corrections/new-outlet requests →
  review queue, [Outlets §7](../product/12-outlets-master-data.md#7-offline-behavior)). Sync applies
  each pushed mutation *through* the owning module so its domain invariants run server-side
  (assortment, outlet-open, permissions, price re-check) — "apply through contracts, not tables"
  ([sync engine §4](12-offline-sync-engine.md#4-push-protocol-device-owned-mutations)).

> **Where these interfaces live.** `IReferenceChangeFeed` and the `I…Ingest` *shape* are declared
> once in `BuildingBlocks` (each is generic over the module's DTO); modules depend on `BuildingBlocks`
> and implement them, and `BuildingBlocks` references no module — so this stays within the dependency
> rule (§2). Sync composes the implementations by DI, keying deltas per entity type.
