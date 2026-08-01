# Functional Spec — Offline Behavior

> **Cross-cutting** · **Phase:** 2 (v1) → 3 (v2) · **Status:** ✅ Baseline
> **Functional view.** The engineering design is the [offline sync engine deep dive](../architecture/12-offline-sync-engine.md).

## 1. Purpose

Offline isn't a feature of FieldKit — it's the environment. Reps work inside stores, in
basements and back rooms, with no signal. This spec defines, **from the user's point of view**,
what works offline, how sync feels, and how the app behaves when connectivity is unreliable. It
ties together the offline notes in every field module.

## 2. Principles

- **Offline is the default, not the fallback.** The full in-store flow (journey → visit →
  audit → order) works with zero connectivity. Being online only adds sync.
- **Never block the rep.** The app never stops a rep from doing their job because of the
  network — it records and reconciles later (e.g. geofence override, off-assortment reason).
- **The rep always knows the state.** Clear, honest indicators of what's synced, pending, or
  failed — never a silent loss of work.
- **No lost *captured* work.** *Captured/submitted* work (a checked-out visit, a submitted order or
  audit) is durable the instant it's entered and survives crash, app-update, and device swap. The
  guarantee is honestly scoped: an **unsubmitted Draft** is local-only and can be lost on a device
  swap (the app nudges the rep to submit first), and durability holds **within the OS's storage
  guarantees** — FieldKit requires **installing the PWA** and requests **persistent storage**
  (`navigator.storage.persist()`) because browsers, iOS especially, can evict storage; a large or
  aged unsynced outbox is treated as at-risk and prompts a sync ([sync engine §2](../architecture/12-offline-sync-engine.md#2-client-storage-model-indexeddb--dexie)).

## 3. What is available offline

| Area | Offline capability |
|---|---|
| Login/session | Must log in online once; session then tolerates going offline (token refresh on reconnect) — [IAM §7](10-identity-and-access.md#7-offline-behavior) |
| Journey | Today + short horizon available; work it, mark not-visited, add unplanned — [JRN §7](20-journey-planning.md#7-offline-behavior) |
| Visit | Full check-in (with geo), steps, check-out — [VIS §7](21-visit-execution.md#7-offline-behavior) |
| Audit | Full audit + survey + photos + on-device score — [AUD §7](22-merchandising-and-audits.md#7-offline-behavior) |
| Order | Full capture with on-device pricing & promotions — [ORD §7](23-order-capture.md#7-offline-behavior) |
| Master data | Read-only on device (territory-scoped snapshot) — [A4](decisions-and-assumptions.md#a4--offline-data-scope-territory-scoped) |
| Admin / back office | **Online only** (not an in-store activity) |

## 4. The sync experience (functional)

### F1 · Initial sync (device bind)
On first login / device bind ([A8](decisions-and-assumptions.md#a8--device--sync-behavior-one-active-device-auto-background-sync)),
the app pulls the rep's **territory-scoped snapshot**: outlets, journeys, products, prices,
assortments, promotions, audit templates. A progress screen shows it; after that the rep can go
offline indefinitely.

### F2 · Working offline
- Every captured item (visit, audit, order, proposal) is written to a durable local **outbox**
  and shown with a **"pending sync"** badge.
- The rep sees a persistent **connectivity + pending-count** indicator.

### F3 · Reconnect & sync
Sync runs **automatically on reconnect**, **periodically in the background**, and on-demand via
**"Sync now"** ([A8](decisions-and-assumptions.md#a8--device--sync-behavior-one-active-device-auto-background-sync)):
1. **Push** — queued work uploads idempotently (each item keyed by a client mutation id, so a
   retry never double-posts). Photos upload separately and can lag the data.
2. **Pull** — new/changed reference data downloads as a delta since the last watermark.
3. Badges flip from *pending* → *synced*; the pending count drops to zero.

### F4 · Partial failure
- If some items fail to push (e.g. a validation rejection), the rest still succeed; failures are
  shown distinctly as **"needs attention"** with the reason, and retried on the next sync.
- Photo upload failures retry independently without blocking data sync ([B5](decisions-and-assumptions.md#b5--photo--binary-sync)).

## 5. How conflicts appear to the user

By design, the rep rarely sees a conflict, because the model **engineers them out**
([B7](decisions-and-assumptions.md#b7--conflict-resolution-matrix)):

- **Reference data** is server-authoritative and read-only on device → the rep just receives the
  latest on pull; nothing to resolve.
- **The rep's own work** (visits/audits/orders) is device-owned and append-only → no competing
  writer, so it uploads cleanly.
- **Server changed master data while offline** (e.g. a price update) → the rep's already-captured
  order keeps the price it was taken at; if the server re-price differs, it's shown as a **flag**
  on the order, not a silent change or a blocking conflict ([BR-ORD-6](23-order-capture.md#5-business-rules)).

The one thing the rep may see: a **hard rejection** (e.g. ordering an outlet that was closed
server-side after their snapshot, or a SKU discontinued mid-day) surfaced as a *needs-attention*
item with a clear reason. When it's an **order**, the rejected order **re-opens editable** so the
rep fixes the flagged line and resubmits — rejected work is never a dead-end
([BR-ORD-9](23-order-capture.md#5-business-rules)). Scope/permission are judged **as-of-capture**,
so work done before a mid-day reassignment isn't wrongly rejected.

**No work is lost even if the device is swapped.** If a rep is re-bound to a new device before
reconnecting, the old device still completes a final **drain-push** of its outbox before it stops
syncing — a full offline day survives a device change ([sync engine §7](../architecture/12-offline-sync-engine.md#7-device-lifecycle)).

## 6. Requirements

| ID | Requirement | MoSCoW | Phase |
|---|---|---|---|
| OFF-01 | Core in-store flow (journey → visit) works with zero connectivity | Must | 2 |
| OFF-01b | Audit & order steps work fully offline (arrive with those modules) | Must | 3 |
| OFF-02 | Durable local store (survives app restart/crash) | Must | 2 |
| OFF-03 | Territory-scoped initial + delta sync | Must | 2 |
| OFF-04 | Outbox with idempotent push (client mutation ids) | Must | 2 |
| OFF-05 | Connectivity + pending-count indicator; per-item sync badges | Must | 2 |
| OFF-06 | Auto sync on reconnect + manual "Sync now" | Must | 2 |
| OFF-07 | Periodic background sync (where supported) | Should | 3 |
| OFF-08 | Separate, independently-retried photo upload | Must | 3 |
| OFF-09 | Partial-failure handling with "needs attention" + reasons | Should | 3 |
| OFF-10 | Installable PWA (home-screen, offline shell) | Should | 2 |
| OFF-11 | Storage-pressure handling (quota, eviction warnings) | Could | 4 |
| OFF-12 | Deactivated device may complete a final drain-push (no lost work on device swap) | Must | 3 |
| OFF-13 | Local-store (IndexedDB) schema migration preserves a pending outbox across app updates | Must | 3 |

## 7. Non-functional expectations

- **Durability:** captured work is persisted synchronously to IndexedDB before the UI confirms.
- **Sync latency:** a normal day's outbox (a handful of visits/orders + photos) syncs within
  seconds of a good reconnect; photos may trail.
- **Footprint:** a territory snapshot stays within a few MB ([B6](decisions-and-assumptions.md#b6--scale-assumptions-representative-not-limits)).
- **Resilience:** flaky connectivity (connect/drop mid-sync) never corrupts state or double-posts.

## 8. Acceptance criteria (sample)

- With the network fully off, a rep completes a whole visit (check-in → audit+photos → order →
  check-out); after reconnect every item lands exactly once and photos follow.
- Killing the app mid-visit loses nothing; reopening resumes with all captured data intact.
- Toggling the network on/off repeatedly during sync results in a consistent state with no
  duplicates.

## 9. Open questions

- Background-sync reach given iOS PWA limitations — how far to push vs. rely on
  reconnect/manual? (Assumed: reconnect + manual are the guarantees; background sync is
  best-effort.)
- Snapshot refresh cadence for long offline stretches (multi-day). (Assumed: pull-on-reconnect
  is sufficient; no forced expiry in v1.)
