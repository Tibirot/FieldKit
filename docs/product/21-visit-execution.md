# Functional Spec — Visit Execution

> **Module:** Visit · **Group:** Field · **Phase:** 2 · **Status:** ✅ Baseline
> **Depends on:** Journey · **Consumed by:** Audit, Order

## 1. Purpose

Visit Execution is the **spine of the in-store experience**. It models the lifecycle of a
single store call — check in, work through a **configurable set of steps**, check out — and it
is the container that Audit and Order attach to. Everything a rep does in a store belongs to a
Visit. It is designed **offline-first**: the entire flow works with zero connectivity.

## 2. Actors

| Actor | Interest |
|---|---|
| Field Rep | Executes the visit end-to-end, in-store, offline |
| Supervisor | Reviews visit outcomes, durations, coverage, compliance |
| Sales Ops / Admin | Configures the per-channel visit workflow (steps) |

## 3. Core concepts

- **Visit** — one in-store engagement: outlet, rep, planned/unplanned, timestamps, geo-stamp,
  status, outcome. Owns its child work (audits, orders, notes, photos).
- **Visit workflow** — the ordered list of **steps** for a visit, **configured per channel/
  tenant** ([A1](decisions-and-assumptions.md#a1--per-tenant-customization-config-driven-moderate)).
  Step types include: *audit*, *order*, *survey*, *task/checklist*, *photo*, *note*, *signature*.
- **Check-in / check-out** — the boundaries of a visit; check-in captures a **geo point**
  validated against the outlet **geofence**.
- **Visit outcome** — productive / non-productive (+ reason), e.g. "ordered", "no order — store
  closed".
- **Not-visited** — a planned visit the rep couldn't do (reason captured; no check-in).

## 4. Capabilities & flows

### F1 · Check in
1. Rep opens a planned (or unplanned) outlet.
2. App captures location; validates against the outlet **geofence** (radius, [OUT-08](12-outlets-master-data.md)).
3. If outside the geofence → allowed with an **override reason** (flagged for the supervisor).
4. Visit starts; timer + step list appear.

### F2 · Work the steps
- The rep progresses through the configured steps. Steps can be **mandatory** (must complete to
  check out) or optional. Audit/Order/Survey steps open the respective sub-flows.

### F3 · Check out
1. Rep completes mandatory steps; sets the **outcome**.
2. App stamps check-out time (→ time-on-site); the visit is sealed locally and queued for sync.

### F4 · Not-visited
- From the journey, the rep marks an outlet not-visited with a reason (no check-in occurs).

## 5. Business rules

- **BR-VIS-1** A visit belongs to exactly one outlet and one rep; its children (audit/order)
  belong to it.
- **BR-VIS-2** Check-in captures a geo point; outside-geofence requires an **override reason**
  (visit still allowed — never block the rep, always record).
  > 📝 ASSUMPTION: **remote-capable visit types skip the override.** A phone call, a video
  > conference or a head-office meeting is legitimately not at the outlet, so demanding an override
  > reason records an exception where nothing exceptional happened — and a flag that fires on
  > ordinary work is a flag supervisors learn to ignore. The visit type should carry whether presence
  > is expected, configured per channel through `IVisitWorkflow` (Configuration module). Not built:
  > `VIS-01`/`VIS-02` are Phase 2 and the Configuration module lands first.
  >
  > This is also the correct home for the question a per-tenant "geo validation" flag briefly tried
  > to answer in the Outlets module (#56, reverted): whether coordinates are *valid* is data
  > integrity and never optional; whether a rep must be *at* them is policy, and it belongs here.
- **BR-VIS-3** All **mandatory** steps must be complete before check-out.
- **BR-VIS-4** A visit, once **checked out**, is **sealed** — device-owned, append-only, and
  **not editable after sync** (mirrors the order rule; keeps sync conflict-free — [B7](decisions-and-assumptions.md#b7--conflict-resolution-matrix)).
- **BR-VIS-5** Time-on-site = checkout − checkin; abnormally short/long visits are flagged for
  reporting, not blocked.
- **BR-VIS-6** Every visit carries the **snapshot version** of reference data it was executed
  against (for audit/repricing traceability).

## 6. Requirements

| ID | Requirement | MoSCoW | Phase |
|---|---|---|---|
| VIS-01 | Check-in with geo capture + geofence validation | Must | 2 |
| VIS-02 | Outside-geofence override with reason | Must | 2 |
| VIS-03 | Configurable per-channel step workflow | Must | 2 |
| VIS-04 | Mandatory-step gating on check-out | Must | 2 |
| VIS-05 | Check-out with outcome + time-on-site + **check-out geo-stamp** (single point, a cheap duration-fraud counter; still two points, not a trail — consistent with the GDPR posture) | Must | 2 |
| VIS-06 | Notes & photos as visit steps | Should | 2 |
| VIS-07 | Not-visited handling (from Journey) | Must | 2 |
| VIS-08 | Signature capture step | Could | 3 |
| VIS-09 | Visit summary screen (recap before checkout) | Should | 3 |
| VIS-10 | Supervisor visit review (durations, overrides, outcomes) | Should | 3 |

## 7. Offline behavior

**The whole visit is offline-first.** Check-in, all steps, and check-out execute against
on-device data with no network. The completed visit (and its audit/order children, photos) is
written to the **local outbox** and pushed idempotently on reconnect; the visit is
**device-owned and append-only**, so no server-side conflict arises ([B7](decisions-and-assumptions.md#b7--conflict-resolution-matrix)).
Geo capture uses the device sensors offline; geofence data is part of the synced outlet record.

## 8. Module contract (exposed to others)

- `IVisitContext` — the current/opened visit a step attaches to (used by Audit, Order).
- `IVisitQuery` — visits for an outlet/rep/day (reporting).
- `IVisitIngest` — apply a pushed visit through this module, used by **Sync** ([module boundaries §7](../architecture/10-module-boundaries.md#7-module-registry)).
- Consumes `IJourneyQuery`, `IOutletCatalog` (geofence), and `IVisitWorkflow`
  (Configuration — the config-driven step sequence, VIS-03).
- Publishes `VisitCompleted` (with children summary) → reporting/Sync. An **amended** child order
  (BR-ORD-9) re-emits a `VisitCompleted`-correction so reporting/strike-rate stay accurate.

## 9. Acceptance criteria (sample)

- A rep with no signal can check in (with geofence validation), complete mandatory audit+order
  steps, and check out; on reconnect the full visit lands server-side exactly once.
- Attempting check-out with an incomplete mandatory step is blocked with a clear prompt.

## 10. Open questions

- Can a rep run **two visits** to the same outlet in a day (e.g. redelivery)? (Assumed: yes,
  each a distinct visit.)
- Is a supervisor allowed to reopen/annotate a sealed visit server-side? (Assumed: annotate
  only, never edit rep data.)
