# ADR-0006: In-process messaging & transactional outbox

- **Status:** Accepted
- **Date:** 2026-08
- **Deciders:** Tiberiu Socea
- **Related:** [ADR-0002](0002-modular-monolith.md), [ADR-0005](0005-postgres-schema-per-module.md),
  [module boundaries](../10-module-boundaries.md)

## Context

Modules must talk **across boundaries** without coupling to each other's internals
([ADR-0002](0002-modular-monolith.md)). Two interaction shapes exist:

1. **Synchronous, in the same request** — "I need an answer now" (e.g. Order asks Products to
   resolve a price). This is a direct call on the target module's **public contract**; no
   messaging needed.
2. **Asynchronous, reacting to something that happened** — "when an outlet is closed, Journey
   should drop it from future plans." Here the publisher must not know or wait for subscribers.

For (2) we need reliable event delivery. The classic trap is the **dual-write problem**: commit
a domain change to the DB *and* publish an event as two separate operations — if the process
dies between them, they diverge. Since everything runs in one process on one database
([ADR-0005](0005-postgres-schema-per-module.md)), we can solve this cleanly without a broker.

## Decision

**Two mechanisms, one rule** ("call for answers, publish for facts"):

### 1. Synchronous — public contracts
Cross-module reads/commands in a request go through the target module's **contract interface**
(e.g. `IPricingService`, `IOutletCatalog`), resolved by DI. In-process = no network, no partial
failure, and it participates in the **same transaction**.

### 2. Asynchronous — domain events + transactional outbox
- A module raises **integration events** (e.g. `OutletClosed`, `PriceListPublished`,
  `OrderSubmitted`) as part of its work.
- Events are **not** dispatched inline. They are written to an **outbox table in the same schema
  and the same local transaction** as the domain change. Commit is atomic: either both the change
  and the event persist, or neither does. **Dual-write eliminated.**
- A background **outbox dispatcher** polls unpublished rows and invokes in-process handlers,
  marking each row processed. Handlers are **idempotent** and dispatch is **at-least-once**.
- Handlers run in their **own** transaction (a subscriber failure never rolls back the
  publisher's committed work).
- **Under multiple server replicas** each runs a dispatcher, so polling claims rows with
  **`SELECT … FOR UPDATE SKIP LOCKED`** (or a single-leader election) — a row is processed by
  exactly one replica. At-least-once + idempotent handlers keep this correct; `SKIP LOCKED` keeps it
  from doing double work. The same claim mechanism guards the sync idempotency-ledger and device
  registry ([architecture overview §7](../00-architecture-overview.md#7-deployment-topology)).

### Tooling
An in-process mediator/message-bus abstraction (a lightweight hand-rolled dispatcher, or a
library such as **Wolverine** which provides mediator + outbox natively).
> ASSUMPTION (📝): default to a thin in-house dispatcher + outbox to keep the mechanics explicit
> and dependency-light (and because MediatR moved to a commercial license). Wolverine is a
> strong alternative that would also give the outbox for free — revisit if hand-rolling the
> dispatcher grows costs.

## Options considered

| Option | Verdict | Why |
|---|---|---|
| Direct calls only (no events) | Rejected | Forces synchronous coupling for reactions; publisher must know every subscriber. |
| In-memory events, dispatched inline | Rejected | Reintroduces the dual-write problem; a crash mid-handler loses events or ties subscriber failures to the publisher's transaction. |
| **Contracts (sync) + outbox events (async)** | **Chosen** | Right tool per shape; atomic publish; reliable at-least-once delivery; no broker. |
| External broker (Kafka/RabbitMQ/SB) | Rejected (for now) | Operational + latency cost of a distributed system we don't need in one process. The outbox is broker-ready if we extract a module later. |

## Consequences

**Positive**
- **No dual-write divergence** — events and state commit atomically.
- Publisher/subscriber fully decoupled; a module can be added/removed as an event consumer
  without touching publishers.
- Reliable **at-least-once** delivery with idempotent handlers survives crashes and restarts.
- **Broker-ready seam:** the same outbox rows can later be shipped to a real broker if a module
  is extracted into its own service — the extraction path [ADR-0002](0002-modular-monolith.md)
  promised.

**Negative / costs**
- **Eventual consistency across boundaries** — subscribers see a change slightly after commit.
  Acceptable: cross-module reactions (drop a closed outlet from plans) are not
  read-your-writes-critical.
- Handlers **must be idempotent** (at-least-once can redeliver). Enforced by design (natural keys
  / processed-ledger).
- An outbox dispatcher is a moving part to run and observe (it emits its own
  [metrics](../15-observability.md): backlog depth, dispatch latency).

**Enforcement / observability**
- Outbox backlog and dispatch latency are first-class [metrics](../15-observability.md); a
  growing backlog is an alertable condition.
- The **same outbox pattern** carries the offline sync push server-side
  ([sync engine](../12-offline-sync-engine.md)) — one reliability primitive, reused.
