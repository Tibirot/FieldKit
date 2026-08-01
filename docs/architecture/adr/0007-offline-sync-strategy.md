# ADR-0007: Offline sync strategy

- **Status:** Accepted
- **Date:** 2026-08
- **Deciders:** Tiberiu Socea
- **Related:** [offline sync engine](../12-offline-sync-engine.md),
  [offline behavior spec](../../product/30-offline-behavior.md),
  decisions [A4](../../product/decisions-and-assumptions.md#a4--offline-data-scope-territory-scoped) ·
  [A8](../../product/decisions-and-assumptions.md#a8--device--sync-behavior-one-active-device-auto-background-sync) ·
  [B7](../../product/decisions-and-assumptions.md#b7--conflict-resolution-matrix)

## Context

Field reps work inside stores with no connectivity ([offline behavior](../../product/30-offline-behavior.md)).
The client must be a **self-sufficient offline application** that captures a full day's work and
reconciles later — not a thin UI over an API. This is FieldKit's hardest problem and its biggest
showcase, so the strategy must be deliberate.

The design space for offline sync is wide and full of traps: naive last-write-wins loses data;
general conflict resolution (CRDTs, operational transforms, vector clocks) is powerful but heavy
and often overkill; "just queue the requests" ignores reference-data staleness and idempotency.

Three domain facts shape the answer:
- **Reference data** (products, prices, outlets, journeys) is authored in the back office; the
  rep only *reads* it in the field.
- **Transactional data** (visits, audits, orders) is *created* by the rep on their device and
  belongs to that device — there is no second writer.
- A rep has **one active device** ([A8](../../product/decisions-and-assumptions.md#a8--device--sync-behavior-one-active-device-auto-background-sync)).

## Decision

Adopt an **asymmetric, snapshot + outbox sync** model that **engineers conflicts out** rather
than resolving them:

### 1. Pull (server → device): versioned delta of reference data
- The device holds a **territory-scoped snapshot** ([A4](../../product/decisions-and-assumptions.md#a4--offline-data-scope-territory-scoped))
  of reference data, read-only.
- Each syncable entity carries a monotonic **row version** (per-tenant logical clock). The device
  stores a **watermark** (highest version seen per entity type) and pulls **only changes since the
  watermark**, filtered to its territory.
- Reference data is **server-authoritative**: the device always accepts the server's version.
  There is nothing to merge.

### 2. Push (device → server): idempotent outbox of device-owned mutations
- Every mutation the rep makes offline (a completed visit, an audit, a submitted order, an outlet
  correction *proposal*) is written to a local **outbox** with a **client-generated mutation id
  (GUID)**.
- On reconnect, the outbox is pushed. The server **deduplicates on the mutation id** (idempotency
  ledger in Redis + persisted) so retries never double-apply.
- Mutations are **append-only and device-owned** → no competing writer → **no conflict**.

### 3. Conflicts: designed away (see [B7 matrix](../../product/decisions-and-assumptions.md#b7--conflict-resolution-matrix))
- Reference data: server wins (client read-only).
- Transactional data: device-owned append-only; idempotent apply.
- Records that would otherwise be co-edited (orders) are **locked after submit**, and visits/
  audits are **sealed after checkout** — so the only hard case (two writers, one record) **cannot
  occur** by construction.
- Master data changed while the rep was offline is reconciled by **server-wins on next pull**;
  the rep's already-captured transactions keep the **snapshot version** they were made against,
  and any server re-price delta is **flagged, not silently applied**
  ([BR-ORD-6](../../product/23-order-capture.md#5-business-rules)).

### 4. Binaries (photos): out-of-band
Photos are downscaled on-device and uploaded **separately** via presigned URLs, retried
independently of the JSON push ([B5](../../product/decisions-and-assumptions.md#b5--photo--binary-sync)).

## Options considered

| Option | Verdict | Why |
|---|---|---|
| Online-only / thin client | Rejected | Fails the core domain requirement — reps have no signal in-store. |
| Queue raw API requests, LWW everywhere | Rejected | Silent data loss on concurrent edits; ignores reference-data staleness & idempotency. |
| CRDTs / operational transforms | Rejected | Real conflict-free merging, but heavy and unnecessary — our transactional data has a single owner, so there's no merge to perform. |
| **Snapshot pull + idempotent outbox push + conflicts-designed-out** | **Chosen** | Matches the domain's read/write asymmetry; simple, robust, debuggable; correct under flaky connectivity. |

## Consequences

**Positive**
- **No general conflict-resolution machinery** — a deliberate, defensible simplification enabled
  by domain shape (single-writer transactional data + locked records).
- Robust under adversarial connectivity: at-least-once push + idempotency ⇒ **no duplicates**,
  **no lost work**, exactly-once *effect*.
- The server-side push path reuses the **same outbox primitive** as cross-module messaging
  ([ADR-0006](0006-in-process-messaging-and-outbox.md)) — one reliability idea, reused.
- Small device footprint (territory-scoped) and cheap deltas (watermark-based).

**Negative / costs**
- The "no conflicts" guarantee **depends on the invariants** (append-only, locked-after-submit,
  one active device). If a future feature needs true co-editing of a record, this ADR must be
  revisited — the design is honest that it trades generality for the domain it has.
- **Two documented refinements to the strict invariants** (each preserves the no-conflict property):
  a **server-rejected order re-opens editable** and resubmits under a new mutation id (the only
  exception to locked-after-submit — [BR-ORD-9](../../product/23-order-capture.md#5-business-rules)),
  and a **deactivated device may drain-push** its append-only outbox (exclusivity is on pull/bind,
  not on draining) — [A8](../../product/decisions-and-assumptions.md#a8--device--sync-behavior-one-active-device-auto-background-sync),
  [sync engine §7](../12-offline-sync-engine.md#7-device-lifecycle). Neither introduces a competing
  writer, so conflicts stay engineered out.
- **Eventual consistency**: the back office sees field work only after sync.
- Row-versioning + watermarks per entity + an idempotency ledger are real mechanisms to build and
  observe.

**Follow-up:** the protocol, data structures, and failure handling are specified in the
[offline sync engine deep dive](../12-offline-sync-engine.md).
