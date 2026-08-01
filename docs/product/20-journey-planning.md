# Functional Spec — Journey Planning

> **Module:** Journey · **Group:** Field · **Phase:** 2 · **Status:** ✅ Baseline
> **Depends on:** Outlets, Organization · **Consumed by:** Visit

## 1. Purpose

Journey Planning decides **where a rep goes and when**. From each outlet's **call frequency**
and the rep's **territory** and **working calendar**, it produces the rep's daily **journey** —
the ordered list of outlets to visit. It is the bridge between static master data and the live
in-store work. (Not a route optimizer — sequencing is by simple rules, not VRP; see
[non-goals](00-product-overview.md#6-scope--non-goals).) Rules drafted in
[B3](decisions-and-assumptions.md#b3--journey-generation).

## 2. Actors

| Actor | Interest |
|---|---|
| Sales Ops / Admin | Set call frequencies & cycle rules; generate/publish journeys |
| Supervisor | Adjust a rep's plan; monitor planned-vs-actual coverage |
| Field Rep | Receives today's journey; works it; adds unplanned visits |

## 3. Core concepts

- **Call frequency** — how often an outlet should be visited: *visits per cycle* over a **cycle
  length** (e.g. 1×/week). May derive from outlet segment.
- **Working calendar** — the rep's working days/hours, holidays, capacity (visits/day).
- **Journey plan** — the generated schedule mapping outlets → days for a rep over a period.
- **Journey (day)** — the ordered list of scheduled visits for one rep on one day.
- **Planned visit** — an entry on a journey (outlet + date + expected). Becomes an actual
  **Visit** when executed (Visit module).
- **Unplanned visit** — a visit the rep adds in the field (not on the plan).

## 4. Capabilities & flows

### F1 · Configure frequency & calendar
- Admin sets call frequency per outlet/segment and the rep's working calendar/capacity.

### F2 · Generate a journey plan
1. For the period, compute each outlet's required visits from its frequency.
2. Distribute visits across the rep's working days honoring capacity and territory
   ([B3](decisions-and-assumptions.md#b3--journey-generation)).
3. Sequence each day's outlets (by proximity/segment heuristic — not optimization).
4. Publish → the plan syncs to the rep's device.

### F3 · Adjust a plan
- Supervisor/admin can move, add, or drop planned visits; reps can **reschedule within the
  cycle** and add **unplanned** visits.

### F4 · Work the journey (on device)
- The rep sees today's ordered journey, opens each outlet into a **Visit**, and can:
  - mark a planned visit **not-visited** with a **reason** (can't delete it),
  - add an **unplanned** visit for an in-scope outlet.

## 5. Business rules

- **BR-JRN-1** Journeys are generated only for outlets in the rep's **active territory**
  ([A4](decisions-and-assumptions.md#a4--offline-data-scope-territory-scoped)).
- **BR-JRN-2** A planned visit cannot be **deleted** by a rep — only completed or marked
  **not-visited with reason** ([B3](decisions-and-assumptions.md#b3--journey-generation)).
- **BR-JRN-3** Daily scheduled visits must not exceed the rep's **capacity** (generation
  respects it; manual overrides warn).
- **BR-JRN-4** Rescheduling is allowed **within the cycle**; moving outside the cycle requires
  supervisor action.
- **BR-JRN-5** Closed/inactive outlets are excluded from new plans (BR-OUT-4).
- **BR-JRN-6** Frequency compliance (did the outlet get its required visits this cycle?) is a
  reportable metric.

## 6. Requirements

| ID | Requirement | MoSCoW | Phase |
|---|---|---|---|
| JRN-01 | Configure call frequency per outlet/segment; cycle length | Must | 2 |
| JRN-02 | Configure rep working calendar + daily capacity | Must | 2 |
| JRN-03 | Generate a journey plan from frequency + territory + calendar | Must | 2 |
| JRN-04 | Publish plan; sync today's journey to device | Must | 2 |
| JRN-05 | Rep works ordered journey; opens outlets into visits | Must | 2 |
| JRN-06 | Not-visited with reason; add unplanned visit | Must | 2 |
| JRN-07 | Supervisor plan adjustments | Should | 2 |
| JRN-08 | Reschedule within cycle | Should | 3 |
| JRN-09 | Proximity/segment day-sequencing heuristic | Should | 3 |
| JRN-10 | Frequency-compliance / coverage metric | Should | 3 |

## 7. Offline behavior

The rep's **journey is pulled to the device** (today + a short horizon) and worked fully
offline. Rep-side journey changes are **device-owned transactional data** — *not-visited*
reasons, *unplanned* visits, and reschedules are captured locally and pushed via the outbox
([B7](decisions-and-assumptions.md#b7--conflict-resolution-matrix)). Plan *generation* is a
server/back-office activity; the rep receives the result.

## 8. Module contract (exposed to others)

- `IJourneyQuery` — today's/period journey for a rep; the planned visit for `(rep, outlet, day)`.
- `IReferenceChangeFeed` (sync source) — territory-scoped, row-version delta of the rep's journey
  with tombstones, for **Sync** ([module boundaries §7](../architecture/10-module-boundaries.md#7-module-registry)).
- `IJourneyIngest` — apply pushed journey annotations (not-visited reason, unplanned visit,
  reschedule) through this module, used by **Sync** ([§7](#7-offline-behavior)).
- Consumes `IOutletCatalog`, `ITerritoryDirectory`, `IRepScope`.
- Publishes `JourneyPublished` → Sync; `PlannedVisitMarkedNotVisited` → reporting.

## 9. Acceptance criteria (sample)

- Generating a plan for a weekly-frequency territory schedules each outlet its required number
  of times without exceeding daily capacity.
- A rep offline can mark an outlet not-visited with a reason and add an unplanned visit; both
  appear server-side after sync.

## 10. Open questions

- Day-sequencing: proximity heuristic vs. leave manual in v1? (Assumed: simple proximity.)
- Should frequency derive automatically from segment, or be set explicitly? (Assumed: segment
  default, overridable.)
