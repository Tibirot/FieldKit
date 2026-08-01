# ADR-0002: Adopt a modular monolith

- **Status:** Accepted
- **Date:** 2026-07
- **Deciders:** Tiberiu Socea
- **Related:** [ADR-0005](0005-postgres-schema-per-module.md),
  [ADR-0006](0006-in-process-messaging-and-outbox.md),
  [module boundaries](../10-module-boundaries.md)

## Context

FieldKit spans several reasonably independent domains — identity, organization/territory,
outlets, products & pricing, journeys, visits, audits, orders, and sync (plus **configuration/
customization**, added later — [ADR-0009](0009-config-driven-customization.md); ten modules total).
These domains have different rates of change and different owners conceptually. The instinct in 2020s .NET
architecture discussions is often to reach for **microservices**.

But the actual drivers here (see [architecture overview §1](../00-architecture-overview.md#1-architectural-drivers))
are:

- A **solo build** that must stay operationally trivial.
- A need to demonstrate **clean domain boundaries and architectural discipline**.
- A domain whose hard problem is **offline sync on the client**, not server-side scale.
- No requirement — now or realistically ever, for this project — for independent
  deployment, per-service scaling, or polyglot persistence.

Microservices would buy independent deployability and scaling at the cost of network calls,
distributed transactions, eventual-consistency everywhere, multiple pipelines, and a
distributed system to debug — all to solve problems FieldKit does not have. A **big ball of
mud** monolith would avoid that cost but throw away the boundaries that make the domain
legible and changeable.

## Decision

Build FieldKit as a **modular monolith**: a single deployable process (`FieldKit.Server`)
composed of **modules**, where each module is a bounded context with:

1. **Its own domain model** — entities, value objects, and aggregates it fully owns.
2. **Its own database schema** — physical isolation within one Postgres database
   (see [ADR-0005](0005-postgres-schema-per-module.md)); no module reads another's tables.
3. **A public contract** — a small interface surface (and event contracts) that is the
   *only* sanctioned way other modules interact with it. Everything else in the module is
   `internal`.
4. **Enforced boundaries** — [architecture tests](../17-testing-strategy.md) fail the build
   if a module takes a dependency on another module's internals.

Modules communicate two ways:
- **Synchronously**, in the same request, by calling another module's public contract.
- **Asynchronously**, across boundaries, by publishing integration events on an in-process
  bus backed by a [transactional outbox](0006-in-process-messaging-and-outbox.md) for
  reliability.

The guiding principle is **"microservices-ready, but not microservices."** Boundaries are
kept as clean as a network would force them to be — so that *if* a genuine driver ever
appeared, a module could be extracted into its own service by swapping in-process calls for
remote ones. Until then, we keep the simplicity.

## Options considered

| Option | Verdict | Why |
|---|---|---|
| **Microservices** | Rejected | Solves scaling/independent-deploy problems FieldKit doesn't have; imposes distributed-systems cost on a solo build. |
| **Layered monolith** (horizontal layers: controllers/services/repos) | Rejected | Layers cut the wrong way — a change to "pricing" touches every layer and nothing stops the domains bleeding into each other. No real boundaries. |
| **Modular monolith** (vertical modules + enforced boundaries) | **Chosen** | Gets the boundaries and domain isolation of microservices with the operability of a monolith; extractable later. |

## Consequences

**Positive**
- One process, one database, one pipeline, one thing to debug — operability suited to a solo
  build.
- In-process calls: no network latency, no partial failure, **local ACID transactions**
  across a request (the outbox handles cross-boundary reliability).
- Boundaries are explicit, enforced by tests, and documented — the architecture is legible.
- A credible extraction path preserves optionality without paying for it now.

**Negative / costs**
- Requires *discipline the compiler won't fully give you* — hence the architecture tests and
  `internal` visibility. Boundaries that aren't enforced will erode.
- A single process is a single failure/deploy unit; there is no independent scaling of one
  hot module. Accepted — not a driver here.
- **The extraction path is real but not free.** Lifting a module to its own service breaks the
  single-database, single-local-transaction outbox that [ADR-0005](0005-postgres-schema-per-module.md)
  / [ADR-0006](0006-in-process-messaging-and-outbox.md) depend on — the extracted module inherits a
  per-service outbox, a real broker, and distributed-consistency reasoning. Extraction swaps the
  *transport* **and** the *consistency model*, not just the call site. We hold the option; exercising
  it is a genuine step up in complexity.
- Shared runtime means one module can, in principle, affect another's resource use.

**Follow-ups**
- [ADR-0005](0005-postgres-schema-per-module.md): how schema-per-module is realised.
- [ADR-0006](0006-in-process-messaging-and-outbox.md): the in-process bus + outbox.
- [module boundaries](../10-module-boundaries.md): concrete project layout, visibility
  rules, and the architecture-test suite that enforces them.
