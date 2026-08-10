# ADR-0013: The row version is a transactional per-tenant counter, not a sequence

- **Status:** Accepted
- **Date:** 2026-08
- **Deciders:** Tiberiu Socea
- **Related:** [ADR-0007](0007-offline-sync-strategy.md), [ADR-0005](0005-postgres-schema-per-module.md),
  [ADR-0006](0006-in-process-messaging-and-outbox.md),
  [sync engine §4](../12-offline-sync-engine.md)

## Context

[ADR-0007](0007-offline-sync-strategy.md) makes delta sync the whole of the pull path, and the
[sync engine](../12-offline-sync-engine.md#4-wire-protocol) names the primitive it rests on:

> **Row version** = a per-tenant monotonic sequence (Postgres `bigint` from a sequence, or a logical
> clock column) stamped on every change. It is the single ordering primitive.

A device pulls with `rowVersion > cursor`, applies what comes back, and stores the highest version it
saw. Everything about correctness reduces to one property:

> **If a device has seen version N, no change with a version ≤ N may become visible afterwards.**

Break it and a row is skipped **permanently** — not until the next pull, but until someone notices an
outlet that has been missing from a rep's device for a month. There is no reconciliation pass in the
design that would catch it, by construction: the whole point of a watermark is not to re-read what is
already known.

W8 slice 0 builds this, so the parenthetical above has to be resolved.

## Decision

A **per-tenant, per-module counter row**, incremented **inside the same `SaveChanges`** as the change
it stamps, guarded by an optimistic-concurrency token.

- `ISyncTracked` marks an entity as carrying `RowVersion` (`bigint`).
- Each module schema owns a `change_sequence` table keyed by tenant — the same shape as the outbox
  table, which every module schema already carries for the same reason ([ADR-0006](0006-in-process-messaging-and-outbox.md)).
- A save-changes interceptor takes **one** version per transaction and stamps every dirty
  `ISyncTracked` entity in it with that value. Fifty visits pushed in one batch share one version;
  the counter counts *change sets*, not rows.
- The counter's value is a concurrency token, so two transactions racing on the same tenant produce a
  `DbUpdateConcurrencyException` for the loser rather than two rows claiming the same version.

**Scope of the ordering is per tenant per module**, not global. That is enough because the client
holds a watermark **per entity type** ([sync engine §4](../12-offline-sync-engine.md#4-wire-protocol)),
and a module's ordering is a superset of any of its entity types'. A global counter would need a
table outside every module's schema, which [ADR-0005](0005-postgres-schema-per-module.md) does not
allow and no requirement asks for.

## Options considered

| Option | Verdict | Why |
|---|---|---|
| **Transactional counter row** | **Chosen** | Allocation *is* a row lock held to commit, so version order = commit order, and a rollback returns the number rather than burning it. |
| Postgres `nextval()` sequence | Rejected | **Allocates before commit and never rolls back.** T1 takes 5, T2 takes 6 and commits first; a device pulling now sees 6, stores it, and never sees 5. Silent, permanent, and invisible in any test that does not run two concurrent writers. Fixing it on the read side means computing a "safe" watermark from in-flight transactions — real work, in every pull query, forever. |
| `xmin` / system columns | Rejected | Not monotonic across wraparound, not stable across `VACUUM FULL`, and not something to build a client protocol on. |
| Commit timestamps (`track_commit_timestamp`) | Rejected | Correct in principle and a server-level setting this deployment does not control on a managed flexible server. Ties the protocol to a database configuration flag. |
| A logical clock in application code | Rejected | Every instance would need to agree; that is a distributed counter with extra steps, and `minReplicas` is not always 1. |

## Consequences

**Positive**

- The dangerous property holds by construction: the counter row is locked from allocation to commit,
  so nothing can commit a lower version afterwards. It is not a test that catches this, it is the
  lock.
- **Gapless.** A rolled-back transaction returns its number. Gaplessness is not required by the
  protocol, but it makes the feed readable by a human, which matters when debugging a device that
  claims to be at 8830.
- One version per transaction rather than per row keeps a 50-row push to a single increment, and
  gives the version a meaning a reader can state: *the Nth committed change set for this tenant*.
- No new infrastructure. No sequence to create, no shared schema, no cross-module table — the
  mechanism is a table each module already knows how to own.

**Negative / costs**

- **Writes to one tenant serialize at the counter.** Two concurrent saves touching `ISyncTracked`
  entities of the same tenant *in the same module* contend, and the loser gets a concurrency
  exception to retry. This is the real cost, and it is accepted because the write profile is a back
  office plus periodic device pushes — not a high-throughput ingest. If that stops being true, the
  fix is a retry policy at the endpoint before it is a change to this design.
- **An extra round trip per save** that touches tracked entities: the counter is read before it is
  written. Unmeasurable next to the save it accompanies.
- **Every module that syncs needs the table**, added by its own migration. Slice 0 gives it to
  Outlets, the entity the first `/sync/pull` serves; the rest arrive with W8 slice 8.

**Deliberately not solved here**

- **Deletes.** A row version orders changes to rows that exist; a device learns about a deletion from
  a tombstone, which is W8 slice 1.
- **Scope entry.** An entity moving *into* a device's scope may carry an old version and would never
  appear in a `rowVersion > cursor` delta. That hole is closed by the scope diff described in
  [sync engine §4](../12-offline-sync-engine.md#4-wire-protocol), not by this counter — and it is
  why the counter alone is not the whole pull.
